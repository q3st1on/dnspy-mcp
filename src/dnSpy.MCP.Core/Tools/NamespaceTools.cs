using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    /// <summary>
    /// Namespace inventory. Namespaces have no metadata row of their own, so each is
    /// identified by its anchor type token (lowest TypeDef token in the namespace);
    /// every member type is reported with its own token.
    /// </summary>
    public sealed class NamespaceTools {
        const string ToolGetGlobalNamespaces = "get_global_namespaces";

        private readonly McpContext _ctx;
        public NamespaceTools(McpContext ctx) => _ctx = ctx;

        [Description("List every type that has no explicit namespace (the global namespace). Each type is reported with its metadata token + name, grouped under a namespace identity whose token is the anchor (lowest) TypeDef token of that namespace.")]
        public string GetGlobalNamespaces() {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetGlobalNamespaces, "No assemblies loaded.");

            var groups = new List<JsonObject>();
            var total = 0;

            foreach (var module in _ctx.Resolver.GetAllModules()) {
                var types = module.GetTypes()
                    .Where(t => string.IsNullOrEmpty(t.Namespace?.String))
                    .OrderBy(t => t.MDToken.Raw)
                    .ToList();
                if (types.Count == 0)
                    continue;

                total += types.Count;
                groups.Add(new JsonObject {
                    ["namespace"] = MetadataIdentity.ForNamespace(module, ""),
                    ["count"] = types.Count,
                    ["types"] = ToolResponse.Array(types.Select(MetadataIdentity.ForType)),
                });
            }

            if (total == 0)
                return ToolResponse.Failure(ToolGetGlobalNamespaces, "No types in the global namespace.");

            return ToolResponse.Success(ToolGetGlobalNamespaces, new JsonObject {
                ["typeCount"] = total,
                ["moduleCount"] = groups.Count,
                ["groups"] = ToolResponse.Array(groups),
            });
        }
    }
}
