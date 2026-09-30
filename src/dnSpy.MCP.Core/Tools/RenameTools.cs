using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    /// <summary>
    /// Metadata mutations. Every rename is addressed by metadata token — the caller passes
    /// the <c>id</c> it got from a discovery tool, the element is located in the loaded
    /// assembly definitions by that token, the change is applied to the definition AND
    /// propagated to every reference row in the loaded workspace, and the updated payload
    /// (same id + new name) is broadcast back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token NEVER changes as a result of a rename (it is the identity); only the name
    /// field of the response moves. That is what makes a rename sequence idempotent: the
    /// same token can be renamed again, or used afterwards to decompile/inspect the element
    /// under its new name, without re-discovery.
    /// </para>
    /// <para>
    /// Name/signature matching ("rename every method called Foo", "partial match") is
    /// deliberately absent: on an obfuscated binary it renames the wrong elements. The macro
    /// form (<c>rename_symbol</c>) covers the legitimate bulk case and is itself anchored on
    /// a token.
    /// </para>
    /// </remarks>
    public sealed class RenameTools {
        const string ToolRenameClass = "rename_class";
        const string ToolRenameMethod = "rename_method";
        const string ToolRenameNamespace = "rename_namespace";
        const string ToolRenameSymbol = "rename_symbol";

        private readonly McpContext _ctx;
        public RenameTools(McpContext ctx) => _ctx = ctx;

        [Description("Rename ONE type, addressed by its metadata id/token (hex or decimal; discover it with search_types/assembly_list_types/get_type_members). Applies in dnSpy's live metadata AND propagates to every TypeRef/MemberRef in the loaded workspace so cross-assembly references stay bound to this token. dryRun=true (default) previews. On success the response broadcasts the updated identity: the SAME id with the NEW current_name.")]
        public string RenameClass(
            [Description("Metadata token ('id') of the TypeDef to rename, e.g. '0x02000001' or '33554433'")] string token,
            [Description("New simple type name (no namespace, no dots)")] string newName,
            [Description("Preview only, do not modify metadata")] bool dryRun = true,
            [Description("Propagate the rename to every reference row in every loaded module (keep true in the pipeline)")] bool includeReferences = true,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolRenameClass, "No assemblies loaded.");

            var nameError = ValidateSimpleName(newName, "newName");
            if (nameError is not null)
                return ToolResponse.Failure(ToolRenameClass, nameError);

            var resolution = _ctx.Resolver.Resolve(token, moduleMvid);
            if (!resolution.Success)
                return ToolResponse.Failure(ToolRenameClass, resolution.Error!);
            if (resolution.Entity is not TypeDef type)
                return ToolResponse.Failure(ToolRenameClass, NotRenameableAs(resolution, "TypeDef", "rename_symbol"));

            var previousName = type.Name?.String ?? "";
            var previousFullName = type.FullName?.ToString() ?? "";
            var newFullName = string.IsNullOrEmpty(type.Namespace?.String)
                ? newName
                : $"{type.Namespace.String}.{newName}";

            if (dryRun) {
                var probe = PreviewRename(resolution.Token, type, includeReferences);
                return ToolResponse.Success(ToolRenameClass, MetadataIdentity.ForType(type), new JsonObject {
                    ["dryRun"] = true,
                    ["changed"] = false,
                    ["previousName"] = previousName,
                    ["newName"] = newName,
                    ["previousFullName"] = previousFullName,
                    ["newFullName"] = newFullName,
                    // The whole reference closure the rename covers, not just rows needing a write: an
            // intra-module call site points at the definition object itself. This is the number
            // to treat as the blast radius.
            ["referenceSitesToUpdate"] = probe.Rows,
                    ["collisions"] = ToArray(probe.Collisions),
                    ["message"] = $"Would rename type {resolution.Id} from '{previousName}' to '{newName}' "
                        + $"and update {probe.Rows} reference row(s) across {_ctx.Resolver.GetAllModules().Count()} loaded module(s).",
                });
            }

            var outcome = SymbolRenamer.Rename(_ctx.Resolver, resolution, newName, includeReferences);
            if (!outcome.Success)
                return ToolResponse.Failure(ToolRenameClass, outcome.Error!);

            var saveNote = RefreshAfterRename();

            // Broadcast the updated metadata payload: the id is unchanged, the name moved.
            var updated = MetadataIdentity.ForType(type);
            return ToolResponse.Success(ToolRenameClass, updated, new JsonObject {
                ["dryRun"] = false,
                ["changed"] = true,
                ["previousName"] = previousName,
                ["newName"] = newName,
                ["previousFullName"] = previousFullName,
                ["newFullName"] = type.FullName?.ToString() ?? newFullName,
                ["referenceRowsUpdated"] = outcome.ReferenceRowsRenamed,
                ["referenceSitesUpdated"] = outcome.ReferenceSites,
                ["definitionsUpdated"] = outcome.RenamedDefinitions.Count,
                ["definitionsRenamedByPropagation"] = outcome.DefinitionsRenamedByPropagation,
                ["collisions"] = ToArray(outcome.Collisions),
                ["warnings"] = ToArray(outcome.Warnings),
                ["verified"] = outcome.Warnings.Count == 0,
                ["persisted"] = false,
                ["message"] = $"Renamed type {outcome.Id} to '{newName}'; {outcome.ReferenceSites} reference site(s) in the loaded workspace now resolve to the new name ({outcome.ReferenceRowsRenamed} cross-assembly row(s) rewritten).{saveNote}",
            });
        }

        [Description("Rename ONE method, addressed by its metadata id/token (hex or decimal; discover it with search_methods/get_type_members). Applies in dnSpy's live metadata AND propagates to every call site (MemberRef) in the loaded workspace, so cross-assembly calls stay bound to this token. dryRun=true (default) previews. Method lookup by name or partial match is intentionally not supported (obfuscated binaries reuse names).")]
        public string RenameMethod(
            [Description("Metadata token ('id') of the MethodDef to rename, e.g. '0x060012AB'")] string token,
            [Description("New method name (no dots, no signature)")] string newName,
            [Description("Preview only, do not modify metadata")] bool dryRun = true,
            [Description("Propagate the rename to every reference row in every loaded module (keep true in the pipeline)")] bool includeReferences = true,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolRenameMethod, "No assemblies loaded.");

            var nameError = ValidateSimpleName(newName, "newName");
            if (nameError is not null)
                return ToolResponse.Failure(ToolRenameMethod, nameError);

            var resolution = _ctx.Resolver.Resolve(token, moduleMvid);
            if (!resolution.Success)
                return ToolResponse.Failure(ToolRenameMethod, resolution.Error!);
            if (resolution.Entity is not MethodDef method)
                return ToolResponse.Failure(ToolRenameMethod, NotRenameableAs(resolution, "MethodDef", "rename_symbol"));

            var previousName = method.Name?.String ?? "";
            var declaringType = method.DeclaringType?.FullName?.ToString() ?? "";

            if (dryRun) {
                var probe = PreviewRename(resolution.Token, method, includeReferences);
                return ToolResponse.Success(ToolRenameMethod, MetadataIdentity.ForMethod(method), new JsonObject {
                    ["dryRun"] = true,
                    ["changed"] = false,
                    ["previousName"] = previousName,
                    ["newName"] = newName,
                    ["declaringType"] = declaringType,
                    // The whole reference closure the rename covers, not just rows needing a write: an
            // intra-module call site points at the definition object itself. This is the number
            // to treat as the blast radius.
            ["referenceSitesToUpdate"] = probe.Rows,
                    ["collisions"] = ToArray(probe.Collisions),
                    ["message"] = $"Would rename method {resolution.Id} ({declaringType}::{previousName}) to '{newName}' "
                        + $"and update {probe.Rows} reference row(s).",
                });
            }

            var outcome = SymbolRenamer.Rename(_ctx.Resolver, resolution, newName, includeReferences);
            if (!outcome.Success)
                return ToolResponse.Failure(ToolRenameMethod, outcome.Error!);

            var saveNote = RefreshAfterRename();

            var updated = MetadataIdentity.ForMethod(method);
            return ToolResponse.Success(ToolRenameMethod, updated, new JsonObject {
                ["dryRun"] = false,
                ["changed"] = true,
                ["previousName"] = previousName,
                ["newName"] = newName,
                ["declaringType"] = declaringType,
                ["referenceRowsUpdated"] = outcome.ReferenceRowsRenamed,
                ["referenceSitesUpdated"] = outcome.ReferenceSites,
                ["definitionsUpdated"] = outcome.RenamedDefinitions.Count,
                ["definitionsRenamedByPropagation"] = outcome.DefinitionsRenamedByPropagation,
                // A same-name/same-signature sibling is a real CLR conflict — surfaced, not hidden.
                ["collisions"] = ToArray(outcome.Collisions),
                ["warnings"] = ToArray(outcome.Warnings),
                ["verified"] = outcome.Warnings.Count == 0,
                ["persisted"] = false,
                ["message"] = $"Renamed method {outcome.Id} ({declaringType}::{previousName}) to '{newName}' "
                    + $"; {outcome.ReferenceSites} reference site(s) in the loaded workspace now resolve to the new name ({outcome.ReferenceRowsRenamed} cross-assembly row(s) rewritten).{saveNote}",
            });
        }

        [Description("Rename a namespace across all types in a module, addressed by the metadata id/token of ANY TypeDef inside that namespace (discover one with get_type_members/assembly_list_types). Propagates the new namespace to every TypeRef in the loaded workspace. dryRun=true (default) previews. On success the response broadcasts the updated namespace identity (anchor id + NEW name) plus every affected type's immutable id.")]
        public string RenameNamespace(
            [Description("Metadata token ('id') of any TypeDef in the namespace to rename")] string token,
            [Description("New namespace value (empty string moves the types to the global namespace)")] string newNamespace,
            [Description("Preview only, do not modify metadata")] bool dryRun = true,
            [Description("Propagate the rename to every TypeRef row in every loaded module (keep true in the pipeline)")] bool includeReferences = true,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolRenameNamespace, "No assemblies loaded.");

            var namespaceError = ValidateNamespace(newNamespace);
            if (namespaceError is not null)
                return ToolResponse.Failure(ToolRenameNamespace, namespaceError);

            var anchor = _ctx.Resolver.ResolveAs<TypeDef>(token, moduleMvid, out var module, out var resolveError);
            if (anchor is null)
                return ToolResponse.Failure(ToolRenameNamespace, resolveError!);

            var oldNamespace = anchor.Namespace?.String ?? "";
            var targetModule = anchor.Module ?? module;
            if (targetModule is null)
                return ToolResponse.Failure(ToolRenameNamespace, "Cannot determine the module owning the resolved type token.");

            var affected = TypesInNamespace(targetModule, oldNamespace);
            if (affected.Count == 0)
                return ToolResponse.Failure(ToolRenameNamespace,
                    $"No types in module {MetadataIdentity.Mvid(targetModule)} declare namespace '{oldNamespace}'.");

            var previousIdentity = MetadataIdentity.ForNamespace(targetModule, oldNamespace);

            if (dryRun) {
                var plan = new List<JsonObject>();
                foreach (var type in affected) {
                    var item = MetadataIdentity.ForType(type);
                    item["newFullName"] = string.IsNullOrEmpty(newNamespace)
                        ? type.Name?.String ?? ""
                        : $"{newNamespace}.{type.Name}";
                    plan.Add(item);
                }

                return ToolResponse.Success(ToolRenameNamespace, previousIdentity, new JsonObject {
                    ["dryRun"] = true,
                    ["changed"] = false,
                    ["previousName"] = oldNamespace,
                    ["newName"] = newNamespace,
                    ["typeCount"] = affected.Count,
                    ["types"] = ToolResponse.Array(plan),
                    ["message"] = $"Would rename namespace '{oldNamespace}' to '{newNamespace}' for {affected.Count} type(s).",
                });
            }

            var outcome = SymbolRenamer.RenameNamespace(_ctx.Resolver, anchor, newNamespace, includeReferences);
            if (!outcome.Success)
                return ToolResponse.Failure(ToolRenameNamespace, outcome.Error!);

            _ctx.TreeRefresh.NotifyNamespaceRenamed(
                MetadataIdentity.AssemblyName(targetModule), oldNamespace, newNamespace);
            var saveNote = RefreshAfterRename();

            var updatedTypes = new List<JsonObject>();
            foreach (var type in outcome.AffectedTypes) {
                var item = MetadataIdentity.ForType(type);
                item["previousFullName"] = string.IsNullOrEmpty(oldNamespace)
                    ? type.Name?.String ?? ""
                    : $"{oldNamespace}.{type.Name}";
                updatedTypes.Add(item);
            }

            // Broadcast the updated namespace identity (anchor id + NEW name). The per-type
            // ids are unchanged by the rename — only their names moved.
            var updatedAnchor = MetadataIdentity.ForNamespace(targetModule, newNamespace);
            return ToolResponse.Success(ToolRenameNamespace, updatedAnchor, new JsonObject {
                ["dryRun"] = false,
                ["changed"] = true,
                ["previousName"] = oldNamespace,
                ["newName"] = newNamespace,
                ["typeCount"] = outcome.AffectedTypes.Count,
                ["previousIdentity"] = previousIdentity,
                ["types"] = ToolResponse.Array(updatedTypes),
                ["referenceRowsUpdated"] = outcome.ReferenceRowsRenamed,
                ["referenceSitesUpdated"] = outcome.ReferenceSites,
                ["warnings"] = ToArray(outcome.Warnings),
                ["verified"] = outcome.Warnings.Count == 0,
                ["persisted"] = false,
                ["message"] = $"Renamed namespace '{oldNamespace}' to '{newNamespace}' for {outcome.AffectedTypes.Count} type(s) "
                    + $"; {outcome.ReferenceSites} type reference site(s) now resolve to the new namespace ({outcome.ReferenceRowsRenamed} TypeRef row(s) rewritten).{saveNote}",
            });
        }

        [Description(
            "MACRO rename for pipeline-wide symbol cleanup: rename any token-addressable element (Type, Method, Field, Property, Event) by its metadata id, apply the change in dnSpy's live memory, and immediately propagate it to every reference in the loaded workspace. "
            + "One call = one element plus its whole reference closure, so a worker never has to enumerate call sites itself. mode='suffix' appends a marker to the current name, which is the collision-free way to make duplicated obfuscated names addressable. "
            + "dryRun=true (default) reports exactly how many definitions and reference rows would change without writing anything.")]
        public string RenameSymbol(
            [Description("Metadata token ('id') of the element to rename, e.g. '0x060012AB'")] string token,
            [Description("New name. mode='set': the full new name. mode='suffix': the marker appended to the current name (e.g. '_decrypted')")] string? newName = null,
            [Description("'set' (replace the name with newName) or 'suffix' (append newName to the current name)")] string mode = "set",
            [Description("Preview only, do not modify metadata")] bool dryRun = true,
            [Description("Propagate the rename to every reference row in every loaded module (keep true so cross-assembly calls stay bound)")] bool includeReferences = true,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolRenameSymbol, "No assemblies loaded.");

            if (string.IsNullOrWhiteSpace(newName)) {
                return ToolResponse.Failure(ToolRenameSymbol,
                    "newName is required. For the collision-free bulk form pass mode='suffix' and newName='_<marker>'.");
            }

            var normalizedMode = (mode ?? "set").Trim().ToLowerInvariant();
            if (normalizedMode is not ("set" or "suffix")) {
                return ToolResponse.Failure(ToolRenameSymbol,
                    $"Unknown mode '{mode}'. Use 'set' (replace the name) or 'suffix' (append to the current name).");
            }

            if (normalizedMode == "set") {
                var nameError = ValidateSimpleName(newName, "newName");
                if (nameError is not null)
                    return ToolResponse.Failure(ToolRenameSymbol, nameError);
            }
            else if (newName.IndexOfAny(new[] { '.', '/', ':', ' ' }) >= 0) {
                return ToolResponse.Failure(ToolRenameSymbol,
                    $"Suffix '{newName}' must be an identifier fragment (no '.', '/', ':', or whitespace).");
            }

            var resolution = _ctx.Resolver.Resolve(token, moduleMvid);
            if (!resolution.Success)
                return ToolResponse.Failure(ToolRenameSymbol, resolution.Error!);

            var entity = resolution.Entity!;
            if (entity is not (TypeDef or MethodDef or FieldDef or PropertyDef or EventDef)) {
                return ToolResponse.Failure(ToolRenameSymbol,
                    $"Token {resolution.Id} resolves to {entity.GetType().Name}, which is not a renameable element "
                    + "(supported: TypeDef, MethodDef, FieldDef, PropertyDef, EventDef).");
            }

            var currentName = CurrentNameOf(entity) ?? "";
            var finalName = normalizedMode == "suffix" ? currentName + newName : newName;

            var probe = PreviewRename(resolution.Token, entity, includeReferences);
            var target = IdentityOf(entity);

            if (dryRun) {
                return ToolResponse.Success(ToolRenameSymbol, target, new JsonObject {
                    ["dryRun"] = true,
                    ["changed"] = false,
                    ["mode"] = normalizedMode,
                    ["previousName"] = currentName,
                    ["newName"] = finalName,
                    // The whole reference closure the rename covers, not just rows needing a write: an
            // intra-module call site points at the definition object itself. This is the number
            // to treat as the blast radius.
            ["referenceSitesToUpdate"] = probe.Rows,
                    ["collisions"] = ToArray(probe.Collisions),
                    ["message"] = $"Would rename {MetadataIdentity.TypeOf(entity)} {resolution.Id} from '{currentName}' "
                        + $"to '{finalName}' and update {probe.Rows} reference row(s) across "
                        + $"{_ctx.Resolver.GetAllModules().Count()} loaded module(s).",
                });
            }

            var outcome = SymbolRenamer.Rename(_ctx.Resolver, resolution, finalName, includeReferences);
            if (!outcome.Success)
                return ToolResponse.Failure(ToolRenameSymbol, outcome.Error!);

            var saveNote = RefreshAfterRename();

            return ToolResponse.Success(ToolRenameSymbol, IdentityOf(entity), new JsonObject {
                ["dryRun"] = false,
                ["changed"] = true,
                ["mode"] = normalizedMode,
                ["previousName"] = currentName,
                ["newName"] = finalName,
                ["type"] = MetadataIdentity.TypeOf(entity),
                ["referenceRowsUpdated"] = outcome.ReferenceRowsRenamed,
                ["referenceSitesUpdated"] = outcome.ReferenceSites,
                ["definitionsUpdated"] = outcome.RenamedDefinitions.Count,
                ["definitionsRenamedByPropagation"] = outcome.DefinitionsRenamedByPropagation,
                ["collisions"] = ToArray(outcome.Collisions),
                ["warnings"] = ToArray(outcome.Warnings),
                ["verified"] = outcome.Warnings.Count == 0,
                ["persisted"] = false,
                ["message"] = $"Renamed {MetadataIdentity.TypeOf(entity)} {outcome.Id} from '{currentName}' to '{finalName}' "
                    + $"; {outcome.ReferenceSites} reference site(s) in the loaded workspace now resolve to the new name ({outcome.ReferenceRowsRenamed} cross-assembly row(s) rewritten).{saveNote}",
            });
        }

        // ---------------------------------------------------------------------
        // Dry-run probe
        // ---------------------------------------------------------------------

        /// <summary>
        /// Counts the reference closure a rename would affect, WITHOUT touching metadata.
        /// </summary>
        /// <remarks>
        /// This calls the SAME <see cref="SymbolRenamer.PlanOrApplyReferences"/> the write path
        /// calls, with <c>newName: null</c> (plan-only). A dry run therefore cannot report a
        /// different set than the real write would touch — the predicate and traversal are one
        /// implementation, not two that have to be kept in step.
        /// </remarks>
        private (int Rows, List<string> Collisions) PreviewRename(uint targetToken, IMDTokenProvider entity, bool includeReferences) {
            var collisions = SymbolRenamer.FindCollisions(entity, PreviewNameOf(entity));
            if (!includeReferences)
                return (0, collisions);

            var plan = SymbolRenamer.PlanOrApplyReferences(
                _ctx.Resolver,
                targetToken,
                MetadataIdentity.Mvid(ModuleOf(entity)),
                MetadataIdentity.AssemblyName(ModuleOf(entity)),
                entity,
                newName: null,
                warnings: null);

            // "Rows" is the whole reference closure, not just the rows that need a write: an
            // intra-module call site points at the definition object itself (already renamed),
            // and a caller counting only MemberRef writes would under-report the blast radius.
            return (plan.ReferenceSites, collisions);
        }

        /// <summary>Name the entity currently carries (used only to probe sibling collisions).</summary>
        private static string PreviewNameOf(IMDTokenProvider entity) => CurrentNameOf(entity) ?? "";

        private static string NotRenameableAs(TokenResolution resolution, string expected, string alternativeTool) =>
            $"Token {resolution.Id} resolves to {resolution.Entity!.GetType().Name}, not a {expected}. "
            + $"Use {alternativeTool} to rename any token-addressable element.";

        private static string? CurrentNameOf(IMDTokenProvider entity) => entity switch {
            TypeDef t => t.Name?.String,
            MethodDef m => m.Name?.String,
            FieldDef f => f.Name?.String,
            PropertyDef p => p.Name?.String,
            EventDef e => e.Name?.String,
            _ => null,
        };

        private static JsonObject IdentityOf(IMDTokenProvider entity) => entity switch {
            TypeDef t => MetadataIdentity.ForType(t),
            MethodDef m => MetadataIdentity.ForMethod(m),
            FieldDef f => MetadataIdentity.ForField(f),
            PropertyDef p => MetadataIdentity.ForProperty(p),
            EventDef e => MetadataIdentity.ForEvent(e),
            _ => MetadataIdentity.For(entity, MetadataIdentity.ReferenceKind, ModuleOf(entity), CurrentNameOf(entity)),
        };

        private static ModuleDef? ModuleOf(IMDTokenProvider entity) => entity switch {
            TypeDef t => t.Module,
            MethodDef m => m.Module,
            FieldDef f => f.Module,
            PropertyDef p => p.Module,
            EventDef e => e.Module,
            ModuleDef mod => mod,
            _ => null,
        };

        private static List<TypeDef> TypesInNamespace(ModuleDef module, string @namespace) =>
            module.GetTypes()
                .Where(t => string.Equals(t.Namespace?.String ?? "", @namespace, StringComparison.Ordinal))
                .OrderBy(t => t.MDToken.Raw)
                .ToList();

        private static JsonArray ToArray(IEnumerable<string> values) {
            var array = new JsonArray();
            foreach (var value in values)
                array.Add((JsonNode)value);
            return array;
        }

        /// <summary>Refreshes the host tree/tabs after an in-memory metadata change.</summary>
        private string RefreshAfterRename() {
            _ctx.TreeRefresh.RefreshAll();
            return " Changes applied in-memory. Use workspace_save_code to re-dump the whole workspace as a C# project.";
        }

        /// <summary>
        /// Rejects names that would corrupt metadata (empty, dotted, or containing the
        /// namespace/type separators used by .NET's reflection names).
        /// </summary>
        static string? ValidateSimpleName(string? newName, string parameterName) {
            if (string.IsNullOrWhiteSpace(newName))
                return $"{parameterName} is required.";
            if (newName.Contains('.') || newName.Contains('/') || newName.Contains(':'))
                return $"{parameterName} must be a simple name: '{newName}' contains '.', '/' or ':'.";
            return null;
        }

        static string? ValidateNamespace(string? newNamespace) {
            if (newNamespace is null)
                return "newNamespace is required (pass an empty string for the global namespace).";
            if (newNamespace.Contains("::", StringComparison.Ordinal) || newNamespace.Contains('/'))
                return "newNamespace must be a dotted namespace or empty; it cannot contain '::' or '/'.";
            return null;
        }
    }
}
