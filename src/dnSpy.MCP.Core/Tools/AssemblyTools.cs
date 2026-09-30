using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    /// <summary>
    /// Assembly/module management and inventory.
    /// </summary>
    /// <remarks>
    /// A module's immutable identity is its MVID (a module has no useful metadata
    /// token of its own beyond the fixed Module row 0x00000001), and the assembly
    /// simple name is treated as mutable metadata — it is the human handle used to
    /// load/close/scope, never an element address. Every element discovered here
    /// (types, namespaces, references) is reported with its metadata token.
    /// </remarks>
    public sealed class AssemblyTools {
        const string ToolLoadAssembly = "load_assembly";
        const string ToolCloseAssembly = "close_assembly";
        const string ToolListLoadedAssemblies = "list_loaded_assemblies";
        const string ToolAssemblyOverview = "assembly_overview";
        const string ToolAssemblyListNamespaces = "assembly_list_namespaces";
        const string ToolAssemblyListTypes = "assembly_list_types";
        const string ToolAssemblyGetReferences = "assembly_get_references";

        private readonly McpContext _ctx;
        public AssemblyTools(McpContext ctx) => _ctx = ctx;

        [Description("Load a .NET DLL/EXE into dnSpy by absolute path. Returns the module identity (moduleMvid + assembly name) and type count on success. Use list_loaded_assemblies to verify.")]
        public string LoadAssembly(
            [Description("Absolute path to the DLL or EXE file")] string path) {

            if (string.IsNullOrWhiteSpace(path))
                return ToolResponse.Failure(ToolLoadAssembly, "path is required.");

            if (!File.Exists(path))
                return ToolResponse.Failure(ToolLoadAssembly, $"File not found: {path}");

            try {
                var result = _ctx.AssemblyLoader.Load(path);
                if (!result.Success)
                    return ToolResponse.Failure(ToolLoadAssembly, result.Error ?? "Unknown load error.");

                var loaded = result.Module!;
                if (loaded.Module is ModuleDef mod) {
                    var typeCount = mod.GetTypes().Count();
                    _ctx.Log.Info($"Loaded assembly: {MetadataIdentity.AssemblyName(mod)} ({typeCount} types) from {path}");

                    var identity = MetadataIdentity.ForModule(mod);
                    return ToolResponse.Success(ToolLoadAssembly, identity, new JsonObject {
                        ["typeCount"] = typeCount,
                        ["path"] = mod.Location ?? path,
                    });
                }

                return ToolResponse.Success(ToolLoadAssembly, new JsonObject {
                    ["message"] = $"Loaded (non-CLR or no module): {path}",
                    ["typeCount"] = 0,
                });
            }
            catch (Exception ex) {
                _ctx.Log.Error($"LoadAssembly failed: {path}: {ex.Message}");
                return ToolResponse.Failure(ToolLoadAssembly, $"Error loading '{path}': {ex.Message}");
            }
        }

        [Description("Unload (close) an assembly by its simple name (case-insensitive), e.g. 'MyAssembly'. Assemblies are file-level containers identified by moduleMvid, not metadata-token elements, so the simple name is the handle here. Returns how many documents were removed plus their module identities.")]
        public string CloseAssembly(
            [Description("Assembly simple name to remove (case-insensitive)")] string assemblyName) {

            if (string.IsNullOrWhiteSpace(assemblyName))
                return ToolResponse.Failure(ToolCloseAssembly, "assemblyName is required.");

            // Snapshot matching identities before removal so the payload can still
            // describe them once the underlying documents are gone.
            var closing = new List<JsonObject>();
            foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                if (loaded.Module is not ModuleDef mod) continue;
                var name = MetadataIdentity.AssemblyName(mod);
                if (string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase))
                    closing.Add(MetadataIdentity.ForModule(mod));
            }

            if (closing.Count == 0)
                return ToolResponse.Failure(ToolCloseAssembly,
                    $"No loaded assembly named '{assemblyName}'. Use list_loaded_assemblies to see what's loaded.");

            try {
                var removed = _ctx.AssemblyLoader.Close(assemblyName);
                _ctx.Log.Info($"Closed assembly '{assemblyName}' ({removed} document(s))");
                return ToolResponse.Success(ToolCloseAssembly, new JsonObject {
                    ["assemblyName"] = assemblyName,
                    ["closedCount"] = removed,
                    ["items"] = ToolResponse.Array(closing),
                });
            }
            catch (Exception ex) {
                _ctx.Log.Error($"CloseAssembly failed: {assemblyName}: {ex.Message}");
                return ToolResponse.Failure(ToolCloseAssembly, $"Error closing '{assemblyName}': {ex.Message}");
            }
        }

        [Description("List every binary currently loaded. Each entry carries the immutable module identity (moduleMvid) plus the mutable assembly/file names, type count, and path. Call this first to learn the MVIDs needed to qualify metadata tokens.")]
        public string ListLoadedAssemblies() {
            var docs = _ctx.AssemblyLoader.GetDocuments();
            if (docs.Count == 0)
                return ToolResponse.Failure(ToolListLoadedAssemblies, "No assemblies loaded.");

            var items = new List<JsonObject>();
            for (int i = 0; i < docs.Count; i++) {
                var loaded = docs[i];
                if (loaded.Module is not ModuleDef mod) continue;

                var identity = MetadataIdentity.ForModule(mod);
                identity["index"] = i;
                identity["typeCount"] = mod.GetTypes().Count();
                items.Add(identity);
            }

            return ToolResponse.Success(ToolListLoadedAssemblies, new JsonObject {
                ["count"] = items.Count,
                ["items"] = ToolResponse.Array(items),
            });
        }

        [Description("Overview of a loaded module: MVID, runtime version, entry point, type/namespace counts. Pass assemblyName to scope to one binary; otherwise the first loaded module is described. Element counts are accompanied by the module identity (moduleMvid).")]
        public string AssemblyOverview(
            [Description("Optional assembly simple name to scope to (e.g. 'PVService')")] string? assemblyName = null) {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolAssemblyOverview, "No assemblies loaded.");

            ModuleDef? module = null;
            if (string.IsNullOrEmpty(assemblyName)) {
                foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                    if (loaded.Module is ModuleDef mod) {
                        module = mod;
                        break;
                    }
                }
            }
            else {
                module = _ctx.Resolver.GetModules(assemblyName).FirstOrDefault();
            }

            if (module is null)
                return ToolResponse.Failure(ToolAssemblyOverview, string.IsNullOrEmpty(assemblyName)
                    ? "No assembly loaded. Please open an assembly in dnSpy."
                    : $"Assembly '{assemblyName}' not found. Use list_loaded_assemblies to see available assemblies.");

            var types = module.GetTypes().ToList();
            var result = new JsonObject {
                ["moduleVersionId"] = $"{module.Mvid:D}",
                ["runtimeVersion"] = module.RuntimeVersion ?? "",
                ["typeCount"] = types.Count,
                ["namespaceCount"] = types.Select(t => t.Namespace?.String ?? "").Where(ns => ns.Length > 0).Distinct().Count(),
                ["entryPointToken"] = module.EntryPoint is null ? null : module.EntryPoint.MDToken.Raw,
                ["entryPointTokenHex"] = module.EntryPoint is null ? null : TokenParser.Format(module.EntryPoint.MDToken.Raw),
                ["entryPoint"] = module.EntryPoint is null ? null : $"{module.EntryPoint.DeclaringType?.FullName}::{module.EntryPoint.Name}",
                ["assemblyFullName"] = module.Assembly?.FullName ?? "",
                ["references"] = ToolResponse.Array(module.GetAssemblyRefs().Select(r => MetadataIdentity.ForAssemblyRef(r, module))),
            };

            return ToolResponse.Success(ToolAssemblyOverview, MetadataIdentity.ForModule(module), result);
        }

        [Description("List namespaces with their metadata-token anchor. .NET namespaces have no metadata row of their own, so each namespace is identified by its ANCHOR TYPE: the lowest metadata token among the TypeDefs in that namespace — deterministic and stable for the loaded revision. Pass assemblyName to scope to one binary.")]
        public string AssemblyListNamespaces(
            [Description("Optional assembly simple name to scope to (e.g. 'Hinet.Web')")] string? assemblyName = null) {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolAssemblyListNamespaces, "No assemblies loaded.");

            var modules = ScopeModules(assemblyName);
            if (modules.Count == 0)
                return ToolResponse.Failure(ToolAssemblyListNamespaces, UnknownAssemblyMessage(assemblyName));

            var items = new List<JsonObject>();
            foreach (var module in modules) {
                var namespaces = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var type in module.GetTypes()) {
                    var ns = type.Namespace?.String;
                    if (!string.IsNullOrEmpty(ns))
                        namespaces.Add(ns);
                }

                foreach (var ns in namespaces) {
                    var item = MetadataIdentity.ForNamespace(module, ns);
                    item["typeCount"] = module.GetTypes().Count(t => string.Equals(t.Namespace?.String ?? "", ns, StringComparison.Ordinal));
                    items.Add(item);
                }
            }

            return ToolResponse.Success(ToolAssemblyListNamespaces, new JsonObject {
                ["scope"] = string.IsNullOrEmpty(assemblyName) ? "all loaded assemblies" : assemblyName,
                ["count"] = items.Count,
                ["items"] = ToolResponse.Array(items),
            });
        }

        [Description("List types with their metadata tokens. Optional regex pattern filters the full names; assemblyName scopes to one binary. Use this (or search_types) to obtain the tokens every other tool requires.")]
        public string AssemblyListTypes(
            [Description("Optional regex pattern to filter type full names")] string? pattern = null,
            [Description("Optional assembly simple name to scope to (e.g. 'Hinet.Web')")] string? assemblyName = null) {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolAssemblyListTypes, "No assemblies loaded.");

            var modules = ScopeModules(assemblyName);
            if (modules.Count == 0)
                return ToolResponse.Failure(ToolAssemblyListTypes, UnknownAssemblyMessage(assemblyName));

            var types = new List<JsonObject>();
            foreach (var module in modules) {
                foreach (var type in module.GetTypes()) {
                    var fullName = type.FullName?.ToString();
                    if (string.IsNullOrEmpty(fullName)) continue;

                    if (pattern is not null) {
                        try {
                            if (!System.Text.RegularExpressions.Regex.IsMatch(fullName, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
                                continue;
                        }
                        catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex) {
                            // Regex too expensive on this type name; treat as no-match but
                            // trace it so pathological patterns stay diagnosable.
                            _ctx.Log.Warn($"Regex timeout matching '{fullName}' against pattern '{pattern}': {ex.Message}");
                            continue;
                        }
                    }

                    types.Add(MetadataIdentity.ForType(type));
                }
            }

            if (types.Count == 0)
                return ToolResponse.Failure(ToolAssemblyListTypes, pattern is not null
                    ? $"No types match '{pattern}'."
                    : "No types found.");

            return ToolResponse.Success(ToolAssemblyListTypes, new JsonObject {
                ["pattern"] = pattern,
                ["scope"] = string.IsNullOrEmpty(assemblyName) ? "all loaded assemblies" : assemblyName,
                ["count"] = types.Count,
                ["items"] = ToolResponse.Array(types),
            });
        }

        [Description("List assembly references (DLLs, NuGet packages) with their metadata tokens, optionally scoped to one assembly by simple name.")]
        public string AssemblyGetReferences(
            [Description("Optional assembly simple name to scope to (e.g. 'Hinet.Web')")] string? assemblyName = null) {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolAssemblyGetReferences, "No assemblies loaded.");

            var modules = ScopeModules(assemblyName);
            if (modules.Count == 0)
                return ToolResponse.Failure(ToolAssemblyGetReferences, UnknownAssemblyMessage(assemblyName));

            var items = new List<JsonObject>();
            foreach (var module in modules) {
                foreach (var asmRef in module.GetAssemblyRefs()) {
                    var item = MetadataIdentity.ForAssemblyRef(asmRef, module);
                    item["referencedBy"] = MetadataIdentity.ForModule(module);
                    items.Add(item);
                }
            }

            return ToolResponse.Success(ToolAssemblyGetReferences, new JsonObject {
                ["scope"] = string.IsNullOrEmpty(assemblyName) ? "all loaded assemblies" : assemblyName,
                ["count"] = items.Count,
                ["items"] = ToolResponse.Array(items),
            });
        }

        /// <summary>
        /// Resolves modules for an optional assembly scope. Null/empty scope returns
        /// every loaded module. This is DISCOVERY scoping by mutable assembly name — it
        /// selects which binaries to enumerate, it never addresses an element.
        /// </summary>
        private List<ModuleDef> ScopeModules(string? assemblyName) {
            if (string.IsNullOrEmpty(assemblyName))
                return _ctx.AssemblyLoader.GetDocuments()
                    .Where(d => d.Module is ModuleDef)
                    .Select(d => d.Module)
                    .Cast<ModuleDef>()
                    .ToList();
            return _ctx.Resolver.GetModules(assemblyName).ToList();
        }

        private string UnknownAssemblyMessage(string? assemblyName) =>
            $"Assembly '{assemblyName}' not found. Loaded modules: {_ctx.Resolver.DescribeModules()}";
    }
}
