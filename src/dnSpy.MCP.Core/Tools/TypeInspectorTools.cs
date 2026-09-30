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
    /// Type introspection. The owning type is addressed by metadata token; every
    /// member reported carries its own token next to its (mutable) name.
    /// </summary>
    public sealed class TypeInspectorTools {
        const string ToolGetTypeMembers = "get_type_members";
        const string ToolGetFields = "get_fields";
        const string ToolGetProperties = "get_properties";

        private readonly McpContext _ctx;
        public TypeInspectorTools(McpContext ctx) => _ctx = ctx;

        [Description("List every member (fields, properties, methods, events) of a type. Address the type by .NET metadata token (hex or decimal). Each member is returned with its own metadata token + name. Optional memberType filter: 'all', 'fields', 'properties', 'methods', 'events'.")]
        public string GetTypeMembers(
            [Description("Metadata token of the TypeDef, e.g. '0x02000001' or '33554433'")] string token,
            [Description("Filter: 'all', 'fields', 'properties', 'methods', or 'events'")] string memberType = "all",
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetTypeMembers, "No assemblies loaded.");

            var type = _ctx.Resolver.ResolveAs<TypeDef>(token, moduleMvid, out _, out var error);
            if (type is null)
                return ToolResponse.Failure(ToolGetTypeMembers, error!);

            var result = new JsonObject();
            var total = 0;

            if (memberType is "all" or "fields") {
                var fields = type.Fields.Select(MetadataIdentity.ForField).ToList();
                result["fields"] = ToolResponse.Array(fields);
                result["fieldCount"] = fields.Count;
                total += fields.Count;
            }

            if (memberType is "all" or "properties") {
                var properties = type.Properties.Select(MetadataIdentity.ForProperty).ToList();
                result["properties"] = ToolResponse.Array(properties);
                result["propertyCount"] = properties.Count;
                total += properties.Count;
            }

            if (memberType is "all" or "methods") {
                var methods = type.Methods.Select(m => {
                    var item = MetadataIdentity.ForMethod(m);
                    item["returnType"] = m.ReturnType?.FullName ?? "void";
                    item["isStatic"] = m.IsStatic;
                    item["parameterCount"] = m.Parameters.Count(p => !p.IsHiddenThisParameter);
                    return item;
                }).ToList();
                result["methods"] = ToolResponse.Array(methods);
                result["methodCount"] = methods.Count;
                total += methods.Count;
            }

            if (memberType is "all" or "events") {
                var events = type.Events.Select(MetadataIdentity.ForEvent).ToList();
                result["events"] = ToolResponse.Array(events);
                result["eventCount"] = events.Count;
                total += events.Count;
            }

            result["memberType"] = memberType;
            result["totalMembers"] = total;
            return ToolResponse.Success(ToolGetTypeMembers, MetadataIdentity.ForType(type), result);
        }

        [Description("Detailed field info (type, accessibility, static/const, literal value) for a type addressed by .NET metadata token. nameFilter is a display filter over the returned set, never an address.")]
        public string GetFields(
            [Description("Metadata token of the TypeDef, e.g. '0x02000001' or '33554433'")] string token,
            [Description("Optional substring filter applied to the field names in the result set")] string? nameFilter = null,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetFields, "No assemblies loaded.");

            var type = _ctx.Resolver.ResolveAs<TypeDef>(token, moduleMvid, out _, out var error);
            if (type is null)
                return ToolResponse.Failure(ToolGetFields, error!);

            var fields = type.Fields.AsEnumerable();
            if (!string.IsNullOrEmpty(nameFilter))
                fields = fields.Where(f => f.Name.String.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);

            var items = new List<JsonObject>();
            foreach (var field in fields) {
                var item = MetadataIdentity.ForField(field);
                item["fieldType"] = field.FieldType?.FullName ?? "";
                item["access"] = FieldAccessStr(field);
                item["isStatic"] = field.IsStatic;
                item["isLiteral"] = field.IsLiteral;
                if (field.IsLiteral && field.Constant is not null)
                    item["value"] = FormatConstant(field.Constant.Value);
                items.Add(item);
            }

            return ToolResponse.Success(ToolGetFields, MetadataIdentity.ForType(type), new JsonObject {
                ["nameFilter"] = nameFilter,
                ["count"] = items.Count,
                ["items"] = ToolResponse.Array(items),
            });
        }

        [Description("Detailed property info (type, accessibility, getter/setter with their own metadata tokens) for a type addressed by .NET metadata token. nameFilter is a display filter over the returned set, never an address.")]
        public string GetProperties(
            [Description("Metadata token of the TypeDef, e.g. '0x02000001' or '33554433'")] string token,
            [Description("Optional substring filter applied to the property names in the result set")] string? nameFilter = null,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetProperties, "No assemblies loaded.");

            var type = _ctx.Resolver.ResolveAs<TypeDef>(token, moduleMvid, out _, out var error);
            if (type is null)
                return ToolResponse.Failure(ToolGetProperties, error!);

            var properties = type.Properties.AsEnumerable();
            if (!string.IsNullOrEmpty(nameFilter))
                properties = properties.Where(p => p.Name.String.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);

            var items = new List<JsonObject>();
            foreach (var property in properties) {
                var item = MetadataIdentity.ForProperty(property);
                item["propertyType"] = property.PropertySig?.RetType?.FullName ?? "";
                item["access"] = PropAccessStr(property);
                item["getter"] = property.GetMethod is null ? null : MetadataIdentity.ForMethod(property.GetMethod);
                item["setter"] = property.SetMethod is null ? null : MetadataIdentity.ForMethod(property.SetMethod);
                items.Add(item);
            }

            return ToolResponse.Success(ToolGetProperties, MetadataIdentity.ForType(type), new JsonObject {
                ["nameFilter"] = nameFilter,
                ["count"] = items.Count,
                ["items"] = ToolResponse.Array(items),
            });
        }

        // --- Helpers ---

        private static string FieldAccessStr(FieldDef f) {
            if (f.IsPublic) return "public";
            if (f.IsPrivate) return "private";
            if (f.IsFamily) return "protected";
            if (f.IsAssembly) return "internal";
            if (f.IsFamilyOrAssembly) return "protected internal";
            if (f.IsFamilyAndAssembly) return "private protected";
            return f.Access.ToString();
        }

        private static string MethodAccessStr(MethodDef m) {
            if (m.IsPublic) return "public";
            if (m.IsPrivate) return "private";
            if (m.IsFamily) return "protected";
            if (m.IsAssembly) return "internal";
            if (m.IsFamilyOrAssembly) return "protected internal";
            if (m.IsFamilyAndAssembly) return "private protected";
            return m.Access.ToString();
        }

        private static string PropAccessStr(PropertyDef p) {
            if (p.GetMethod is not null) return MethodAccessStr(p.GetMethod);
            if (p.SetMethod is not null) return MethodAccessStr(p.SetMethod);
            return "unknown";
        }

        private static string FormatConstant(object? value) {
            if (value is null) return "null";
            if (value is byte[] bytes)
                return $"{bytes.Length} bytes: {BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 32))}{(bytes.Length > 32 ? "..." : "")}";
            return value.ToString() ?? "null";
        }
    }
}
