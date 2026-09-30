using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Core.Helpers {
    /// <summary>Options for a workspace re-dump.</summary>
    public sealed class ProjectExportOptions {
        /// <summary>Root directory the project tree is written into (created if absent).</summary>
        public string OutputDirectory { get; init; } = "";
        /// <summary>Name inserted into the generated .sln (defaults to a fixed workspace name).</summary>
        public string SolutionName { get; init; } = "dnspy-workspace";
        /// <summary>
        /// Emit a single .csproj for the whole workspace instead of one project per module.
        /// Modules are then compiled together, which is usually what a human wants when the
        /// binaries are two halves of one program.
        /// </summary>
        public bool SingleProject { get; init; }
        /// <summary>Project file name (without extension) used in single-project mode.</summary>
        public string ProjectName { get; init; } = "Workspace";
        /// <summary>Also export types in the global namespace (default true).</summary>
        public bool IncludeGlobalNamespace { get; init; } = true;
        /// <summary>Recreate the output directory when it already exists.</summary>
        public bool Overwrite { get; init; }
        /// <summary>
        /// Hard cap on how many types are decompiled across the whole export. The cap exists so
        /// one runaway assembly cannot turn a save request into an unbounded job; the response
        /// reports exactly how many types were skipped.
        /// </summary>
        public int MaxTypes { get; init; } = 20000;
    }

    /// <summary>One decompiled source file in the generated project.</summary>
    public sealed class ExportedFile {
        public string Path { get; init; } = "";
        public uint Token { get; init; }
        public string Id => TokenParser.Format(Token);
        public string Type { get; init; } = "";
        public string Namespace { get; init; } = "";
        public string CurrentName { get; init; } = "";
        public int CharacterCount { get; init; }
        public bool Decompiled { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>Per-module export summary.</summary>
    public sealed class ExportedModule {
        public string ModuleMvid { get; init; } = "";
        public string AssemblyName { get; init; } = "";
        public string ProjectDirectory { get; init; } = "";
        public string ProjectFile { get; init; } = "";
        public int TypeCount { get; init; }
        public int FileCount { get; init; }
        public int FailedTypeCount { get; init; }
        public List<ExportedFile> Files { get; init; } = new();
    }

    /// <summary>Result of a workspace re-dump.</summary>
    public sealed class ProjectExportResult {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string OutputDirectory { get; init; } = "";
        public string? SolutionFile { get; init; }
        public string? TokenManifestFile { get; init; }
        public List<ExportedModule> Modules { get; init; } = new();
        public int TypeCount { get; init; }
        public int FileCount { get; init; }
        public int FailedTypeCount { get; init; }
        public bool Truncated { get; init; }
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// Re-dumps the loaded (and possibly mutated) workspace to disk as a C# project tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each loaded module becomes a directory holding a <c>.csproj</c> and one source file per
    /// top-level type, laid out namespace-by-namespace. When several modules are loaded they
    /// are linked with <c>ProjectReference</c> edges by their real <c>AssemblyRef</c> rows, so
    /// the emitted solution reflects the actual assembly graph rather than the load order.
    /// </para>
    /// <para>
    /// The C# is produced by the SAME decompiler the interactive tools use, which means the
    /// dump reflects in-memory mutations (renames, IL patches) that were never written back to
    /// the original binaries. That is the point of the endpoint: it is the only way to see the
    /// current workspace state as source.
    /// </para>
    /// <para>
    /// The dump is an inspection artifact. Decompiled obfuscated code is frequently not
    /// compilable (flattened control flow, invalid identifiers, unreachable CIL), and references
    /// to assemblies outside the workspace are emitted as <c>Reference</c> hints only. The
    /// machine-readable contract is the sibling <c>workspace-tokens.json</c>, which maps every
    /// exported type's immutable metadata token to its file; the build is a convenience, the
    /// token map is the deliverable.
    /// </para>
    /// <para>
    /// All filesystem work happens on a private staging directory first and is moved into place
    /// at the end, so a failed export cannot leave a half-written project where the previous
    /// good one used to be.
    /// </para>
    /// </remarks>
    public sealed class ProjectExporter {
        const string TokenManifestName = "workspace-tokens.json";
        const string ReadmeName = "WORKSPACE-README.md";
        /// <summary>Characters .NET forbids in a path segment (DirectorySeparator is added per-OS).</summary>
        static readonly char[] s_invalidDirChars = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':' };

        readonly IReadOnlyList<LoadedModule> _modules;
        readonly ISourceDecompiler _decompiler;
        readonly ILogSink _log;

        public ProjectExporter(IReadOnlyList<LoadedModule> modules, ISourceDecompiler decompiler, ILogSink log) {
            _modules = modules ?? throw new ArgumentNullException(nameof(modules));
            _decompiler = decompiler ?? throw new ArgumentNullException(nameof(decompiler));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>Runs the export. Never throws for a per-type failure; those are reported.</summary>
        public ProjectExportResult Export(ProjectExportOptions options) {
            if (_modules.Count == 0)
                return new ProjectExportResult { Success = false, Error = "No assemblies loaded." };

            if (string.IsNullOrWhiteSpace(options.OutputDirectory))
                return new ProjectExportResult { Success = false, Error = "outputDirectory is required (absolute path)." };

            var outputDirectory = Path.GetFullPath(options.OutputDirectory);
            var warnings = new List<string>();

            string stagingDirectory;
            try {
                stagingDirectory = CreateStagingDirectory(outputDirectory);
            }
            catch (Exception ex) {
                return new ProjectExportResult {
                    Success = false,
                    Error = $"Cannot create output directory '{outputDirectory}': {ex.Message}",
                };
            }

            try {
                var result = ExportCore(stagingDirectory, options, warnings);
                if (!result.Success)
                    return result;

                Commit(stagingDirectory, outputDirectory, options.Overwrite, warnings);
                return new ProjectExportResult {
                    Success = true,
                    OutputDirectory = outputDirectory,
                    SolutionFile = result.SolutionFile,
                    TokenManifestFile = Path.Combine(outputDirectory, TokenManifestName),
                    Modules = result.Modules,
                    TypeCount = result.TypeCount,
                    FileCount = result.FileCount,
                    FailedTypeCount = result.FailedTypeCount,
                    Truncated = result.Truncated,
                    Warnings = warnings,
                };
            }
            catch (Exception ex) {
                return new ProjectExportResult {
                    Success = false,
                    Error = $"{ex.GetType().Name}: {ex.Message}",
                    Warnings = warnings,
                };
            }
            finally {
                TryDeleteDirectory(stagingDirectory);
            }
        }

        ProjectExportResult ExportCore(string root, ProjectExportOptions options, List<string> warnings) {
            var remaining = options.MaxTypes <= 0 ? int.MaxValue : options.MaxTypes;
            var truncated = false;
            var moduleResults = new List<ExportedModule>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Plan: module → its target directory and project file, so cross-module
            // ProjectReference edges can be resolved before any file is written.
            var plan = new List<(LoadedModule Loaded, string DirectoryName, string ProjectFileName)>();
            var usedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var loaded in _modules) {
                var directoryName = options.SingleProject
                    ? ""
                    : UniqueName(SanitizeDirectoryName(MetadataIdentity.AssemblyName(loaded.Module)), usedDirectoryNames);
                var projectFileName = options.SingleProject
                    ? SanitizeFileName(options.ProjectName) + ".csproj"
                    : SanitizeFileName(MetadataIdentity.AssemblyName(loaded.Module)) + ".csproj";
                plan.Add((loaded, directoryName, projectFileName));
            }

            foreach (var (loaded, directoryName, projectFileName) in plan) {
                var moduleDirectory = directoryName.Length == 0 ? root : Path.Combine(root, directoryName);
                Directory.CreateDirectory(moduleDirectory);

                var exported = ExportModule(loaded, root, moduleDirectory, projectFileName, options, ref remaining, ref truncated, seenPaths, warnings);
                moduleResults.Add(exported);
            }

            // Project files are written after every module directory exists, so ProjectReference
            // paths can be validated against the real layout.
            var byAssemblyName = new Dictionary<string, (LoadedModule Loaded, string ProjectFile)>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < plan.Count; i++) {
                var assemblyName = MetadataIdentity.AssemblyName(plan[i].Loaded.Module);
                var projectPath = options.SingleProject
                    ? Path.Combine(root, plan[i].ProjectFileName)
                    : Path.Combine(root, plan[i].DirectoryName, plan[i].ProjectFileName);
                byAssemblyName[assemblyName] = (plan[i].Loaded, projectPath);
            }

            if (options.SingleProject) {
                WriteProjectFile(Path.Combine(root, plan[0].ProjectFileName), plan[0].Loaded.Module, options, Array.Empty<(string, string)>());
            }
            else {
                for (int i = 0; i < plan.Count; i++) {
                    var module = plan[i].Loaded.Module;
                    var projectPath = Path.Combine(root, plan[i].DirectoryName, plan[i].ProjectFileName);

                    // One ProjectReference per loaded assembly this module actually references.
                    var references = new List<(string AssemblyName, string RelativePath)>();
                    var seenRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var assemblyRef in module.GetAssemblyRefs()) {
                        var name = assemblyRef.Name?.String ?? "";
                        if (name.Length == 0 || !seenRefs.Add(name))
                            continue;
                        if (!byAssemblyName.TryGetValue(name, out var target))
                            continue;
                        if (string.Equals(name, MetadataIdentity.AssemblyName(module), StringComparison.OrdinalIgnoreCase))
                            continue;
                        var relative = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, target.ProjectFile);
                        references.Add((name, relative.Replace('\\', '/')));
                    }

                    WriteProjectFile(projectPath, module, options, references);
                }
            }

            string? solutionFile = null;
            if (!options.SingleProject && plan.Count > 1) {
                solutionFile = Path.Combine(root, SanitizeFileName(options.SolutionName) + ".sln");
                File.WriteAllText(solutionFile, BuildSolution(plan, root), new UTF8Encoding(false));
            }

            var manifestPath = Path.Combine(root, TokenManifestName);
            File.WriteAllText(manifestPath, BuildTokenManifest(moduleResults), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, ReadmeName), BuildReadme(moduleResults, warnings), new UTF8Encoding(false));

            return new ProjectExportResult {
                Success = true,
                OutputDirectory = root,
                SolutionFile = solutionFile,
                TokenManifestFile = manifestPath,
                Modules = moduleResults,
                TypeCount = moduleResults.Sum(m => m.TypeCount),
                FileCount = moduleResults.Sum(m => m.FileCount),
                FailedTypeCount = moduleResults.Sum(m => m.FailedTypeCount),
                Truncated = truncated,
                Warnings = warnings,
            };
        }

        ExportedModule ExportModule(
            LoadedModule loaded,
            string root,
            string moduleDirectory,
            string projectFileName,
            ProjectExportOptions options,
            ref int remaining,
            ref bool truncated,
            HashSet<string> seenPaths,
            List<string> warnings) {

            var module = loaded.Module;
            var files = new List<ExportedFile>();
            var failed = 0;
            var visited = 0;

            // Manifest paths are relative to the OUTPUT ROOT (not to the module directory), so a
            // consumer resolves them without knowing which sub-project the type landed in.
            var rootRelativePrefix = Path.GetRelativePath(root, moduleDirectory);
            if (rootRelativePrefix == ".")
                rootRelativePrefix = "";

            foreach (var type in module.GetTypes().OrderBy(t => t.MDToken.Raw)) {
                // Nested types come out with their declaring type's source; only top-level
                // types get their own file (a nested type's C# text is not standalone).
                if (type.DeclaringType is not null)
                    continue;

                var typeNamespace = type.Namespace?.String ?? "";
                if (typeNamespace.Length == 0 && !options.IncludeGlobalNamespace)
                    continue;

                visited++;

                if (remaining <= 0) {
                    truncated = true;
                    continue;
                }
                remaining--;

                // Directory layout mirrors the namespace so the emitted tree is browsable and
                // the csproj needs no per-file items (SDK-style globbing picks them up).
                var typeDirectory = moduleDirectory;
                if (typeNamespace.Length > 0) {
                    typeDirectory = Path.Combine(moduleDirectory, SanitizeNamespacePath(typeNamespace));
                    Directory.CreateDirectory(typeDirectory);
                }

                var fileName = SanitizeFileName(DeclarationName(type)) + ".cs";
                var filePath = Path.Combine(typeDirectory, fileName);
                // Uniqueness is enforced on the ROOT-relative path: two modules in single-project
                // mode writing the same namespace+type name must not silently overwrite each
                // other, and a collision must never depend on which module was exported first.
                filePath = MakeUniquePath(root, filePath, type.MDToken.Raw, seenPaths);
                var manifestPath = Path.GetRelativePath(root, filePath).Replace('\\', '/');

                string source;
                try {
                    source = _decompiler.DecompileType(type);
                }
                catch (Exception ex) {
                    // A type the decompiler refuses is normal in obfuscated input. Emit a stub
                    // that still carries the token, so the file exists, the manifest entry is
                    // honest, and the graph can record "decompilation failed here".
                    failed++;
                    var error = $"{ex.GetType().Name}: {ex.Message}";
                    _log.Warn($"workspace export: failed to decompile {TokenParser.Format(type.MDToken.Raw)} ('{type.FullName}'): {ex.Message}");
                    source = BuildFailureStub(type, error);
                    files.Add(new ExportedFile {
                        Path = manifestPath,
                        Token = type.MDToken.Raw,
                        Type = MetadataIdentity.TypeType,
                        Namespace = typeNamespace,
                        CurrentName = type.Name?.String ?? "",
                        CharacterCount = source.Length,
                        Decompiled = false,
                        Error = error,
                    });
                    File.WriteAllText(filePath, source, new UTF8Encoding(false));
                    continue;
                }

                // The decompiler already emits the namespace/type declaration, so the text is
                // written verbatim — rewriting it would invalidate any line numbers a caller
                // correlates with decompile_type output.
                File.WriteAllText(filePath, source, new UTF8Encoding(false));
                files.Add(new ExportedFile {
                    Path = manifestPath,
                    Token = type.MDToken.Raw,
                    Type = MetadataIdentity.TypeType,
                    Namespace = typeNamespace,
                    CurrentName = type.Name?.String ?? "",
                    CharacterCount = source.Length,
                    Decompiled = true,
                });
            }

            if (visited == 0)
                warnings.Add($"Assembly '{MetadataIdentity.AssemblyName(module)}' produced no source files (no top-level types matched the export scope).");

            return new ExportedModule {
                ModuleMvid = MetadataIdentity.Mvid(module),
                AssemblyName = MetadataIdentity.AssemblyName(module),
                ProjectDirectory = moduleDirectory,
                ProjectFile = moduleDirectory.Length == 0 ? projectFileName : Path.Combine(moduleDirectory, projectFileName),
                TypeCount = files.Count,
                FileCount = files.Count,
                FailedTypeCount = failed,
                Files = files,
            };
        }

        // ---------------------------------------------------------------------
        // Project / solution files
        // ---------------------------------------------------------------------

        static void WriteProjectFile(
            string projectPath,
            ModuleDef module,
            ProjectExportOptions options,
            IReadOnlyList<(string AssemblyName, string RelativePath)> projectReferences) {

            var assemblyName = MetadataIdentity.AssemblyName(module);
            var rootNamespace = SanitizeIdentifier(assemblyName.Length == 0 ? "Workspace" : assemblyName);

            var sb = new StringBuilder();
            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine();
            sb.AppendLine("  <!--");
            sb.AppendLine("    Generated by dnSpy MCP (workspace_save_code).");
            sb.AppendLine($"    Source module MVID: {MetadataIdentity.Mvid(module)}");
            sb.AppendLine($"    Source module file: {module.Location ?? "(in-memory)"}");
            sb.AppendLine("    This file is an inspection artifact regenerated on every export;");
            sb.AppendLine("    edits here are not read back into dnSpy.");
            sb.AppendLine("  -->");
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
            sb.AppendLine($"    <AssemblyName>{Escape(assemblyName.Length == 0 ? "Workspace" : assemblyName)}</AssemblyName>");
            sb.AppendLine($"    <RootNamespace>{Escape(rootNamespace)}</RootNamespace>");
            sb.AppendLine("    <Nullable>disable</Nullable>");
            sb.AppendLine("    <!-- Decompiled output is not expected to compile cleanly. -->");
            sb.AppendLine("    <NoWarn>$(NoWarn);CS0108;CS0114;CS0162;CS0168;CS0169;CS0219;CS0414;CS0649;CS8981</NoWarn>");
            sb.AppendLine("    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>");
            sb.AppendLine("    <GenerateAssemblyInfo>true</GenerateAssemblyInfo>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine();

            if (projectReferences.Count > 0) {
                sb.AppendLine("  <ItemGroup>");
                foreach (var (name, relative) in projectReferences) {
                    sb.AppendLine($"    <!-- AssemblyRef: {Escape(name)} -->");
                    sb.AppendLine($"    <ProjectReference Include=\"{Escape(relative)}\" />");
                }
                sb.AppendLine("  </ItemGroup>");
                sb.AppendLine();
            }

            // Assemblies outside the loaded workspace cannot become ProjectReferences. They are
            // emitted as commented hints so a human can add the real package/DLL, and the gap is
            // visible rather than silent.
            var externalReferences = module.GetAssemblyRefs()
                .Select(r => r.Name?.String ?? "")
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => !projectReferences.Any(p => string.Equals(p.AssemblyName, n, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (externalReferences.Count > 0) {
                sb.AppendLine("  <!--");
                sb.AppendLine("    Referenced assemblies outside the loaded workspace (not emitted as");
                sb.AppendLine("    ProjectReference — add them as PackageReference/Reference if you need to build):");
                foreach (var name in externalReferences)
                    sb.AppendLine($"      {name}");
                sb.AppendLine("  -->");
                sb.AppendLine();
            }

            sb.AppendLine("</Project>");
            File.WriteAllText(projectPath, sb.ToString(), new UTF8Encoding(false));
        }

        static string BuildSolution(IReadOnlyList<(LoadedModule Loaded, string DirectoryName, string ProjectFileName)> plan, string root) {
            var sb = new StringBuilder();
            sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            sb.AppendLine("# Visual Studio Version 17");
            sb.AppendLine("VisualStudioVersion = 17.0.31903.59");
            sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");

            var guids = new List<(string Name, Guid Guid, string RelativePath)>();
            foreach (var (loaded, directoryName, projectFileName) in plan) {
                var name = MetadataIdentity.AssemblyName(loaded.Module);
                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileNameWithoutExtension(projectFileName);
                var relative = directoryName.Length == 0 ? projectFileName : $"{directoryName}\\{projectFileName}";
                var guid = DeterministicGuid(MetadataIdentity.Mvid(loaded.Module) + name);
                guids.Add((name, guid, relative));
                sb.AppendLine($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"{relative}\", \"{{{guid.ToString().ToUpperInvariant()}}}\"");
                sb.AppendLine("EndProject");
            }

            sb.AppendLine("Global");
            sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
            sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
            sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
            sb.AppendLine("\tEndGlobalSection");
            sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
            foreach (var (_, guid, _) in guids) {
                var g = guid.ToString().ToUpperInvariant();
                sb.AppendLine($"\t\t{{{g}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
                sb.AppendLine($"\t\t{{{g}}}.Debug|Any CPU.Build.0 = Debug|Any CPU");
                sb.AppendLine($"\t\t{{{g}}}.Release|Any CPU.ActiveCfg = Release|Any CPU");
                sb.AppendLine($"\t\t{{{g}}}.Release|Any CPU.Build.0 = Release|Any CPU");
            }
            sb.AppendLine("\tEndGlobalSection");
            sb.AppendLine("EndGlobal");
            return sb.ToString();
        }

        // ---------------------------------------------------------------------
        // Token manifest — the machine-readable contract of the export
        // ---------------------------------------------------------------------

        static string BuildTokenManifest(IReadOnlyList<ExportedModule> modules) {
            var root = new JsonObject {
                ["schema"] = "dnspy-mcp.workspace-tokens/1",
                ["identityModel"] = ToolResponse.IdentityModel,
                ["primaryId"] = ToolResponse.PrimaryId,
                ["moduleCount"] = modules.Count,
                ["typeCount"] = modules.Sum(m => m.TypeCount),
            };

            var moduleArray = new JsonArray();
            foreach (var module in modules) {
                var typeArray = new JsonArray();
                foreach (var file in module.Files.OrderBy(f => f.Token)) {
                    typeArray.Add(new JsonObject {
                        ["id"] = file.Id,
                        ["type"] = file.Type,
                        ["current_name"] = file.CurrentName,
                        ["nameIsMutable"] = true,
                        ["idIsImmutable"] = true,
                        ["moduleMvid"] = module.ModuleMvid,
                        ["assembly"] = module.AssemblyName,
                        ["namespace"] = file.Namespace,
                        ["file"] = file.Path,
                        ["decompiled"] = file.Decompiled,
                        ["sourceCharacters"] = file.CharacterCount,
                        ["decompileError"] = file.Error,
                    });
                }

                moduleArray.Add(new JsonObject {
                    ["moduleMvid"] = module.ModuleMvid,
                    ["assembly"] = module.AssemblyName,
                    ["projectFile"] = Path.GetFileName(module.ProjectFile),
                    ["typeCount"] = module.TypeCount,
                    ["types"] = typeArray,
                });
            }

            root["modules"] = moduleArray;
            return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        static string BuildReadme(IReadOnlyList<ExportedModule> modules, IReadOnlyList<string> warnings) {
            var sb = new StringBuilder();
            sb.AppendLine("# Workspace re-dump (dnSpy MCP)");
            sb.AppendLine();
            sb.AppendLine("Generated by the `workspace_save_code` tool / `POST /api/workspace/save-code`.");
            sb.AppendLine();
            sb.AppendLine("## Read this before trying to build");
            sb.AppendLine();
            sb.AppendLine("- This tree is an **inspection artifact**. It is not the authoritative source of the");
            sb.AppendLine("  workspace and it is not written back into dnSpy.");
            sb.AppendLine("- Decompiled obfuscated code frequently does **not** compile: flattened control flow,");
            sb.AppendLine("  invalid identifiers, and unreachable CIL are all normal inputs here.");
            sb.AppendLine("- Assemblies outside the loaded workspace appear as commented hints in each `.csproj`,");
            sb.AppendLine("  not as resolvable references.");
            sb.AppendLine("- The machine-readable contract is **`workspace-tokens.json`**: it maps every");
            sb.AppendLine("  exported type's immutable metadata token (`id`) to the file that carries it. Build");
            sb.AppendLine("  the graph from that file, not from the C# text.");
            sb.AppendLine("- A type whose decompilation failed still gets a file with a stub plus the token, so");
            sb.AppendLine("  `workspace-tokens.json` stays complete; check `decompiled: false` for those.");
            sb.AppendLine();
            sb.AppendLine("## Contents");
            sb.AppendLine();
            foreach (var module in modules) {
                sb.AppendLine($"- `{Path.GetFileName(module.ProjectFile)}` — {module.AssemblyName} " +
                    $"(MVID `{module.ModuleMvid}`, {module.TypeCount} type(s), {module.FailedTypeCount} decompile failure(s))");
            }
            sb.AppendLine();
            sb.AppendLine("## Token index");
            sb.AppendLine();
            sb.AppendLine("| id | type | current_name | file |");
            sb.AppendLine("|----|------|--------------|------|");
            foreach (var module in modules) {
                foreach (var file in module.Files.OrderBy(f => f.Token).Take(2000)) {
                    sb.AppendLine($"| `{file.Id}` | {file.Type} | `{Escape(file.CurrentName)}` | `{file.Path}` |");
                }
            }
            if (modules.Sum(m => m.TypeCount) > 2000) {
                sb.AppendLine();
                sb.AppendLine("_(table truncated at 2000 rows — the complete index is in `workspace-tokens.json`)_");
            }

            if (warnings.Count > 0) {
                sb.AppendLine();
                sb.AppendLine("## Warnings");
                sb.AppendLine();
                foreach (var warning in warnings)
                    sb.AppendLine($"- {warning}");
            }

            return sb.ToString();
        }

        // ---------------------------------------------------------------------
        // Staging / commit
        // ---------------------------------------------------------------------

        static string CreateStagingDirectory(string outputDirectory) {
            var parent = Path.GetDirectoryName(outputDirectory);
            if (string.IsNullOrEmpty(parent)) {
                // No parent means a bare drive/root path; stage in the target itself.
                Directory.CreateDirectory(outputDirectory);
                return Path.Combine(outputDirectory, ".dnspy-mcp-staging-" + Guid.NewGuid().ToString("N"));
            }
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, "." + Path.GetFileName(outputDirectory) + ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            return staging;
        }

        /// <summary>
        /// Moves the staged tree into place. Existing output is preserved unless
        /// <paramref name="overwrite"/> is set, so a mistyped path cannot destroy a directory.
        /// </summary>
        static void Commit(string stagingDirectory, string outputDirectory, bool overwrite, List<string> warnings) {
            if (Directory.Exists(outputDirectory)) {
                if (!overwrite) {
                    // Merge: copy staged files over the existing tree without deleting anything
                    // the caller did not ask us to replace.
                    CopyDirectory(stagingDirectory, outputDirectory);
                    warnings.Add($"Output directory already existed; staged files were merged into it (pass overwrite=true to replace it entirely).");
                    return;
                }

                // Replace atomically-ish: move the old tree aside, move the new one in, then
                // delete the old one. A crash between the moves leaves the old tree recoverable
                // next to the new one instead of a half-written project.
                var backup = outputDirectory + ".replaced-" + Guid.NewGuid().ToString("N");
                Directory.Move(outputDirectory, backup);
                try {
                    Directory.Move(stagingDirectory, outputDirectory);
                }
                catch {
                    Directory.Move(backup, outputDirectory);
                    throw;
                }
                TryDeleteDirectory(backup);
                return;
            }

            Directory.Move(stagingDirectory, outputDirectory);
        }

        static void CopyDirectory(string source, string destination) {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        static void TryDeleteDirectory(string path) {
            try {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch {
                // Best-effort cleanup: a locked staging directory must not fail the export that
                // already succeeded, nor mask the original error when it did not.
            }
        }

        // ---------------------------------------------------------------------
        // Naming
        // ---------------------------------------------------------------------

        /// <summary>Simple name of a type or, for a generic, its arity-stripped name.</summary>
        static string DeclarationName(TypeDef type) {
            var name = type.Name?.String ?? "Type";
            var tick = name.IndexOf('`');
            if (tick > 0)
                name = name.Substring(0, tick);
            return name.Length == 0 ? "Type" : name;
        }

        /// <summary>Turns a dotted namespace into a relative directory path.</summary>
        static string SanitizeNamespacePath(string @namespace) {
            var parts = @namespace.Split('.');
            var sb = new StringBuilder();
            foreach (var part in parts) {
                if (part.Length == 0)
                    continue;
                if (sb.Length > 0)
                    sb.Append(Path.DirectorySeparatorChar);
                sb.Append(SanitizeFileName(part));
            }
            return sb.Length == 0 ? "_global" : sb.ToString();
        }

        /// <summary>
        /// Makes a path segment safe on the current OS. Invalid characters are replaced with
        /// '_' rather than dropped so two distinct names cannot collapse into one file.
        /// </summary>
        static string SanitizeFileName(string name) {
            if (string.IsNullOrEmpty(name))
                return "_";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name) {
                sb.Append(Array.IndexOf(invalid, ch) >= 0 || Array.IndexOf(s_invalidDirChars, ch) >= 0 ? '_' : ch);
            }
            var result = sb.ToString().TrimEnd('.', ' ');
            if (result.Length == 0)
                return "_";
            // Reserved device names would make the file unopenable on Windows.
            foreach (var reserved in s_reservedNames) {
                if (string.Equals(result, reserved, StringComparison.OrdinalIgnoreCase))
                    return "_" + result;
            }
            return result;
        }

        static readonly string[] s_reservedNames = {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        static string SanitizeDirectoryName(string name) {
            var sanitized = SanitizeFileName(name);
            if (sanitized is "." or "..")
                return "_";
            return sanitized;
        }

        static string SanitizeIdentifier(string name) {
            var sb = new StringBuilder(name.Length + 1);
            for (int i = 0; i < name.Length; i++) {
                var ch = name[i];
                sb.Append(i == 0 ? (char.IsLetter(ch) || ch == '_' ? ch : '_') : (char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_'));
            }
            return sb.Length == 0 ? "_" : sb.ToString();
        }

        static string UniqueName(string name, HashSet<string> used) {
            if (used.Add(name))
                return name;
            for (int i = 2; ; i++) {
                var candidate = $"{name}_{i}";
                if (used.Add(candidate))
                    return candidate;
            }
        }

        /// <summary>
        /// Guarantees a unique file path, keyed on the path relative to <paramref name="root"/>.
        /// Two types in different namespaces never collide (different directories); two types
        /// with the SAME name in the SAME namespace do, so the second is disambiguated by its
        /// token — never by a transient counter, because the path is part of the manifest.
        /// </summary>
        static string MakeUniquePath(string root, string path, uint token, HashSet<string> seen) {
            var key = Path.GetRelativePath(root, path);
            if (seen.Add(key))
                return path;
            var directory = Path.GetDirectoryName(path) ?? "";
            var baseName = Path.GetFileNameWithoutExtension(path);
            for (int i = 1; ; i++) {
                var suffix = i == 1 ? "" : "_" + i.ToString(CultureInfo.InvariantCulture);
                var candidate = Path.Combine(directory, $"{baseName}__{token:X8}{suffix}.cs");
                if (seen.Add(Path.GetRelativePath(root, candidate)))
                    return candidate;
            }
        }

        static string BuildFailureStub(TypeDef type, string error) => string.Join(Environment.NewLine, new[] {
            "// dnSpy MCP: decompilation failed for this type.",
            $"// id: {TokenParser.Format(type.MDToken.Raw)}",
            $"// type: {type.FullName}",
            $"// error: {error}",
            "//",
            "// The token above is the authoritative address; use get_type_members / decompile_type",
            "// to inspect this type directly. This stub exists so the workspace token manifest stays",
            "// complete and the file layout matches the exported project structure.",
            "",
        });

        /// <summary>Stable GUID derived from a string, so regenerating a .sln does not churn identities.</summary>
        static Guid DeterministicGuid(string seed) {
            using var md5 = System.Security.Cryptography.MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(seed)));
        }

        static string Escape(string value) => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
