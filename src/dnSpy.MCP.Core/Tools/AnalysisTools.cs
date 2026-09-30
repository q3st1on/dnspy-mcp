using System;
using System.ComponentModel;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    /// <summary>
    /// Low-level IL / signature / hierarchy analysis. Every tool addresses its
    /// target by metadata token and echoes that token (with the current name) back.
    /// </summary>
    public sealed class AnalysisTools {
        const string ToolGetMethodIl = "get_method_il";
        const string ToolGetMethodSignatures = "get_method_signatures";
        const string ToolGetTypeHierarchy = "get_type_hierarchy";
        const string ToolGetMethodBody = "get_method_body";

        private readonly McpContext _ctx;
        public AnalysisTools(McpContext ctx) => _ctx = ctx;

        [Description("Get raw IL instructions of a method body. Address the method by .NET metadata token (hex or decimal). Each instruction carries its operand's metadata token when the operand is an element.")]
        public string GetMethodIl(
            [Description("Metadata token of the MethodDef, e.g. '0x06000001' or '100663297'")] string token,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetMethodIl, "No assemblies loaded.");

            var method = _ctx.Resolver.ResolveAs<MethodDef>(token, moduleMvid, out _, out var error);
            if (method is null)
                return ToolResponse.Failure(ToolGetMethodIl, error!);

            var identity = MetadataIdentity.ForMethod(method);
            if (method.Body is null)
                return ToolResponse.Failure(ToolGetMethodIl, $"Method {TokenParser.Format(method.MDToken.Raw)} has no body.");

            var body = method.Body;
            var handlers = ToolResponse.EmptyArray();
            foreach (var eh in body.ExceptionHandlers) {
                handlers.Add(new JsonObject {
                    ["handlerType"] = eh.HandlerType.ToString(),
                    ["tryStart"] = eh.TryStart is null ? null : $"IL_{eh.TryStart.Offset:X4}",
                    ["tryEnd"] = eh.TryEnd is null ? null : $"IL_{eh.TryEnd.Offset:X4}",
                    ["handlerStart"] = eh.HandlerStart is null ? null : $"IL_{eh.HandlerStart.Offset:X4}",
                    ["handlerEnd"] = eh.HandlerEnd is null ? null : $"IL_{eh.HandlerEnd.Offset:X4}",
                    ["catchTypeToken"] = eh.CatchType is null ? null : eh.CatchType.MDToken.Raw,
                    ["catchType"] = eh.CatchType?.FullName?.ToString(),
                });
            }

            return ToolResponse.Success(ToolGetMethodIl, identity, new JsonObject {
                ["maxStack"] = body.MaxStack,
                ["initLocals"] = body.InitLocals,
                ["localCount"] = body.Variables.Count,
                ["instructionCount"] = body.Instructions.Count,
                ["instructions"] = IlJson.Instructions(body.Instructions),
                ["exceptionHandlers"] = handlers,
            });
        }

        [Description("Get a method's signature detail: parameters, return type, flags, generics, P/Invoke. Address the method by .NET metadata token.")]
        public string GetMethodSignatures(
            [Description("Metadata token of the MethodDef, e.g. '0x06000001' or '100663297'")] string token,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetMethodSignatures, "No assemblies loaded.");

            var method = _ctx.Resolver.ResolveAs<MethodDef>(token, moduleMvid, out _, out var error);
            if (method is null)
                return ToolResponse.Failure(ToolGetMethodSignatures, error!);

            var parameters = ToolResponse.EmptyArray();
            foreach (var param in method.Parameters) {
                parameters.Add(new JsonObject {
                    ["index"] = param.Index,
                    ["name"] = param.Name ?? "",
                    ["type"] = param.Type?.FullName ?? "",
                    ["isHiddenThis"] = param.IsHiddenThisParameter,
                });
            }

            var genericParameters = ToolResponse.EmptyArray();
            foreach (var gp in method.GenericParameters)
                genericParameters.Add((JsonNode)(gp.Name?.String ?? ""));

            var result = new JsonObject {
                ["returnType"] = method.ReturnType?.FullName ?? "void",
                ["isPublic"] = method.IsPublic,
                ["isStatic"] = method.IsStatic,
                ["isVirtual"] = method.IsVirtual,
                ["isAbstract"] = method.IsAbstract,
                ["parameters"] = parameters,
                ["genericParameters"] = genericParameters,
                ["hasBody"] = method.Body is not null,
            };

            if (method.ImplMap is not null)
                result["pinvoke"] = new JsonObject {
                    ["module"] = method.ImplMap.Module?.Name?.String ?? "",
                    ["entryPoint"] = method.ImplMap.Name?.String ?? "",
                };

            return ToolResponse.Success(ToolGetMethodSignatures, MetadataIdentity.ForMethod(method), result);
        }

        [Description("Get a type's hierarchy: inheritance chain, implemented interfaces, member counts. Address the type by .NET metadata token (hex or decimal). Each ancestor and interface carries its own metadata token.")]
        public string GetTypeHierarchy(
            [Description("Metadata token of the TypeDef, e.g. '0x02000001' or '33554433'")] string token,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetTypeHierarchy, "No assemblies loaded.");

            var type = _ctx.Resolver.ResolveAs<TypeDef>(token, moduleMvid, out _, out var error);
            if (type is null)
                return ToolResponse.Failure(ToolGetTypeHierarchy, error!);

            var chain = ToolResponse.EmptyArray();
            TypeDef? current = type;
            var depth = 0;
            while (current is not null && depth < MaxHierarchyDepth) {
                chain.Add(MetadataIdentity.ForType(current));
                current = current.BaseType?.ResolveTypeDef();
                depth++;
            }

            var interfaces = ToolResponse.EmptyArray();
            foreach (var iface in type.Interfaces) {
                var item = new JsonObject {
                    ["fullName"] = iface.Interface?.FullName?.ToString() ?? "",
                    ["name"] = iface.Interface?.Name?.String ?? "",
                };
                var resolved = TryResolveInterface(iface.Interface);
                item["token"] = resolved is null ? null : resolved.MDToken.Raw;
                item["tokenHex"] = resolved is null ? null : TokenParser.Format(resolved.MDToken.Raw);
                item["moduleMvid"] = resolved is null ? null : MetadataIdentity.Mvid(resolved.Module);
                item["resolved"] = resolved is not null;
                interfaces.Add(item);
            }

            return ToolResponse.Success(ToolGetTypeHierarchy, MetadataIdentity.ForType(type), new JsonObject {
                ["inheritanceChain"] = chain,
                ["interfaces"] = interfaces,
                ["fieldCount"] = type.Fields.Count,
                ["methodCount"] = type.Methods.Count,
                ["propertyCount"] = type.Properties.Count,
                ["isPublic"] = type.IsPublic,
                ["isAbstract"] = type.IsAbstract,
                ["isSealed"] = type.IsSealed,
                ["isInterface"] = type.IsInterface,
            });
        }

        [Description("Get a method body's raw IL for pattern matching (MaxStack, InitLocals, instructions). Address the method by .NET metadata token.")]
        public string GetMethodBody(
            [Description("Metadata token of the MethodDef, e.g. '0x06000001' or '100663297'")] string token,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetMethodBody, "No assemblies loaded.");

            var method = _ctx.Resolver.ResolveAs<MethodDef>(token, moduleMvid, out _, out var error);
            if (method is null)
                return ToolResponse.Failure(ToolGetMethodBody, error!);

            var identity = MetadataIdentity.ForMethod(method);
            if (method.Body is null)
                return ToolResponse.Failure(ToolGetMethodBody, $"Method {TokenParser.Format(method.MDToken.Raw)} has no body.");

            return ToolResponse.Success(ToolGetMethodBody, identity, new JsonObject {
                ["maxStack"] = method.Body.MaxStack,
                ["initLocals"] = method.Body.InitLocals,
                ["instructionCount"] = method.Body.Instructions.Count,
                ["instructions"] = IlJson.Instructions(method.Body.Instructions),
            });
        }

        /// <summary>Caps the inheritance walk so a cyclic/obfuscated hierarchy can't spin.</summary>
        const int MaxHierarchyDepth = 20;

        static TypeDef? TryResolveInterface(ITypeDefOrRef? iface) {
            try { return iface?.ResolveTypeDef(); }
            catch { return null; }
        }
    }
}
