using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Adapters;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace DnSpy.MCP.Verify;

/// <summary>
/// Out-of-solution verification harness. Drives the real Core tool classes through the real
/// <see cref="ToolRegistry"/> (so reflection argument binding is exercised too) against a real
/// assembly, and asserts the token-first contract on the emitted JSON. No child process, no
/// test host — see the csproj comment for why that matters.
/// </summary>
internal static class Program {
    static int _passed;
    static readonly List<string> _failures = new();

    static int Main(string[] args) {
        // "server" selects the transport harness (real TCP, in-process); anything else is the
        // tool-contract harness. Keeping them separate lets CI run either alone.
        if (args.Length > 0 && string.Equals(args[0], "server", StringComparison.OrdinalIgnoreCase))
            return Server.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();

        // "toolcount [extensions]" prints what the REAL registry discovers, so
        // scripts/verify-tool-count.ps1 can compare a reflection count against the documented
        // number instead of a directory scan that can miss a wrapped [Description] attribute.
        if (args.Length > 0 && string.Equals(args[0], "tools", StringComparison.OrdinalIgnoreCase))
            return PrintToolNames(args.Length > 1 && string.Equals(args[1], "extensions", StringComparison.OrdinalIgnoreCase));

        // "xunit <tests.dll> [--e2e] [--temp <dir>]" runs the real xUnit suite IN THIS PROCESS, for
        // environments where `dotnet test` cannot start its testhost. See XunitRunner for why.
        if (args.Length > 1 && string.Equals(args[0], "xunit", StringComparison.OrdinalIgnoreCase)) {
            var includeE2E = args.Skip(2).Any(a => string.Equals(a, "--e2e", StringComparison.OrdinalIgnoreCase));
            string? tempDirectory = null;
            string? baseDirectory = null;
            for (int i = 2; i < args.Length - 1; i++) {
                if (string.Equals(args[i], "--temp", StringComparison.OrdinalIgnoreCase))
                    tempDirectory = args[i + 1];
                if (string.Equals(args[i], "--base-directory", StringComparison.OrdinalIgnoreCase))
                    baseDirectory = args[i + 1];
            }
            return XunitRunner.Run(args[1], includeE2E, tempDirectory, baseDirectory);
        }

        if (args.Length == 0) {
            Console.Error.WriteLine("usage: dnspy-mcp-verify <assembly.dll> [outputDirectory]");
            Console.Error.WriteLine("       dnspy-mcp-verify server <assembly.dll> [outputDirectory]");
            Console.Error.WriteLine("       dnspy-mcp-verify tools [extensions]");
            Console.Error.WriteLine("       dnspy-mcp-verify xunit <tests.dll> [--e2e] [--temp <dir>] [--base-directory <dir>]");
            return 2;
        }

        var assemblyPath = Path.GetFullPath(args[0]);
        if (!File.Exists(assemblyPath)) {
            Console.Error.WriteLine($"assembly not found: {assemblyPath}");
            return 2;
        }

        var outputDirectory = args.Length > 1
            ? Path.GetFullPath(args[1])
            // Default INSIDE the working tree, not the OS temp dir: the workspace re-dump writes a
            // whole project tree, and locked-down environments often deny writes to %TEMP%.
            : Path.Combine(Directory.GetCurrentDirectory(), "verify-output", Guid.NewGuid().ToString("N"));

        using var loader = new DnlibLoader();
        var load = loader.Load(assemblyPath);
        Check("load assembly", load.Success, load.Error ?? "");

        var decompiler = new DnSpyDecompilerSourceProvider(ReflectionDecompilerLoader.LoadCSharp());
        var ctx = new McpContext(loader, decompiler, new StubUi(), new StubLog(), new StubRefresh());
        var registry = new ToolRegistry(ctx, typeof(dnSpy.MCP.Core.Helpers.MetadataIdentity).Assembly);

        var tools = registry.ListTools();
        Console.WriteLine($"  discovered {tools.Count} tools"); // TOOLDUMP
        foreach (var t in tools) Console.WriteLine("  tool " + System.Text.Json.JsonSerializer.SerializeToNode(t)!["name"]!.GetValue<string>());

        // ---- 1. discovery returns canonical identities -----------------------------
        var listTypes = Invoke(registry, "assembly_list_types");
        Check("assembly_list_types ok", IsOk(listTypes));
        var typeItems = listTypes!["data"]!["items"]!.AsArray();
        Check("assembly_list_types returned types", typeItems.Count > 0);
        CheckIdentity("assembly_list_types item", typeItems[0]!.AsObject(), "Type");

        // ---- 2. method discovery + id round-trip ----------------------------------
        var methods = FindMethodTokens(ctx, out var withCalls, out var withFields);
        Check("found methods with bodies", methods.Count > 0);
        var probe = withCalls.FirstOrDefault();
        Check("found a method that calls something", probe != default(MethodDef));
        var fieldProbe = withFields.FirstOrDefault();

        // ---- 3. get_method_context: the graph-ingestion primitive -----------------
        var methodToken = TokenParser.Format(probe.MDToken.Raw);
        var context = Invoke(registry, "get_method_context", new JsonObject {
            ["token"] = methodToken,
            ["moduleMvid"] = MetadataIdentity.Mvid(probe.Module),
        });
        Check("get_method_context ok", IsOk(context), ErrorOf(context));
        CheckIdentity("get_method_context target", context!["target"]!.AsObject(), "Method");
        var contextData = context!["data"]!.AsObject();
        Check("parentId present", contextData["parentId"] is not null);
        Check("parentId equals declaring type token",
            contextData["parentId"]!.GetValue<string>() == TokenParser.Format(probe.DeclaringType!.MDToken.Raw),
            $"{contextData["parentId"]} vs 0x{probe.DeclaringType.MDToken.Raw:X8}");
        Check("parentType id matches parentId",
            contextData["parentType"]!["id"]!.GetValue<string>() == contextData["parentId"]!.GetValue<string>());
        Check("raw IL present", contextData["ilHex"] is not null && contextData["ilHex"]!.GetValue<string>().Length > 0);
        Check("decompiled C# present", contextData["sourceAvailable"]!.GetValue<bool>()
            && contextData["source"]!.GetValue<string>().Length > 0, contextData["sourceError"]?.GetValue<string>() ?? "");
        Check("il instruction list", contextData["il"]!.AsArray().Count > 0);

        var references = contextData["references"]!.AsArray();
        Check("references present", references.Count > 0);
        var referenceIds = new HashSet<string>();
        for (int i = 0; i < references.Count; i++) {
            var reference = references[i]!.AsObject();
            CheckIdentity($"reference[{i}]", reference, null);
            referenceIds.Add(reference["id"]!.GetValue<string>());
            Check($"reference[{i}] has referenceType", reference["referenceType"] is not null);
            Check($"reference[{i}] has resolved flag", reference["resolved"] is not null);
        }
        Check("references are deduplicated", referenceIds.Count == references.Count,
            $"{referenceIds.Count} distinct vs {references.Count} entries");

        // The graph edge the supervisor needs: the call target's token must appear in the set.
        var callOperand = probe.Body!.Instructions
            .First(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
            .Operand as IMethod;
        var callTargetToken = callOperand!.ResolveMethodDef() is MethodDef callDef
            ? callDef.MDToken.Raw
            : callOperand.MDToken.Raw;
        Check("call target token is in the reference set",
            referenceIds.Contains(TokenParser.Format(callTargetToken)),
            $"looking for {TokenParser.Format(callTargetToken)} among {string.Join(",", referenceIds.Take(5))}");

        // ---- 4. trimming flags shrink the payload --------------------------------
        var trimmed = Invoke(registry, "get_method_context", new JsonObject {
            ["token"] = methodToken,
            ["moduleMvid"] = MetadataIdentity.Mvid(probe.Module),
            ["includeIl"] = false,
            ["includeSource"] = false,
            ["includeReferences"] = false,
        });
        Check("get_method_context trimmed ok", IsOk(trimmed));
        var trimmedData = trimmed!["data"]!.AsObject();
        Check("trimmed omits IL", trimmedData["il"] is null);
        Check("trimmed omits source", trimmedData["source"] is null);
        Check("trimmed omits references", trimmedData["references"] is null);
        Check("trimmed keeps parentId", trimmedData["parentId"] is not null);

        // ---- 5. reference limit is honoured --------------------------------------
        var limited = Invoke(registry, "get_method_context", new JsonObject {
            ["token"] = methodToken,
            ["moduleMvid"] = MetadataIdentity.Mvid(probe.Module),
            ["includeSource"] = false,
            ["includeIl"] = false,
            ["maxReferences"] = 1,
        });
        Check("maxReferences honoured",
            limited!["data"]!["references"]!.AsArray().Count <= 1,
            limited!["data"]!["references"]!.AsArray().Count.ToString());

        // ---- 6. ambiguity is an error, never a silent first-hit ------------------
        var unqualified = Invoke(registry, "get_method_context", new JsonObject { ["token"] = methodToken });
        Check("module-qualified resolution still works without mvid when unambiguous", IsOk(unqualified) || ErrorOf(unqualified).Contains("ambiguous"));

        // ---- 7. string addressing is rejected ------------------------------------
        var byName = Invoke(registry, "decompile_method", new JsonObject { ["token"] = probe.FullName });
        Check("name addressing rejected", !IsOk(byName), "a full name was accepted as a token!");
        Check("rejection explains the token model",
            ErrorOf(byName).Contains("metadata token", StringComparison.OrdinalIgnoreCase),
            ErrorOf(byName));

        var badName = Invoke(registry, "rename_method", new JsonObject {
            ["token"] = methodToken,
            ["newName"] = "Obfuscated.Thing",
            ["dryRun"] = true,
        });
        Check("dotted rename rejected", !IsOk(badName), ErrorOf(badName));

        // ---- 8. dry runs never mutate --------------------------------------------
        var dryProbe = withFields.FirstOrDefault();
        if (dryProbe is not null) {
            var before = dryProbe.Name.String;
            var dry = Invoke(registry, "rename_symbol", new JsonObject {
                ["token"] = TokenParser.Format(dryProbe.MDToken.Raw),
                ["moduleMvid"] = MetadataIdentity.Mvid(dryProbe.Module),
                ["newName"] = "verified_renamed",
                ["dryRun"] = true,
            });
            Check("rename_symbol dry run ok", IsOk(dry), ErrorOf(dry));
            Check("dry run reports the reference closure size", dry!["data"]!["referenceSitesToUpdate"] is not null);
            Check("dry run did not mutate", dryProbe.Name.String == before, $"{before} → {dryProbe.Name.String}");
            CheckIdentity("rename_symbol dry target", dry!["target"]!.AsObject(), "Field");
        }

        // ---- 9. rename propagates into reference rows and keeps the token --------
        // Pick a field that some instruction actually references, otherwise the propagation
        // assertion below would be vacuous (a field nothing uses has nothing to propagate).
        var referencedField = FindReferencedField(ctx);
        Check("found a field with a reference site", referencedField is not null);
        if (referencedField is not null) {
            var fieldToken = referencedField.MDToken.Raw;
            var fieldMvid = MetadataIdentity.Mvid(referencedField.Module);
            var referencing = FindReferenceToField(ctx, referencedField);
            Console.WriteLine($"  info target field {TokenParser.Format(fieldToken)} mvid={fieldMvid} in {referencedField.DeclaringType?.FullName}::{referencedField.Name}");
            Console.WriteLine($"  info operator shape = {referencing.Operand?.GetType().Name}");

            // Dry run on the SAME field first: the count it predicts must be the count the write
            // reports, because both go through SymbolRenamer.PlanOrApplyReferences.
            var dryRun = Invoke(registry, "rename_symbol", new JsonObject {
                ["token"] = TokenParser.Format(fieldToken),
                ["moduleMvid"] = fieldMvid,
                ["newName"] = "mcp_verify_field",
                ["mode"] = "set",
                ["dryRun"] = true,
            });
            Check("rename_symbol dry run ok", IsOk(dryRun), ErrorOf(dryRun));
            var dryRunSiteCount = dryRun!["data"]!["referenceSitesToUpdate"]!.GetValue<int>();
            Check("dry run sees the reference closure", dryRunSiteCount > 0, dryRunSiteCount.ToString());
            Check("dry run did not mutate this field", referencedField.Name.String != "mcp_verify_field");

            var renamed = Invoke(registry, "rename_symbol", new JsonObject {
                ["token"] = TokenParser.Format(fieldToken),
                ["moduleMvid"] = fieldMvid,
                ["newName"] = "mcp_verify_field",
                ["mode"] = "set",
                ["dryRun"] = false,
            });
            Check("rename_symbol applied", IsOk(renamed), ErrorOf(renamed));
            Check("token unchanged after rename",
                renamed!["target"]!["id"]!.GetValue<string>() == TokenParser.Format(fieldToken));
            Check("current_name updated",
                renamed!["target"]!["current_name"]!.GetValue<string>() == "mcp_verify_field");
            Check("name field agrees with current_name",
                renamed!["target"]!["name"]!.GetValue<string>() == "mcp_verify_field");
            Check("nameIsMutable flagged", renamed!["target"]!["nameIsMutable"]!.GetValue<bool>());
            Check("idIsImmutable flagged", renamed!["target"]!["idIsImmutable"]!.GetValue<bool>());
            Check("reference propagation is reported",
                renamed!["data"]!["referenceRowsUpdated"] is not null);
            Check("reference propagation verified", renamed!["data"]!["verified"]!.GetValue<bool>(),
                string.Join(" | ", renamed!["data"]!["warnings"]!.AsArray().Select(n => n!.GetValue<string>())));
            Check("definition renamed in metadata", referencedField.Name.String == "mcp_verify_field");
            // A site inside the same module points at the DEFINITION object, so no reference ROW
            // needs writing there; the closure is counted by referenceSitesUpdated. Asserting on
            // the row count alone would encode the wrong expectation.
            Check("rename covered at least one reference site",
                renamed!["data"]!["referenceSitesUpdated"]!.GetValue<int>() > 0,
                renamed!["data"]!["referenceSitesUpdated"]!.GetValue<int>().ToString());
            Check("dry run predicted the same site count it wrote",
                dryRunSiteCount == renamed!["data"]!["referenceSitesUpdated"]!.GetValue<int>(),
                $"dry={dryRunSiteCount} written={renamed!["data"]!["referenceSitesUpdated"]!.GetValue<int>()}");
            if (referencing.Operand is IField fieldRef)
                Check("reference row followed the rename",
                    fieldRef.Name?.String == "mcp_verify_field",
                    fieldRef.Name?.String ?? "null");
        }

        // ---- 10. namespace rename propagates TypeRefs ---------------------------
        var anchor = ctx.Resolver.GetAllModules()
            .SelectMany(m => m.GetTypes())
            .FirstOrDefault(t => !string.IsNullOrEmpty(t.Namespace?.String) && t.DeclaringType is null);
        if (anchor is not null) {
            var oldNamespace = anchor.Namespace!.String;
            var nsDry = Invoke(registry, "rename_namespace", new JsonObject {
                ["token"] = TokenParser.Format(anchor.MDToken.Raw),
                ["moduleMvid"] = MetadataIdentity.Mvid(anchor.Module),
                ["newNamespace"] = oldNamespace + ".verified",
                ["dryRun"] = true,
            });
            Check("rename_namespace dry run ok", IsOk(nsDry), ErrorOf(nsDry));
            Check("namespace dry run lists types", nsDry!["data"]!["types"]!.AsArray().Count > 0);
            Check("namespace dry run did not mutate", anchor.Namespace.String == oldNamespace);
        }

        // ---- 11. cross-reference tool still token-keyed -------------------------
        var xrefs = Invoke(registry, "get_xrefs_to", new JsonObject {
            ["token"] = methodToken,
            ["moduleMvid"] = MetadataIdentity.Mvid(probe.Module),
        });
        Check("get_xrefs_to ok", IsOk(xrefs), ErrorOf(xrefs));
        CheckIdentity("get_xrefs_to target", xrefs!["target"]!.AsObject(), "Method");

        // ---- 12. workspace export plan ------------------------------------------
        var plan = Invoke(registry, "workspace_export_plan");
        Check("workspace_export_plan ok", IsOk(plan), ErrorOf(plan));
        Check("plan reports modules", plan!["data"]!["moduleCount"]!.GetValue<int>() > 0);
        Check("plan reports top-level types", plan!["data"]!["topLevelTypeCount"]!.GetValue<int>() > 0);

        // ---- 13. workspace save-code --------------------------------------------
        var save = Invoke(registry, "workspace_save_code", new JsonObject {
            ["outputDirectory"] = outputDirectory,
            ["solutionName"] = "verify-workspace",
            ["overwrite"] = true,
        });
        Check("workspace_save_code ok", IsOk(save), ErrorOf(save));
        if (IsOk(save)) {
            var saveData = save!["data"]!.AsObject();
            var manifest = saveData["tokenManifest"]!.GetValue<string>();
            Check("token manifest written", File.Exists(manifest), manifest);
            var projectFiles = Directory.GetFiles(outputDirectory, "*.csproj", SearchOption.AllDirectories);
            Check("a .csproj was emitted", projectFiles.Length > 0);
            var csFiles = Directory.GetFiles(outputDirectory, "*.cs", SearchOption.AllDirectories);
            Check("C# source files emitted", csFiles.Length > 0, $"{csFiles.Length}");
            Check("type count matches files",
                saveData["typeCount"]!.GetValue<int>() == csFiles.Length,
                $"{saveData["typeCount"]} vs {csFiles.Length}");

            // The manifest is the machine-readable contract: every entry must carry the
            // canonical identity triple and point at a file that exists.
            var manifestNode = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
            Check("manifest schema", manifestNode["schema"]!.GetValue<string>() == "dnspy-mcp.workspace-tokens/1");
            Check("manifest primaryId is id", manifestNode["primaryId"]!.GetValue<string>() == "id");
            var moduleArray = manifestNode["modules"]!.AsArray();
            Check("manifest has modules", moduleArray.Count > 0);
            var manifestEntryCount = 0;
            var manifestMissingFiles = 0;
            for (int i = 0; i < moduleArray.Count; i++) {
                var types = moduleArray[i]!["types"]!.AsArray();
                for (int j = 0; j < types.Count; j++) {
                    var entry = types[j]!.AsObject();
                    CheckIdentity($"manifest type[{i}][{j}]", entry, "Type");
                    manifestEntryCount++;
                    var relative = entry["file"]!.GetValue<string>();
                    if (!File.Exists(Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar))))
                        manifestMissingFiles++;
                }
            }
            Check("manifest entries present", manifestEntryCount > 0);
            Check("every manifest entry resolves to a file", manifestMissingFiles == 0, $"{manifestMissingFiles} missing");

            // A token looked up by id must name the same file in decompile_type and the manifest.
            var firstManifestType = moduleArray[0]!["types"]!.AsArray()[0]!.AsObject();
            var manifestId = firstManifestType["id"]!.GetValue<string>();
            var decompiled = Invoke(registry, "decompile_type", new JsonObject {
                ["token"] = manifestId,
                ["moduleMvid"] = moduleArray[0]!["moduleMvid"]!.GetValue<string>(),
            });
            Check("decompile_type accepts a manifest id", IsOk(decompiled), ErrorOf(decompiled));
            Check("decompile_type id matches", decompiled!["target"]!["id"]!.GetValue<string>() == manifestId);

            // Re-running must merge into the existing directory rather than fail.
            var second = Invoke(registry, "workspace_save_code", new JsonObject {
                ["outputDirectory"] = outputDirectory,
                ["overwrite"] = true,
            });
            Check("workspace_save_code is idempotent", IsOk(second), ErrorOf(second));
        }

        // ---- 14. no string-addressing parameter exists on any tool --------------
        CheckNoStringAddressing(registry);

        Console.WriteLine();
        Console.WriteLine($"{_passed} check(s) passed, {_failures.Count} failed.");
        foreach (var failure in _failures)
            Console.WriteLine("  FAIL " + failure);

        try { Directory.Delete(outputDirectory, recursive: true); } catch { }

        return _failures.Count == 0 ? 0 : 1;
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// Prints the tool NAMES the real <see cref="ToolRegistry"/> discovers, one per line, and
    /// nothing else on stdout, so a script can compare them against its own scan.
    /// </summary>
    /// <remarks>
    /// Names, not a count: a count-only check passes whenever two errors cancel out (a tool
    /// missed and a tool double-counted), and it cannot tell CI WHICH tool drifted.
    /// </remarks>
    static int PrintToolNames(bool includeExtension) {
        // Core only. The Extension assembly is deliberately NOT loaded here: it is a WPF/MEF
        // assembly (UseWPF), this harness is a plain console target so WPF is not deployed
        // beside it, and loading the Extension would drag in dnSpy's VS.Text/WPF surface —
        // inflating this tool's dependency footprint, which is exactly what Core avoids.
        // scripts/verify-tool-count.ps1 therefore counts Core tools from THIS registry and
        // Extension-only tools from its own scan of the Extension Tools directory. Each half is
        // measured with the instrument that can actually see it, and the sum is compared to the
        // number CLAUDE.md advertises.
        if (includeExtension) {
            Console.Error.WriteLine("  note: this harness cannot load the WPF Extension assembly; " +
                "use scripts/verify-tool-count.ps1, which counts Extension tools from source.");
        }

        var loader = new DnlibLoader();
        var context = new McpContext(
            loader,
            new DnSpyDecompilerSourceProvider(ReflectionDecompilerLoader.LoadCSharp()),
            new StubUi(), new StubLog(), new StubRefresh());
        var registry = new ToolRegistry(context, typeof(MetadataIdentity).Assembly);

        var names = new List<string>();
        foreach (var tool in registry.ListTools()) {
            var node = System.Text.Json.JsonSerializer.SerializeToNode(tool)!.AsObject();
            names.Add(node["name"]!.GetValue<string>());
        }
        names.Sort(StringComparer.Ordinal);
        foreach (var name in names)
            Console.WriteLine(name);

        loader.Dispose();
        return 0;
    }

    /// <summary>
    /// Fails when any discovered tool exposes a NAME-shaped input parameter.
    /// </summary>
    static void CheckNoStringAddressing(ToolRegistry registry) {
        // The token model is only real if no tool can be addressed by a name/signature. This
        // asserts the invariant on the discovered schema, so a future parameter named
        // typeName/methodFullName/className fails the harness instead of silently
        // reintroducing string routing.
        string[] forbidden = {
            "typename", "methodname", "classname", "membername", "fullname",
            "methodfullname", "typefullname", "signature", "resourcename", "identifier",
        };
        var offenders = new List<string>();
        foreach (var tool in registry.ListTools()) {
            var node = System.Text.Json.JsonSerializer.SerializeToNode(tool)!.AsObject();
            var properties = node["inputSchema"]!["properties"]!.AsObject();
            foreach (var property in properties) {
                var name = property.Key.ToLowerInvariant().Replace("_", "");
                if (forbidden.Contains(name))
                    offenders.Add($"{node["name"]}::{property.Key}");
            }
        }
        Check("no tool exposes a name-based address parameter", offenders.Count == 0, string.Join(", ", offenders));
    }

    static List<MethodDef> FindMethodTokens(McpContext ctx, out List<MethodDef> withCalls, out List<FieldDef> withFields) {
        withCalls = new List<MethodDef>();
        withFields = new List<FieldDef>();
        var all = new List<MethodDef>();

        foreach (var module in ctx.Resolver.GetAllModules()) {
            foreach (var type in module.GetTypes()) {
                foreach (var method in type.Methods) {
                    if (method.Body is null)
                        continue;
                    all.Add(method);
                    if (method.Body.Instructions.Any(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
                        && method.Body.Instructions.Count > 3)
                        withCalls.Add(method);
                }
                foreach (var field in type.Fields) {
                    if (field.IsStatic || field.IsLiteral)
                        continue;
                    withFields.Add(field);
                }
            }
        }

        return all;
    }

    static Instruction FindReferenceToField(McpContext ctx, FieldDef field) {    var targetMvid = field.Module.Mvid;
        foreach (var module in ctx.Resolver.GetAllModules()) {
            foreach (var type in module.GetTypes()) {
                foreach (var method in type.Methods) {
                    if (method.Body is null)
                        continue;
                    foreach (var instr in method.Body.Instructions) {
                        if (instr.Operand is not IField fieldRef)
                            continue;
                        if (fieldRef.MDToken.Raw != field.MDToken.Raw)
                            continue;
                        try {
                            var def = fieldRef.ResolveFieldDef();
                            if (def is not null && def.Module.Mvid == targetMvid)
                                return instr;
                        }
                        catch {
                            // unresolvable reference; keep scanning
                        }
                    }
                }
            }
        }
        return Instruction.Create(OpCodes.Nop);
    }

    /// <summary>
    /// Finds a field that some method body actually references, so the rename-propagation
    /// assertion has a real reference row to check.
    /// </summary>
    /// <remarks>
    /// Candidates are keyed on (moduleMvid, token), NOT on the token alone. A bare token number
    /// is not an address: 0x04000005 exists in every assembly, so token-only matching happily
    /// pairs this assembly's field with an unrelated field of the same number from
    /// System.Private.CoreLib — the exact collision the token-first model exists to eliminate.
    /// </remarks>
    static FieldDef? FindReferencedField(McpContext ctx) {
        var candidates = new HashSet<(string Mvid, uint Token)>();
        foreach (var module in ctx.Resolver.GetAllModules()) {
            foreach (var type in module.GetTypes()) {
                foreach (var method in type.Methods) {
                    if (method.Body is null)
                        continue;
                    foreach (var instr in method.Body.Instructions) {
                        if (instr.Operand is not IField fieldRef)
                            continue;
                        try {
                            var def = fieldRef.ResolveFieldDef();
                            if (def is not null)
                                candidates.Add((MetadataIdentity.Mvid(def.Module), def.MDToken.Raw));
                        }
                        catch {
                            // unresolvable reference; skip
                        }
                    }
                }
            }
        }

        foreach (var module in ctx.Resolver.GetAllModules()) {
            var mvid = MetadataIdentity.Mvid(module);
            foreach (var type in module.GetTypes()) {
                foreach (var field in type.Fields) {
                    if (candidates.Contains((mvid, field.MDToken.Raw)))
                        return field;
                }
            }
        }
        return null;
    }

    static JsonObject? Invoke(ToolRegistry registry, string toolName, JsonObject? arguments = null) {
        var tool = registry.GetTool(toolName);
        if (tool is null) {
            Check($"tool '{toolName}' is registered", false);
            return null;
        }
        var raw = tool.Invoke(arguments);
        try {
            return JsonNode.Parse(raw)!.AsObject();
        }
        catch (Exception ex) {
            Check($"tool '{toolName}' returned valid JSON", false, ex.Message);
            return null;
        }
    }

    static bool IsOk(JsonObject? response) => response is not null && response["ok"]!.GetValue<bool>();

    static string ErrorOf(JsonObject? response) =>
        response?["error"]?.GetValue<string>() ?? "";

    /// <summary>
    /// Asserts the canonical identity schema on an emitted block: <c>id</c> is a canonical hex
    /// token, <c>type</c> is present, <c>current_name</c> is present and equal to <c>name</c>,
    /// and the immutable/mutable flags agree.
    /// </summary>
    static void CheckIdentity(string label, JsonObject block, string? expectedType) {
        var idNode = block["id"];
        Check($"{label}: has id", idNode is not null);
        if (idNode is not null) {
            var id = idNode.GetValue<string>();
            Check($"{label}: id is canonical hex", id.Length == 10 && id.StartsWith("0x", StringComparison.Ordinal)
                && id.Skip(2).All(c => "0123456789ABCDEF".Contains(c)), id);
        }
        Check($"{label}: has type", block["type"] is not null);
        if (expectedType is not null)
            Check($"{label}: type == {expectedType}", block["type"]!.GetValue<string>() == expectedType,
                block["type"]?.GetValue<string>() ?? "null");
        Check($"{label}: has current_name", block["current_name"] is not null);
        if (block["name"] is not null && block["current_name"] is not null)
            Check($"{label}: name == current_name",
                block["name"]!.GetValue<string>() == block["current_name"]!.GetValue<string>());
        Check($"{label}: nameIsMutable", block["nameIsMutable"]!.GetValue<bool>());
        Check($"{label}: idIsImmutable", block["idIsImmutable"]!.GetValue<bool>());
        Check($"{label}: moduleMvid present", block["moduleMvid"] is not null);
        Check($"{label}: numeric token alias agrees",
            block["token"] is null || block["tokenHex"]!.GetValue<string>() == block["id"]!.GetValue<string>());
    }

    static void Check(string label, bool ok, string detail = "") {
        if (ok) {
            _passed++;
            Console.WriteLine($"  ok   {label}");
        }
        else {
            _failures.Add($"{label}{(detail.Length > 0 ? " — " + detail : "")}");
            Console.WriteLine($"  FAIL {label}{(detail.Length > 0 ? " — " + detail : "")}");
        }
    }

    // -------------------------------------------------------------------------
    // Minimal host adapters (the real ones live in the Extension / Headless hosts)
    // -------------------------------------------------------------------------

    sealed class DnlibLoader : IAssemblyLoader, IDisposable {
        readonly ModuleContext _moduleContext = ModuleDef.CreateModuleContext();
        readonly List<LoadedModule> _loaded = new();

        public LoadResult Load(string path) {
            if (!File.Exists(path))
                return new LoadResult(false, $"File not found: {path}", null);
            var module = ModuleDefMD.Load(path, _moduleContext);
            ((AssemblyResolver)_moduleContext.AssemblyResolver).AddToCache(module);
            var loaded = new LoadedModule(module.Name, module.Assembly?.Name?.String, module, path);
            _loaded.Add(loaded);
            return new LoadResult(true, null, loaded);
        }

        public int Close(string assemblyName) {
            var removed = _loaded.RemoveAll(m =>
                string.Equals(m.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase));
            return removed;
        }

        public IReadOnlyList<LoadedModule> GetDocuments() => _loaded.ToList();

        public void Dispose() {
            foreach (var loaded in _loaded)
                loaded.Module.Dispose();
        }
    }

    sealed class StubUi : IUIThreadScheduler {
        public T Invoke<T>(Func<T> action) => action();
        public void Invoke(Action action) => action();
    }

    sealed class StubLog : ILogSink {
        public void Info(string message) { }
        public void Warn(string message) => Console.WriteLine("  warn " + message);
        public void Error(string message, Exception? ex = null) => Console.WriteLine("  error " + message);
    }

    sealed class StubRefresh : ITreeRefreshNotifier {
        public void RefreshAll() { }
        public void NotifyNamespaceRenamed(string assembly, string oldNamespace, string newNamespace) { }
    }

    static class ReflectionDecompilerLoader {
        const string ILSpyCoreAssemblyName = "dnSpy.Decompiler.ILSpy.Core";

        public static IDecompiler LoadCSharp() {
            var asm = Assembly.Load(ILSpyCoreAssemblyName);
            var languages = new List<IDecompiler>();
            foreach (var type in asm.GetTypes()) {
                if (type.IsAbstract || type.IsInterface || !typeof(IDecompilerProvider).IsAssignableFrom(type))
                    continue;
                IDecompilerProvider provider;
                try { provider = (IDecompilerProvider)Activator.CreateInstance(type)!; }
                catch { continue; }
                languages.AddRange(provider.Create());
            }

            return languages.FirstOrDefault(d => d.GenericGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY
                    || d.UniqueGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY)
                ?? languages.FirstOrDefault(d => d.GenericGuid == DecompilerConstants.LANGUAGE_CSHARP
                    || d.UniqueGuid == DecompilerConstants.LANGUAGE_CSHARP)
                ?? throw new InvalidOperationException("No C# decompiler available.");
        }
    }
}
