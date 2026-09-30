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
    /// Custom attribute inspection. The attributed element is addressed by metadata
    /// token; the response echoes that token plus the current name, and resolves each
    /// attribute type to its own token when it lives in a loaded module.
    /// </summary>
    public sealed class AttributeTools {
        const string ToolGetAttributes = "get_attributes";
        const string ToolGetMethodAttributes = "get_method_attributes";

        private readonly McpContext _ctx;
        public AttributeTools(McpContext ctx) => _ctx = ctx;

        [Description("List custom attributes on a type, method, field, property, event, or assembly. The target is addressed by its .NET metadata token (hex or decimal); targetKind may be 'auto' (infer from the token) or one of 'type', 'method', 'field', 'property', 'event', 'assembly'. The response carries the target identity (token + name) and each attribute's constructor/named arguments.")]
        public string GetAttributes(
            [Description("Metadata token of the attributed element, e.g. '0x06000001'")] string token,
            [Description("Target kind: 'auto', 'type', 'method', 'field', 'property', 'event', or 'assembly'")] string targetKind = "auto",
            [Description("Optional filter: only show attributes whose type name contains this substring")] string? attributeFilter = null,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetAttributes, "No assemblies loaded.");

            var resolution = _ctx.Resolver.Resolve(token, moduleMvid);
            if (!resolution.Success)
                return ToolResponse.Failure(ToolGetAttributes, resolution.Error!);

            var kind = (targetKind ?? "auto").Trim().ToLowerInvariant();
            var entity = resolution.Entity!;

            IEnumerable<CustomAttribute>? attributes;
            JsonObject targetIdentity;

            switch (kind) {
                case "assembly" or "module": {
                    var module = entity as ModuleDef ?? resolution.Module;
                    if (module is null)
                        return ToolResponse.Failure(ToolGetAttributes, "Cannot determine the owning module for this token.");
                    attributes = module.Assembly?.CustomAttributes;
                    targetIdentity = MetadataIdentity.ForModule(module);
                    break;
                }
                case "type" or "auto" when entity is TypeDef type:
                    attributes = type.CustomAttributes;
                    targetIdentity = MetadataIdentity.ForType(type);
                    break;
                case "method" or "auto" when entity is MethodDef method:
                    attributes = method.CustomAttributes;
                    targetIdentity = MetadataIdentity.ForMethod(method);
                    break;
                case "field" or "auto" when entity is FieldDef field:
                    attributes = field.CustomAttributes;
                    targetIdentity = MetadataIdentity.ForField(field);
                    break;
                case "property" or "auto" when entity is PropertyDef property:
                    attributes = property.CustomAttributes;
                    targetIdentity = MetadataIdentity.ForProperty(property);
                    break;
                case "event" or "auto" when entity is EventDef ev:
                    attributes = ev.CustomAttributes;
                    targetIdentity = MetadataIdentity.ForEvent(ev);
                    break;
                default:
                    return ToolResponse.Failure(ToolGetAttributes,
                        $"Token {TokenParser.Format(resolution.Token)} resolves to {entity.GetType().Name}, which does not match "
                        + $"targetKind '{kind}'. Supported kinds: auto, type, method, field, property, event, assembly.");
            }

            var items = new List<JsonObject>();
            foreach (var ca in attributes ?? Enumerable.Empty<CustomAttribute>()) {
                var name = ca.AttributeType?.FullName?.ToString() ?? ca.AttributeType?.Name?.String ?? "?";
                if (!string.IsNullOrEmpty(attributeFilter)
                    && name.IndexOf(attributeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var item = new JsonObject {
                    ["name"] = name,
                    ["shortName"] = ca.AttributeType?.Name?.String ?? "",
                };

                var resolvedAttributeType = ca.AttributeType?.ResolveTypeDefSafe();
                item["token"] = resolvedAttributeType is null ? null : resolvedAttributeType.MDToken.Raw;
                item["tokenHex"] = resolvedAttributeType is null ? null : TokenParser.Format(resolvedAttributeType.MDToken.Raw);
                item["moduleMvid"] = resolvedAttributeType is null ? null : MetadataIdentity.Mvid(resolvedAttributeType.Module);
                item["tokenized"] = resolvedAttributeType is not null;

                var constructorArgs = ToolResponse.EmptyArray();
                foreach (var arg in ca.ConstructorArguments)
                    constructorArgs.Add(FormatArgument(arg));
                item["constructorArguments"] = constructorArgs;

                var namedArgs = ToolResponse.EmptyArray();
                foreach (var named in ca.NamedArguments) {
                    var entry = FormatArgument(named.Argument);
                    entry["name"] = named.Name?.String ?? "";
                    namedArgs.Add(entry);
                }
                item["namedArguments"] = namedArgs;
                items.Add(item);
            }

            return ToolResponse.Success(ToolGetAttributes, targetIdentity, new JsonObject {
                ["targetKind"] = kind,
                ["attributeFilter"] = attributeFilter,
                ["count"] = items.Count,
                ["items"] = ToolResponse.Array(items),
            });
        }

        [Description("List custom attributes on one method, addressed by its .NET metadata token (hex or decimal). Shortcut for get_attributes with targetKind='method'; the response carries the method identity (token + name).")]
        public string GetMethodAttributes(
            [Description("Metadata token of the MethodDef, e.g. '0x06000001' or '100663297'")] string token,
            [Description("Optional filter: only show attributes whose type name contains this substring")] string? attributeFilter = null,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetMethodAttributes, "No assemblies loaded.");

            var method = _ctx.Resolver.ResolveAs<MethodDef>(token, moduleMvid, out _, out var error);
            if (method is null)
                return ToolResponse.Failure(ToolGetMethodAttributes, error!);

            // Reuse the kind-dispatched implementation; the token is already validated,
            // so this cannot silently fall back to a name lookup.
            return GetAttributes(TokenParser.Format(method.MDToken.Raw), "method", attributeFilter, MetadataIdentity.Mvid(method.Module));
        }

        static JsonObject FormatArgument(CAArgument arg) {
            var o = new JsonObject {
                ["display"] = FormatValue(arg.Value),
            };
            if (arg.Value is TypeSig typeSig) {
                var def = typeSig.ToTypeDefOrRef()?.ResolveTypeDefSafe();
                o["kind"] = "Type";
                o["value"] = typeSig.FullName;
                o["token"] = def is null ? null : def.MDToken.Raw;
                o["tokenHex"] = def is null ? null : TokenParser.Format(def.MDToken.Raw);
                o["moduleMvid"] = def is null ? null : MetadataIdentity.Mvid(def.Module);
            }
            else if (arg.Value is UTF8String utf8) {
                o["kind"] = "String";
                o["value"] = utf8.String;
            }
            else if (arg.Value is byte[] bytes) {
                o["kind"] = "Byte[]";
                o["length"] = bytes.Length;
            }
            else if (arg.Value is int[] ints) {
                o["kind"] = "Int32[]";
                o["values"] = new JsonArray(ints.Select(i => (JsonNode)i).ToArray());
            }
            else {
                o["kind"] = arg.Value?.GetType().Name ?? "null";
                o["value"] = arg.Value?.ToString();
            }
            return o;
        }

        static string FormatValue(object? value) {
            if (value is null) return "null";
            if (value is TypeSig ts) return $"typeof({ts.FullName})";
            if (value is UTF8String s) return $"\"{s.String}\"";
            if (value is byte[] bytes) return $"{bytes.Length} bytes";
            if (value is int[] ints) return string.Join(", ", ints);
            return value.ToString() ?? "null";
        }
    }
}
