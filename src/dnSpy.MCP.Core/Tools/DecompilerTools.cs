using System;
using System.ComponentModel;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    /// <summary>
    /// Decompilation tools. Every target is addressed by metadata token; every
    /// response carries the target identity (token + name).
    /// </summary>
    public sealed class DecompilerTools {
        const string ToolDecompileMethod = "decompile_method";
        const string ToolDecompileType = "decompile_type";
        const string ToolDecompileAssembly = "decompile_assembly";

        private readonly McpContext _ctx;
        public DecompilerTools(McpContext ctx) => _ctx = ctx;

        [Description("Decompile one method to C# source. Address it by .NET metadata token (hex '0x06000001' or decimal); discover tokens with search_methods. Returns the method identity (token + name) plus the decompiled source.")]
        public string DecompileMethod(
            [Description("Metadata token of the MethodDef, e.g. '0x06000001' or '100663297'")] string token,
            [Description("Optional module MVID (emitted in every identity payload) to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolDecompileMethod, "No assemblies loaded.");

            var method = _ctx.Resolver.ResolveAs<MethodDef>(token, moduleMvid, out _, out var error);
            if (method is null)
                return ToolResponse.Failure(ToolDecompileMethod, error!);

            var identity = MetadataIdentity.ForMethod(method);
            try {
                var source = _ctx.SourceDecompiler.DecompileMethod(method);
                return ToolResponse.Success(ToolDecompileMethod, identity, new JsonObject {
                    ["language"] = "C#",
                    ["source"] = source,
                });
            }
            catch (Exception ex) {
                return ToolResponse.Failure(ToolDecompileMethod,
                    $"Decompilation failed for {TokenParser.Format(method.MDToken.Raw)} ('{method.Name}'): {ex.Message}");
            }
        }

        [Description("Decompile an entire type (all members) to C# source. Address it by .NET metadata token (hex '0x02000001' or decimal); discover tokens with search_types or assembly_list_types. Returns the type identity (token + name) plus the decompiled source.")]
        public string DecompileType(
            [Description("Metadata token of the TypeDef, e.g. '0x02000001' or '33554433'")] string token,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolDecompileType, "No assemblies loaded.");

            var type = _ctx.Resolver.ResolveAs<TypeDef>(token, moduleMvid, out _, out var error);
            if (type is null)
                return ToolResponse.Failure(ToolDecompileType, error!);

            var identity = MetadataIdentity.ForType(type);
            try {
                var source = _ctx.SourceDecompiler.DecompileType(type);
                return ToolResponse.Success(ToolDecompileType, identity, new JsonObject {
                    ["language"] = "C#",
                    ["source"] = source,
                });
            }
            catch (Exception ex) {
                return ToolResponse.Failure(ToolDecompileType,
                    $"Decompilation failed for {TokenParser.Format(type.MDToken.Raw)} ('{type.FullName}'): {ex.Message}");
            }
        }

        [Description("Decompile the whole assembly (first 10 types). Returns the module identity (moduleMvid + name) and, for each sampled type, its metadata token + name + decompiled source. May be slow for large assemblies.")]
        public string DecompileAssembly() {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolDecompileAssembly, "No assemblies loaded.");

            var modules = ToolResponse.EmptyArray();
            var typeItems = ToolResponse.EmptyArray();
            var count = 0;
            var truncated = false;

            foreach (var mod in _ctx.Resolver.GetAllModules()) {
                modules.Add(MetadataIdentity.ForModule(mod));

                foreach (var type in mod.GetTypes()) {
                    if (type.Name.String.StartsWith("<", StringComparison.Ordinal))
                        continue;
                    if (count >= MaxAssemblySample) {
                        truncated = true;
                        break;
                    }
                    try {
                        var item = MetadataIdentity.ForType(type);
                        item["source"] = _ctx.SourceDecompiler.DecompileType(type);
                        typeItems.Add(item);
                        count++;
                    }
                    catch (Exception ex) {
                        // Some types (compiler-generated async state machines, anonymous
                        // types, ...) fail to decompile cleanly. Skip them so the overview
                        // still returns useful output.
                        _ctx.Log.Warn($"Skipped type '{type.FullName}' during assembly overview: {ex.Message}");
                    }
                }
                if (truncated) break;
            }

            return ToolResponse.Success(ToolDecompileAssembly, new JsonObject {
                ["modules"] = modules,
                ["types"] = typeItems,
                ["typeCount"] = count,
                ["truncated"] = truncated,
            });
        }

        /// <summary>How many types decompile_assembly samples before it stops.</summary>
        const int MaxAssemblySample = 10;
    }
}
