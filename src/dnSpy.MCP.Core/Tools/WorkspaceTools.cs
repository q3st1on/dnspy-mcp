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
    /// Workspace-level staging operations: re-dump the entire loaded (and possibly mutated)
    /// multi-assembly workspace to disk as a C# project solution.
    /// </summary>
    /// <remarks>
    /// This is the only tool that writes to the filesystem rather than to metadata, which is
    /// why it is separate from <c>AssemblyTools</c>: it carries a path parameter, an overwrite
    /// flag, and its own failure modes (disk, permissions, name collisions), and it must not
    /// be mistaken for a metadata mutation. It IS declared <c>save_*</c> so the transport's
    /// mutation gate serializes it against rename/patch — a re-dump that races a rename would
    /// write a torn view of the workspace.
    /// </remarks>
    public sealed class WorkspaceTools {
        const string ToolWorkspaceSaveCode = "workspace_save_code";
        const string ToolWorkspaceExportPlan = "workspace_export_plan";

        private readonly McpContext _ctx;
        public WorkspaceTools(McpContext ctx) => _ctx = ctx;

        [Description(
            "Re-dump the ENTIRE loaded workspace to disk as a compilable C# project solution: one directory + .csproj per loaded assembly (or a single project with singleProject=true), namespace-mirroring source files, ProjectReference edges derived from each module's real AssemblyRef rows, a .sln when several assemblies are loaded, and a machine-readable workspace-tokens.json mapping every exported type's immutable metadata token (id) to its file. "
            + "The dump reflects in-memory mutations (renames, IL patches) that were never written back to the original binaries. Obfuscated decompiled output often does not compile, and assemblies outside the workspace appear as commented hints — build the graph from workspace-tokens.json, not from the C# text. Export is staged and committed atomically; existing output is merged unless overwrite=true.")]
        public string WorkspaceSaveCode(
            [Description("Absolute output directory for the generated project tree (created if absent)")] string outputDirectory,
            [Description("Name used for the generated .sln file (default 'dnspy-workspace')")] string solutionName = "dnspy-workspace",
            [Description("Emit ONE .csproj for the whole workspace instead of one project per loaded assembly")] bool singleProject = false,
            [Description("Project file name (without .csproj) used when singleProject=true (default 'Workspace')")] string projectName = "Workspace",
            [Description("Replace the output directory entirely instead of merging staged files into it")] bool overwrite = false,
            [Description("Also export types that live in the global namespace (default true)")] bool includeGlobalNamespace = true,
            [Description("Maximum number of types to decompile across the whole export (default 20000)")] int maxTypes = 20000,
            [Description("Optional assembly simple name to restrict the export to (discovery scoping, not an element address)")] string? assembly = null) {

            if (string.IsNullOrWhiteSpace(outputDirectory))
                return ToolResponse.Failure(ToolWorkspaceSaveCode, "outputDirectory is required (absolute path).");

            var documents = _ctx.AssemblyLoader.GetDocuments();
            if (documents.Count == 0)
                return ToolResponse.Failure(ToolWorkspaceSaveCode, "No assemblies loaded.");

            var selected = SelectModules(documents, assembly, out var scopeError);
            if (scopeError is not null)
                return ToolResponse.Failure(ToolWorkspaceSaveCode, scopeError);

            var exporter = new ProjectExporter(selected, _ctx.SourceDecompiler, _ctx.Log);
            var result = exporter.Export(new ProjectExportOptions {
                OutputDirectory = outputDirectory,
                SolutionName = solutionName,
                SingleProject = singleProject,
                ProjectName = projectName,
                Overwrite = overwrite,
                IncludeGlobalNamespace = includeGlobalNamespace,
                MaxTypes = maxTypes,
            });

            if (!result.Success)
                return ToolResponse.Failure(ToolWorkspaceSaveCode, result.Error!);

            _ctx.Log.Info($"Workspace re-dump: {result.FileCount} type(s) across {result.Modules.Count} module(s) → {result.OutputDirectory}");

            var modules = new List<JsonObject>();
            foreach (var module in result.Modules) {
                modules.Add(new JsonObject {
                    // The module is an element too: its identity is the MVID (module row token
                    // 0x00000001 is identical in every module) plus its assembly name.
                    ["id"] = module.ModuleMvid,
                    ["type"] = MetadataIdentity.ModuleType,
                    ["current_name"] = module.AssemblyName,
                    ["nameIsMutable"] = true,
                    ["idIsImmutable"] = true,
                    ["moduleMvid"] = module.ModuleMvid,
                    ["assembly"] = module.AssemblyName,
                    ["projectFile"] = Path.GetFileName(module.ProjectFile),
                    ["typeCount"] = module.TypeCount,
                    ["fileCount"] = module.FileCount,
                    ["failedTypeCount"] = module.FailedTypeCount,
                });
            }

            return ToolResponse.Success(ToolWorkspaceSaveCode, new JsonObject {
                ["outputDirectory"] = result.OutputDirectory,
                ["solutionFile"] = result.SolutionFile,
                ["tokenManifest"] = result.TokenManifestFile,
                ["moduleCount"] = result.Modules.Count,
                ["typeCount"] = result.TypeCount,
                ["fileCount"] = result.FileCount,
                ["failedTypeCount"] = result.FailedTypeCount,
                ["truncated"] = result.Truncated,
                ["overwroteExisting"] = overwrite,
                ["modules"] = ToolResponse.Array(modules),
                ["warnings"] = ToArray(result.Warnings),
                ["message"] = $"Re-dumped {result.TypeCount} type(s) from {result.Modules.Count} loaded module(s) into '{result.OutputDirectory}'. "
                    + "Compilability is best-effort: the authoritative artifact is "
                    + $"'{Path.GetFileName(result.TokenManifestFile ?? "workspace-tokens.json")}' (token → file map).",
            });
        }

        [Description("Plan a workspace re-dump without writing anything: reports the loaded modules, how many top-level types each would export, which assemblies would link to which, and which references fall outside the workspace. Use this to size a staging re-dump before calling workspace_save_code.")]
        public string WorkspaceExportPlan(
            [Description("Optional assembly simple name to restrict the plan to (discovery scoping, not an element address)")] string? assembly = null) {

            var documents = _ctx.AssemblyLoader.GetDocuments();
            if (documents.Count == 0)
                return ToolResponse.Failure(ToolWorkspaceExportPlan, "No assemblies loaded.");

            var selected = SelectModules(documents, assembly, out var scopeError);
            if (scopeError is not null)
                return ToolResponse.Failure(ToolWorkspaceExportPlan, scopeError);

            var loadedNames = new HashSet<string>(
                selected.Select(d => MetadataIdentity.AssemblyName(d.Module)),
                StringComparer.OrdinalIgnoreCase);

            var modules = new List<JsonObject>();
            var totalTypes = 0;
            foreach (var loaded in selected) {
                var module = loaded.Module;
                var topLevel = module.GetTypes().Count(t => t.DeclaringType is null);
                totalTypes += topLevel;

                var internalRefs = new List<string>();
                var externalRefs = new List<string>();
                foreach (var assemblyRef in module.GetAssemblyRefs()) {
                    var name = assemblyRef.Name?.String ?? "";
                    if (name.Length == 0)
                        continue;
                    if (loadedNames.Contains(name) && !string.Equals(name, MetadataIdentity.AssemblyName(module), StringComparison.OrdinalIgnoreCase))
                        internalRefs.Add(name);
                    else
                        externalRefs.Add(name);
                }

                modules.Add(new JsonObject {
                    ["id"] = MetadataIdentity.Mvid(module),
                    ["type"] = MetadataIdentity.ModuleType,
                    ["current_name"] = MetadataIdentity.AssemblyName(module),
                    ["moduleMvid"] = MetadataIdentity.Mvid(module),
                    ["assembly"] = MetadataIdentity.AssemblyName(module),
                    ["path"] = module.Location ?? "",
                    ["topLevelTypeCount"] = topLevel,
                    ["workspaceReferences"] = ToArray(internalRefs.Distinct(StringComparer.OrdinalIgnoreCase)),
                    ["externalReferences"] = ToArray(externalRefs.Distinct(StringComparer.OrdinalIgnoreCase)),
                });
            }

            return ToolResponse.Success(ToolWorkspaceExportPlan, new JsonObject {
                ["scope"] = string.IsNullOrEmpty(assembly) ? "all loaded assemblies" : assembly,
                ["moduleCount"] = modules.Count,
                ["topLevelTypeCount"] = totalTypes,
                ["modules"] = ToolResponse.Array(modules),
                ["message"] = $"Export would decompile {totalTypes} top-level type(s) from {modules.Count} module(s). "
                    + "Assemblies that are not loaded cannot become ProjectReferences and will be reported as commented hints.",
            });
        }


        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Resolves the optional assembly scope. Scoping here is discovery-style (which binaries
        /// to enumerate) and never addresses an element, which is why a simple name is allowed.
        /// </summary>
        private static IReadOnlyList<Abstractions.LoadedModule> SelectModules(
            IReadOnlyList<Abstractions.LoadedModule> documents,
            string? assembly,
            out string? error) {

            error = null;
            if (string.IsNullOrWhiteSpace(assembly))
                return documents;

            var matched = documents
                .Where(d => string.Equals(d.AssemblyName, assembly, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileNameWithoutExtension(d.Name), assembly, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matched.Count == 0) {
                var available = string.Join(", ", documents.Select(d => $"{d.AssemblyName} ({d.Module.Mvid:D})"));
                error = $"No loaded assembly named '{assembly}'. Loaded: {available}";
                return documents;
            }

            return matched;
        }

        private static JsonArray ToArray(IEnumerable<string> values) {
            var array = new JsonArray();
            foreach (var value in values)
                array.Add((JsonNode)value);
            return array;
        }
    }
}
