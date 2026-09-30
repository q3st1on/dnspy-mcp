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
    /// Metadata mutations. Every rename is addressed by metadata token — the caller
    /// passes the token it got from a discovery tool, the element is located in the
    /// loaded assembly definitions by that token, the change is applied, and the
    /// updated payload (Token + New Name) is broadcast back.
    /// </summary>
    /// <remarks>
    /// The token NEVER changes as a result of a rename (it is the identity); only the
    /// name field of the response moves. That is what makes a rename sequence
    /// idempotent: the same token can be renamed again, or used afterwards to
    /// decompile/inspect the element under its new name, without re-discovery.
    /// Name/signature matching ("rename every method called Foo", "partial match") was
    /// removed deliberately: on an obfuscated binary it renames the wrong elements.
    /// </remarks>
    public sealed class RenameTools {
        const string ToolRenameClass = "rename_class";
        const string ToolRenameMethod = "rename_method";
        const string ToolRenameNamespace = "rename_namespace";

        private readonly McpContext _ctx;
        public RenameTools(McpContext ctx) => _ctx = ctx;

        [Description("Rename ONE type, addressed by its .NET metadata token (hex or decimal; discover it with search_types/assembly_list_types/get_type_members). dryRun=true (default) previews. On success the response broadcasts the updated identity: the SAME token with the NEW name.")]
        public string RenameClass(
            [Description("Metadata token of the TypeDef to rename, e.g. '0x02000001' or '33554433'")] string token,
            [Description("New simple type name (no namespace, no dots)")] string newName,
            [Description("Preview only, do not modify metadata")] bool dryRun = true,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolRenameClass, "No assemblies loaded.");

            var nameError = ValidateSimpleName(newName, "newName");
            if (nameError is not null)
                return ToolResponse.Failure(ToolRenameClass, nameError);

            var type = _ctx.Resolver.ResolveAs<TypeDef>(token, moduleMvid, out _, out var resolveError);
            if (type is null)
                return ToolResponse.Failure(ToolRenameClass, resolveError!);

            var tokenText = TokenParser.Format(type.MDToken.Raw);
            var previousName = type.Name?.String ?? "";
            var previousFullName = type.FullName?.ToString() ?? "";
            var newFullName = string.IsNullOrEmpty(type.Namespace?.String)
                ? newName
                : $"{type.Namespace.String}.{newName}";

            if (dryRun) {
                return ToolResponse.Success(ToolRenameClass, MetadataIdentity.ForType(type), new JsonObject {
                    ["dryRun"] = true,
                    ["changed"] = false,
                    ["previousName"] = previousName,
                    ["newName"] = newName,
                    ["previousFullName"] = previousFullName,
                    ["newFullName"] = newFullName,
                    ["message"] = $"Would rename type {tokenText} from '{previousName}' to '{newName}'.",
                });
            }

            type.Name = (UTF8String)newName;
            var saveNote = RefreshAfterRename();

            // Broadcast the updated metadata payload: token unchanged, name updated.
            var updated = MetadataIdentity.ForType(type);
            return ToolResponse.Success(ToolRenameClass, updated, new JsonObject {
                ["dryRun"] = false,
                ["changed"] = true,
                ["previousName"] = previousName,
                ["newName"] = newName,
                ["previousFullName"] = previousFullName,
                ["newFullName"] = type.FullName?.ToString() ?? newFullName,
                ["persisted"] = false,
                ["message"] = $"Renamed type {tokenText} to '{newName}'.{saveNote}",
            });
        }

        [Description("Rename ONE method, addressed by its .NET metadata token (hex or decimal; discover it with search_methods/get_type_members). dryRun=true (default) previews. On success the response broadcasts the updated identity: the SAME token with the NEW name. Method lookup by name or partial match is intentionally not supported (obfuscated binaries reuse names).")]
        public string RenameMethod(
            [Description("Metadata token of the MethodDef to rename, e.g. '0x06000001' or '100663297'")] string token,
            [Description("New method name (no dots, no signature)")] string newName,
            [Description("Preview only, do not modify metadata")] bool dryRun = true,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolRenameMethod, "No assemblies loaded.");

            var nameError = ValidateSimpleName(newName, "newName");
            if (nameError is not null)
                return ToolResponse.Failure(ToolRenameMethod, nameError);

            var method = _ctx.Resolver.ResolveAs<MethodDef>(token, moduleMvid, out _, out var resolveError);
            if (method is null)
                return ToolResponse.Failure(ToolRenameMethod, resolveError!);

            var tokenText = TokenParser.Format(method.MDToken.Raw);
            var previousName = method.Name?.String ?? "";
            var declaringType = method.DeclaringType?.FullName?.ToString() ?? "";

            if (dryRun) {
                return ToolResponse.Success(ToolRenameMethod, MetadataIdentity.ForMethod(method), new JsonObject {
                    ["dryRun"] = true,
                    ["changed"] = false,
                    ["previousName"] = previousName,
                    ["newName"] = newName,
                    ["declaringType"] = declaringType,
                    ["message"] = $"Would rename method {tokenText} ({declaringType}::{previousName}) to '{newName}'.",
                });
            }

            method.Name = (UTF8String)newName;
            var saveNote = RefreshAfterRename();

            // Broadcast the updated metadata payload: token unchanged, name updated.
            var updated = MetadataIdentity.ForMethod(method);
            return ToolResponse.Success(ToolRenameMethod, updated, new JsonObject {
                ["dryRun"] = false,
                ["changed"] = true,
                ["previousName"] = previousName,
                ["newName"] = newName,
                ["declaringType"] = declaringType,
                ["persisted"] = false,
                ["message"] = $"Renamed method {tokenText} ({declaringType}::{previousName}) to '{newName}'.{saveNote}",
            });
        }

        [Description("Rename a namespace across all types in a module. The target is addressed by the .NET metadata token of ANY TypeDef inside that namespace (discover one with get_type_members/assembly_list_types); the namespace is derived from that type's Namespace. dryRun=true (default) previews. On success the response broadcasts the updated namespace identity (anchor token + NEW name) plus every affected type's immutable token.")]
        public string RenameNamespace(
            [Description("Metadata token of any TypeDef in the namespace to rename")] string token,
            [Description("New namespace value (empty string moves the types to the global namespace)")] string newNamespace,
            [Description("Preview only, do not modify metadata")] bool dryRun = true,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolRenameNamespace, "No assemblies loaded.");

            if (newNamespace is null)
                return ToolResponse.Failure(ToolRenameNamespace, "newNamespace is required (pass an empty string for the global namespace).");
            if (newNamespace.Contains("::", StringComparison.Ordinal) || newNamespace.Contains('/'))
                return ToolResponse.Failure(ToolRenameNamespace, "newNamespace must be a dotted namespace or empty; it cannot contain '::' or '/'.");

            var anchor = _ctx.Resolver.ResolveAs<TypeDef>(token, moduleMvid, out var module, out var resolveError);
            if (anchor is null)
                return ToolResponse.Failure(ToolRenameNamespace, resolveError!);

            var oldNamespace = anchor.Namespace?.String ?? "";
            var targetModule = anchor.Module ?? module;
            if (targetModule is null)
                return ToolResponse.Failure(ToolRenameNamespace, "Cannot determine the module owning the resolved type token.");

            // Token-keyed membership: the namespace is the set of TypeDefs in THIS module
            // whose Namespace field equals the one read from the addressed anchor type.
            var affected = targetModule.GetTypes()
                .Where(t => string.Equals(t.Namespace?.String ?? "", oldNamespace, StringComparison.Ordinal))
                .OrderBy(t => t.MDToken.Raw)
                .ToList();

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

            foreach (var type in affected)
                type.Namespace = (UTF8String)newNamespace;

            _ctx.TreeRefresh.NotifyNamespaceRenamed(
                MetadataIdentity.AssemblyName(targetModule), oldNamespace, newNamespace);
            var saveNote = RefreshAfterRename();

            var updatedTypes = new List<JsonObject>();
            foreach (var type in affected) {
                var item = MetadataIdentity.ForType(type);
                item["previousFullName"] = string.IsNullOrEmpty(oldNamespace)
                    ? type.Name?.String ?? ""
                    : $"{oldNamespace}.{type.Name}";
                updatedTypes.Add(item);
            }

            // Broadcast the updated namespace identity (anchor token + NEW name). The
            // per-type tokens are unchanged by the rename — only their names moved.
            var updatedAnchor = MetadataIdentity.ForNamespace(targetModule, newNamespace);
            return ToolResponse.Success(ToolRenameNamespace, updatedAnchor, new JsonObject {
                ["dryRun"] = false,
                ["changed"] = true,
                ["previousName"] = oldNamespace,
                ["newName"] = newNamespace,
                ["typeCount"] = affected.Count,
                ["previousIdentity"] = previousIdentity,
                ["types"] = ToolResponse.Array(updatedTypes),
                ["persisted"] = false,
                ["message"] = $"Renamed namespace '{oldNamespace}' to '{newNamespace}' for {affected.Count} type(s).{saveNote}",
            });
        }

        /// <summary>Refreshes the host tree/tabs after an in-memory metadata change.</summary>
        private string RefreshAfterRename() {
            _ctx.TreeRefresh.RefreshAll();
            return " Changes applied in-memory. Use dnSpy's File > Save Module to persist to disk.";
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
    }
}
