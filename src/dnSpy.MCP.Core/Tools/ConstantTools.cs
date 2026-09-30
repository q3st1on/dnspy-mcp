using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    /// <summary>
    /// Constant / enum inspection. Enum types and constant fields are addressed (and
    /// reported) by metadata token.
    /// </summary>
    public sealed class ConstantTools {
        const string ToolGetEnumValues = "get_enum_values";
        const string ToolSearchConstants = "search_constants";

        private readonly McpContext _ctx;
        public ConstantTools(McpContext ctx) => _ctx = ctx;

        [Description("Get every named value of an enum, addressed by the enum type's .NET metadata token (hex or decimal). Each value is reported with the backing field's own metadata token + name plus decimal and hex forms.")]
        public string GetEnumValues(
            [Description("Metadata token of the enum TypeDef, e.g. '0x02000001' or '33554433'")] string token,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetEnumValues, "No assemblies loaded.");

            var type = _ctx.Resolver.ResolveAs<TypeDef>(token, moduleMvid, out _, out var error);
            if (type is null)
                return ToolResponse.Failure(ToolGetEnumValues, error!);

            var identity = MetadataIdentity.ForType(type);
            if (!type.IsEnum)
                return ToolResponse.Failure(ToolGetEnumValues,
                    $"Token {TokenParser.Format(type.MDToken.Raw)} ('{type.FullName}') is not an enum.");

            var values = new List<JsonObject>();
            foreach (var field in type.Fields) {
                if (!field.IsLiteral || !field.IsStatic) continue;

                var item = MetadataIdentity.ForField(field);
                var value = field.Constant?.Value;
                if (value is not null) {
                    item["value"] = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    item["valueHex"] = "0x" + Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString("X", CultureInfo.InvariantCulture);
                }
                else {
                    item["value"] = null;
                    item["valueHex"] = null;
                }
                values.Add(item);
            }

            return ToolResponse.Success(ToolGetEnumValues, identity, new JsonObject {
                ["underlyingType"] = type.GetEnumUnderlyingType().FullName ?? "int",
                ["count"] = values.Count,
                ["values"] = ToolResponse.Array(values),
            });
        }

        [Description("Find constant/literal fields (const fields and enum values) by name or value pattern. Discovery only — every hit carries its field metadata token + name and its declaring type token.")]
        public string SearchConstants(
            [Description("Search pattern matched against the field name or its value")] string pattern,
            [Description("Optional namespace substring filter")] string? namespaceFilter = null,
            [Description("Optional assembly simple name to scope the search")] string? assembly = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolSearchConstants, "No assemblies loaded.");

            if (string.IsNullOrEmpty(pattern))
                return ToolResponse.Failure(ToolSearchConstants, "pattern is required.");

            var items = new List<JsonObject>();

            foreach (var mod in _ctx.Resolver.GetModules(assembly)) {
                foreach (var type in mod.GetTypes()) {
                    if (!string.IsNullOrEmpty(namespaceFilter)
                        && !UTF8String.IsNullOrEmpty(type.Namespace)
                        && type.Namespace.String.IndexOf(namespaceFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    foreach (var field in type.Fields) {
                        if (!field.IsLiteral) continue;

                        var name = field.Name.String;
                        var value = field.Constant?.Value;
                        var nameMatch = name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                        var valueMatch = value?.ToString()?.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!nameMatch && valueMatch != true)
                            continue;

                        var item = MetadataIdentity.ForField(field);
                        item["type"] = MetadataIdentity.ForType(type);
                        item["fieldType"] = field.FieldType?.FullName ?? "";
                        item["value"] = FormatValue(value);
                        item["isEnum"] = type.IsEnum;
                        items.Add(item);
                    }
                }
            }

            if (items.Count == 0)
                return ToolResponse.Failure(ToolSearchConstants, $"No constants matching '{pattern}'.");

            return ToolResponse.Success(ToolSearchConstants, new JsonObject {
                ["pattern"] = pattern,
                ["count"] = items.Count,
                ["items"] = ToolResponse.Array(items),
            });
        }

        static string FormatValue(object? value) {
            if (value is null) return "null";
            if (value is byte[] bytes) return $"{bytes.Length} bytes";
            if (value is string s) return $"\"{s}\"";
            return value.ToString() ?? "null";
        }
    }
}
