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
    /// Cross-reference analysis. The referenced element is addressed by metadata
    /// token; matches are decided by comparing the operand's resolved definition
    /// (moduleMvid + token), never by name.
    /// </summary>
    public sealed class XrefTools {
        const string ToolGetXrefsTo = "get_xrefs_to";
        const string ToolGetCallees = "get_callees";

        private readonly McpContext _ctx;
        public XrefTools(McpContext ctx) => _ctx = ctx;

        [Description("Find every instruction that references a type, method, or field, addressed by its .NET metadata token (hex or decimal). Matches are token-based (the operand's resolved definition), so obfuscated duplicate names cannot create false hits. Each hit reports the caller's metadata token + name and the IL offset.")]
        public string GetXrefsTo(
            [Description("Metadata token of the TypeDef, MethodDef, or FieldDef to find references to")] string token,
            [Description("Optional module MVID of the referenced element; also limits the scan scope")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetXrefsTo, "No assemblies loaded.");

            var resolution = _ctx.Resolver.Resolve(token, moduleMvid);
            if (!resolution.Success)
                return ToolResponse.Failure(ToolGetXrefsTo, resolution.Error!);

            var target = resolution.Entity!;
            var targetModule = resolution.Module;
            var targetToken = target.MDToken.Raw;

            JsonObject targetIdentity;
            switch (target) {
                case MethodDef method:
                    targetIdentity = MetadataIdentity.ForMethod(method);
                    break;
                case FieldDef field:
                    targetIdentity = MetadataIdentity.ForField(field);
                    break;
                case TypeDef type:
                    targetIdentity = MetadataIdentity.ForType(type);
                    break;
                default:
                    return ToolResponse.Failure(ToolGetXrefsTo,
                        $"Token {TokenParser.Format(targetToken)} resolves to {target.GetType().Name}; "
                        + "cross-references are supported for TypeDef, MethodDef, and FieldDef tokens.");
            }

            var items = new List<JsonObject>();
            foreach (var module in _ctx.Resolver.GetAllModules()) {
                foreach (var type in module.GetTypes()) {
                    foreach (var caller in type.Methods) {
                        if (caller.Body is null) continue;
                        foreach (var instr in caller.Body.Instructions) {
                            if (!ReferencesOperand(instr.Operand, targetModule, targetToken))
                                continue;
                            var item = MetadataIdentity.ForMethod(caller);
                            item["offset"] = $"IL_{instr.Offset:X4}";
                            item["opCode"] = instr.OpCode.Name;
                            item["operand"] = IlJson.OperandText(instr.Operand);
                            items.Add(item);
                        }
                    }
                }
            }

            return ToolResponse.Success(ToolGetXrefsTo, targetIdentity, new JsonObject {
                ["count"] = items.Count,
                ["items"] = ToolResponse.Array(items),
            });
        }

        [Description("List every method and field a method calls or touches. Address the method by its .NET metadata token (hex or decimal). Each callee/field reference is reported with the metadata token it resolves to (or its reference token when the target lives outside the loaded modules).")]
        public string GetCallees(
            [Description("Metadata token of the MethodDef, e.g. '0x06000001' or '100663297'")] string token,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetCallees, "No assemblies loaded.");

            var method = _ctx.Resolver.ResolveAs<MethodDef>(token, moduleMvid, out _, out var error);
            if (method is null)
                return ToolResponse.Failure(ToolGetCallees, error!);

            var identity = MetadataIdentity.ForMethod(method);
            if (method.Body is null)
                return ToolResponse.Failure(ToolGetCallees, $"Method {TokenParser.Format(method.MDToken.Raw)} has no body.");

            var calls = new List<JsonObject>();
            var fields = new List<JsonObject>();

            foreach (var instr in method.Body.Instructions) {
                if (instr.Operand is IMethod called
                    && (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt || instr.OpCode == OpCodes.Newobj)) {
                    calls.Add(ReferenceItem(called, called.ResolveMethodDefSafe()));
                }
                else if (instr.Operand is IField fieldRef
                    && (instr.OpCode == OpCodes.Ldfld || instr.OpCode == OpCodes.Stfld
                        || instr.OpCode == OpCodes.Ldsfld || instr.OpCode == OpCodes.Stsfld)) {
                    fields.Add(ReferenceItem(fieldRef, fieldRef.ResolveFieldDefSafe()));
                }
            }

            return ToolResponse.Success(ToolGetCallees, identity, new JsonObject {
                ["callCount"] = calls.Count,
                ["fieldRefCount"] = fields.Count,
                ["calls"] = ToolResponse.Array(calls),
                ["fieldReferences"] = ToolResponse.Array(fields),
            });
        }

        /// <summary>
        /// Token-keyed reference test: does <paramref name="operand"/> denote the
        /// definition (moduleMvid, token)? Name equality is deliberately NOT used.
        /// </summary>
        static bool ReferencesOperand(object? operand, ModuleDef? targetModule, uint targetToken) {
            switch (operand) {
                case IMethod method: {
                    var def = method.ResolveMethodDefSafe();
                    return def is not null
                        ? IsSameDefinition(def.Module, def.MDToken.Raw, targetModule, targetToken)
                        : IsSameDefinition(OwnerModule(method), method.MDToken.Raw, targetModule, targetToken);
                }
                case IField field: {
                    var def = field.ResolveFieldDefSafe();
                    return def is not null
                        ? IsSameDefinition(def.Module, def.MDToken.Raw, targetModule, targetToken)
                        : IsSameDefinition(OwnerModule(field), field.MDToken.Raw, targetModule, targetToken);
                }
                case ITypeDefOrRef typeRef: {
                    var def = typeRef.ResolveTypeDefSafe();
                    return def is not null
                        ? IsSameDefinition(def.Module, def.MDToken.Raw, targetModule, targetToken)
                        : IsSameDefinition(OwnerModule(typeRef), typeRef.MDToken.Raw, targetModule, targetToken);
                }
                default:
                    return false;
            }
        }

        static bool IsSameDefinition(ModuleDef? leftModule, uint leftToken, ModuleDef? rightModule, uint rightToken) {
            if (leftToken != rightToken) return false;
            if (rightModule is null || leftModule is null) return leftModule is null && rightModule is null;
            return string.Equals(MetadataIdentity.Mvid(leftModule), MetadataIdentity.Mvid(rightModule), StringComparison.OrdinalIgnoreCase);
        }

        static ModuleDef? OwnerModule(IMethod method) => (method as IMemberRef).OwnerModule();

        static ModuleDef? OwnerModule(IField field) => (field as IMemberRef).OwnerModule();

        static ModuleDef? OwnerModule(ITypeDefOrRef type) => (type as IMemberRef).OwnerModule();

        /// <summary>
        /// Renders a call/field reference. When the reference resolves to a definition
        /// inside a loaded module, the DEFINITION's token is reported (that is the token
        /// to reuse); otherwise the reference row's own token is reported and flagged
        /// as unresolved.
        /// </summary>
        static JsonObject ReferenceItem(IMethod reference, MethodDef? definition) {
            var o = new JsonObject {
                ["kind"] = MetadataIdentity.MethodKind,
                ["name"] = reference.Name?.String ?? "",
                ["declaringType"] = reference.DeclaringType?.FullName?.ToString() ?? "",
                ["fullName"] = $"{reference.DeclaringType?.FullName}::{reference.Name}",
                ["resolved"] = definition is not null,
            };
            if (definition is not null) {
                o["token"] = definition.MDToken.Raw;
                o["tokenHex"] = TokenParser.Format(definition.MDToken.Raw);
                o["moduleMvid"] = MetadataIdentity.Mvid(definition.Module);
            }
            else {
                o["token"] = reference.MDToken.Raw == 0 ? null : reference.MDToken.Raw;
                o["tokenHex"] = reference.MDToken.Raw == 0 ? null : TokenParser.Format(reference.MDToken.Raw);
                o["moduleMvid"] = MetadataIdentity.Mvid(OwnerModule(reference));
            }
            o["nameIsMutable"] = true;
            return o;
        }

        static JsonObject ReferenceItem(IField reference, FieldDef? definition) {
            var o = new JsonObject {
                ["kind"] = MetadataIdentity.FieldKind,
                ["name"] = reference.Name?.String ?? "",
                ["declaringType"] = reference.DeclaringType?.FullName?.ToString() ?? "",
                ["fullName"] = $"{reference.DeclaringType?.FullName}::{reference.Name}",
                ["resolved"] = definition is not null,
            };
            if (definition is not null) {
                o["token"] = definition.MDToken.Raw;
                o["tokenHex"] = TokenParser.Format(definition.MDToken.Raw);
                o["moduleMvid"] = MetadataIdentity.Mvid(definition.Module);
            }
            else {
                o["token"] = reference.MDToken.Raw == 0 ? null : reference.MDToken.Raw;
                o["tokenHex"] = reference.MDToken.Raw == 0 ? null : TokenParser.Format(reference.MDToken.Raw);
                o["moduleMvid"] = MetadataIdentity.Mvid(OwnerModule(reference));
            }
            o["nameIsMutable"] = true;
            return o;
        }
    }
}
