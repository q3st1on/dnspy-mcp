using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    /// <summary>
    /// Discovery tools. These are the ONE place a string pattern is legitimate:
    /// they exist to turn a human-supplied name pattern into metadata tokens, which
    /// every other tool then uses as the address. Every result item therefore carries
    /// its token (the address) next to its name (mutable metadata).
    /// </summary>
    public sealed class SearchTools {
        const string ToolSearchTypes = "search_types";
        const string ToolSearchMethods = "search_methods";
        const string ToolSearchStrings = "search_strings";
        const string ToolGrep = "grep";

        private readonly McpContext _ctx;
        public SearchTools(McpContext ctx) => _ctx = ctx;

        [Description("Find types by name pattern ('regex:' prefix for regex). Discovery only — returns each match's metadata token + name; address the type by that token in every later call. Optional assembly (simple name) scopes the search across loaded binaries.")]
        public string SearchTypes(
            [Description("Name pattern; 'regex:<expr>' for a regular expression")] string pattern,
            [Description("Optional substring filter on the namespace")] string? namespaceFilter = null,
            [Description("Optional assembly simple name to scope the search (discovery scoping, not element addressing)")] string? assembly = null) {

            if (string.IsNullOrWhiteSpace(pattern))
                return ToolResponse.Failure(ToolSearchTypes, "pattern is required.");

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolSearchTypes, "No assemblies loaded.");

            var types = _ctx.Resolver.SearchTypes(pattern, assembly).ToList();
            if (!string.IsNullOrEmpty(namespaceFilter))
                types = types.Where(t => !UTF8String.IsNullOrEmpty(t.Namespace)
                    && UTF8String.ToSystemStringOrEmpty(t.Namespace).IndexOf(namespaceFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var items = types
                .OrderBy(t => t.MDToken.Raw)
                .Take(MaxResults)
                .Select(MetadataIdentity.ForType)
                .ToList();

            return ToolResponse.Success(ToolSearchTypes, new JsonObject {
                ["pattern"] = pattern,
                ["matchCount"] = types.Count,
                ["returnedCount"] = items.Count,
                ["truncated"] = types.Count > items.Count,
                ["items"] = ToolResponse.Array(items),
            });
        }

        [Description("Find methods by name pattern ('regex:' prefix for regex). Discovery only — returns each match's metadata token + name; address the method by that token in every later call. Pass typeToken to scope the search to one type; moduleMvid qualifies that type token.")]
        public string SearchMethods(
            [Description("Name pattern; 'regex:<expr>' for a regular expression")] string pattern,
            [Description("Optional metadata token of a TypeDef to scope the search to that type")] string? typeToken = null,
            [Description("Optional module MVID qualifying typeToken")] string? moduleMvid = null,
            [Description("Optional assembly simple name to scope the search (discovery scoping, not element addressing)")] string? assembly = null) {

            if (string.IsNullOrWhiteSpace(pattern))
                return ToolResponse.Failure(ToolSearchMethods, "pattern is required.");

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolSearchMethods, "No assemblies loaded.");

            TypeDef? scopeType = null;
            JsonObject? scopeIdentity = null;
            if (!string.IsNullOrWhiteSpace(typeToken)) {
                scopeType = _ctx.Resolver.ResolveAs<TypeDef>(typeToken, moduleMvid, out _, out var scopeError);
                if (scopeType is null)
                    return ToolResponse.Failure(ToolSearchMethods, scopeError!);
                scopeIdentity = MetadataIdentity.ForType(scopeType);
            }

            var methods = _ctx.Resolver.SearchMethods(pattern, scopeType, scopeType is null ? assembly : null).ToList();
            var items = methods
                .OrderBy(m => m.MDToken.Raw)
                .Take(MaxResults)
                .Select(MetadataIdentity.ForMethod)
                .ToList();

            var result = new JsonObject {
                ["pattern"] = pattern,
                ["matchCount"] = methods.Count,
                ["returnedCount"] = items.Count,
                ["truncated"] = methods.Count > items.Count,
                ["items"] = ToolResponse.Array(items),
            };
            if (scopeIdentity is not null)
                result["scope"] = scopeIdentity;

            return ToolResponse.Success(ToolSearchMethods, result);
        }

        [Description("Find string literals in IL. Discovery only — returns the literal plus the metadata token + name of the method that loads it. Optional assembly (simple name) scopes the search.")]
        public string SearchStrings(
            [Description("Optional substring to match inside the literal")] string? pattern = null,
            [Description("Minimum literal length to report")] int minLength = 4,
            [Description("Optional assembly simple name to scope the search")] string? assembly = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolSearchStrings, "No assemblies loaded.");

            var occurrences = new List<JsonObject>();
            var distinctValues = new HashSet<string>(StringComparer.Ordinal);

            foreach (var mod in _ctx.Resolver.GetModules(assembly)) {
                foreach (var type in mod.GetTypes()) {
                    foreach (var method in type.Methods) {
                        if (method.Body is null) continue;
                        foreach (var instr in method.Body.Instructions) {
                            if (instr.OpCode != OpCodes.Ldstr || instr.Operand is not string literal)
                                continue;
                            if (literal.Length < minLength)
                                continue;
                            if (pattern is not null && literal.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            distinctValues.Add(literal);
                            if (occurrences.Count >= MaxResults)
                                continue;

                            var item = MetadataIdentity.ForMethod(method);
                            item["value"] = literal;
                            item["offset"] = $"IL_{instr.Offset:X4}";
                            occurrences.Add(item);
                        }
                    }
                }
            }

            if (occurrences.Count == 0)
                return ToolResponse.Failure(ToolSearchStrings, pattern is null
                    ? $"No string literals found (min length: {minLength})."
                    : $"No string literals matching '{pattern}'.");

            return ToolResponse.Success(ToolSearchStrings, new JsonObject {
                ["pattern"] = pattern,
                ["minLength"] = minLength,
                ["distinctValueCount"] = distinctValues.Count,
                ["returnedCount"] = occurrences.Count,
                ["truncated"] = distinctValues.Count > occurrences.Count,
                ["items"] = ToolResponse.Array(occurrences),
            });
        }

        [Description("Search across type names, method names, and string literals. Discovery only — every type/method item carries its metadata token + name, every string item carries the loading method's token.")]
        public string Grep(
            [Description("Name/value pattern")] string pattern,
            [Description("Scope: 'all', 'types', 'methods', or 'strings'")] string scope = "all",
            [Description("Optional assembly simple name to scope the search")] string? assembly = null) {

            if (string.IsNullOrWhiteSpace(pattern))
                return ToolResponse.Failure(ToolGrep, "pattern is required.");

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGrep, "No assemblies loaded.");

            var result = new JsonObject { ["pattern"] = pattern, ["scope"] = scope };
            var total = 0;

            if (scope is "all" or "types") {
                var types = _ctx.Resolver.SearchTypes(pattern, assembly).ToList();
                result["types"] = ToolResponse.Array(types.OrderBy(t => t.MDToken.Raw).Take(MaxResults).Select(MetadataIdentity.ForType));
                result["typeCount"] = types.Count;
                total += types.Count;
            }

            if (scope is "all" or "methods") {
                var methods = _ctx.Resolver.SearchMethods(pattern, null, assembly).ToList();
                result["methods"] = ToolResponse.Array(methods.OrderBy(m => m.MDToken.Raw).Take(MaxResults).Select(MetadataIdentity.ForMethod));
                result["methodCount"] = methods.Count;
                total += methods.Count;
            }

            if (scope is "all" or "strings") {
                var strings = new List<JsonObject>();
                foreach (var mod in _ctx.Resolver.GetModules(assembly)) {
                    foreach (var type in mod.GetTypes()) {
                        foreach (var method in type.Methods) {
                            if (method.Body is null) continue;
                            foreach (var instr in method.Body.Instructions) {
                                if (instr.OpCode != OpCodes.Ldstr || instr.Operand is not string literal)
                                    continue;
                                if (literal.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                                if (strings.Count >= MaxResults)
                                    continue;
                                var item = MetadataIdentity.ForMethod(method);
                                item["value"] = literal;
                                item["offset"] = $"IL_{instr.Offset:X4}";
                                strings.Add(item);
                            }
                        }
                    }
                }
                result["strings"] = ToolResponse.Array(strings);
                result["stringCount"] = strings.Count;
                total += strings.Count;
            }

            result["totalMatches"] = total;
            if (total == 0)
                return ToolResponse.Failure(ToolGrep, $"No results for '{pattern}' in scope '{scope}'.");

            return ToolResponse.Success(ToolGrep, result);
        }

        /// <summary>Upper bound on returned discovery items (clients page with a narrower pattern).</summary>
        const int MaxResults = 100;
    }
}
