using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    /// <summary>
    /// Embedded resource / PE metadata inspection. Resources live in the
    /// ManifestResource metadata table, so they are addressed by their own metadata
    /// token (0x28xxxxxx) — not by name.
    /// </summary>
    public sealed class ResourceTools {
        const string ToolGetResources = "get_resources";
        const string ToolGetResourceData = "get_resource_data";
        const string ToolGetMetadata = "get_metadata";

        private readonly McpContext _ctx;
        public ResourceTools(McpContext ctx) => _ctx = ctx;

        [Description("List every embedded resource with its metadata token (manifest-resource table) + name, grouped under the owning module identity (moduleMvid). Pass the token to get_resource_data to dump bytes.")]
        public string GetResources() {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetResources, "No assemblies loaded.");

            var modules = ToolResponse.EmptyArray();
            var total = 0;

            foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                if (loaded.Module is not ModuleDef mod) continue;

                var resources = ToolResponse.EmptyArray();
                foreach (var resource in mod.Resources) {
                    var item = MetadataIdentity.ForResource(resource, mod);
                    resources.Add(item);
                    total++;
                }

                modules.Add(new JsonObject {
                    ["module"] = MetadataIdentity.ForModule(mod),
                    ["count"] = mod.Resources.Count,
                    ["resources"] = resources,
                });
            }

            if (total == 0)
                return ToolResponse.Failure(ToolGetResources, "No resources found.");

            return ToolResponse.Success(ToolGetResources, new JsonObject {
                ["resourceCount"] = total,
                ["modules"] = modules,
            });
        }

        [Description("Dump the raw bytes (hex) of one embedded resource, addressed by its metadata token from get_resources. maxLength caps how many bytes are dumped (default 512, hard cap 4096).")]
        public string GetResourceData(
            [Description("Metadata token of the resource, e.g. '0x28000001'")] string token,
            [Description("Maximum number of bytes to dump")] int maxLength = 512,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetResourceData, "No assemblies loaded.");

            // Clamp to a sane range so callers can't request gigabytes of hex.
            var cap = Math.Max(0, Math.Min(maxLength, DataDumpHardCap));

            var resource = _ctx.Resolver.ResolveResource(token, moduleMvid, out var module, out var error);
            if (resource is null)
                return ToolResponse.Failure(ToolGetResourceData, error!);

            var identity = MetadataIdentity.ForResource(resource, module);
            if (resource is not EmbeddedResource embedded)
                return ToolResponse.Failure(ToolGetResourceData,
                    $"Resource {TokenParser.Format(resource.MDToken.Raw)} ('{resource.Name}') is {resource.ResourceType}, not an embedded resource.");

            var data = embedded.CreateReader().ToArray();
            var shown = Math.Min(cap, data.Length);

            return ToolResponse.Success(ToolGetResourceData, identity, new JsonObject {
                ["size"] = data.Length,
                ["dumpedBytes"] = shown,
                ["truncated"] = data.Length > shown,
                ["hex"] = BitConverter.ToString(data.Take(shown).ToArray()),
            });
        }

        const int DataDumpHardCap = 4096;

        [Description("PE and metadata info: module identity (moduleMvid), runtime version, assembly name/version/culture, entry point (with token), and on-disk PE headers. Optionally scoped to one assembly by simple name.")]
        public string GetMetadata(
            [Description("Optional assembly simple name to scope to")] string? assembly = null) {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetMetadata, "No assemblies loaded.");

            foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                if (loaded.Module is not ModuleDef mod) continue;

                if (!string.IsNullOrEmpty(assembly)
                    && !string.Equals(MetadataIdentity.AssemblyName(mod), assembly, StringComparison.OrdinalIgnoreCase))
                    continue;

                var result = new JsonObject {
                    ["moduleVersionId"] = $"{mod.Mvid:D}",
                    ["runtimeVersion"] = mod.RuntimeVersion ?? "",
                    ["assemblyName"] = mod.Assembly?.Name?.String ?? "",
                    ["assemblyFullName"] = mod.Assembly?.FullName ?? "",
                    ["version"] = mod.Assembly?.Version?.ToString() ?? "",
                    ["culture"] = mod.Assembly?.Culture?.String ?? "",
                    ["entryPointToken"] = mod.EntryPoint is null ? null : mod.EntryPoint.MDToken.Raw,
                    ["entryPoint"] = mod.EntryPoint is null ? null : $"{mod.EntryPoint.DeclaringType?.FullName}::{mod.EntryPoint.Name}",
                };

                AppendPeHeaders(result, loaded.Path);
                return ToolResponse.Success(ToolGetMetadata, MetadataIdentity.ForModule(mod), result);
            }

            return ToolResponse.Failure(ToolGetMetadata, string.IsNullOrEmpty(assembly)
                ? "No assembly loaded."
                : $"Assembly '{assembly}' not found. Loaded modules: {_ctx.Resolver.DescribeModules()}");
        }

        /// <summary>
        /// Reads PE headers from the on-disk file via <see cref="PEReader"/>. dnSpy's
        /// <c>IDsDocument.PEImage</c> is not exposed by <see cref="Abstractions.LoadedModule"/>;
        /// for in-memory modules the path is empty and PE headers are skipped rather than
        /// crashing on <see cref="File.OpenRead"/>.
        /// </summary>
        static void AppendPeHeaders(JsonObject result, string path) {
            if (string.IsNullOrEmpty(path)) {
                result["peHeaders"] = null;
                result["peHeadersNote"] = "in-memory module, no on-disk image";
                return;
            }

            try {
                using var fs = File.OpenRead(path);
                using var peReader = new PEReader(fs);
                var headers = peReader.PEHeaders;

                var sections = ToolResponse.EmptyArray();
                foreach (var section in headers.SectionHeaders) {
                    sections.Add(new JsonObject {
                        ["name"] = section.Name,
                        ["virtualSize"] = section.VirtualSize,
                        ["rawSize"] = section.SizeOfRawData,
                    });
                }

                result["peHeaders"] = new JsonObject {
                    ["machine"] = headers.CoffHeader?.Machine.ToString(),
                    ["sections"] = sections,
                };
            }
            catch (Exception ex) {
                result["peHeaders"] = null;
                result["peHeadersNote"] = $"failed to read '{path}': {ex.Message}";
            }
        }
    }
}
