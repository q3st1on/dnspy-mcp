using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.MCP.Core.Helpers {
    /// <summary>
    /// JSON rendering for IL instructions.
    /// </summary>
    /// <remarks>
    /// Operands that are metadata entities carry their token alongside the display
    /// text, so a client can walk IL -> element -> IL without ever re-parsing a name.
    /// An operand that resolves to a definition inside a loaded module is marked
    /// <c>operandResolved: true</c>; an external MemberRef/TypeRef is emitted with the
    /// token of the reference row in the module that owns the IL (identified by the
    /// enclosing method's <c>moduleMvid</c>).
    /// </remarks>
    public static class IlJson {
        public static JsonObject Instruction(Instruction instr) {
            var o = new JsonObject {
                ["offset"] = $"IL_{instr.Offset:X4}",
                ["offsetValue"] = instr.Offset,
                ["opCode"] = instr.OpCode.Name,
                ["operand"] = OperandText(instr.Operand),
            };
            AppendOperandIdentity(o, instr.Operand);
            return o;
        }

        public static JsonArray Instructions(IEnumerable<Instruction> instructions) {
            var array = new JsonArray();
            foreach (var instr in instructions)
                array.Add(Instruction(instr));
            return array;
        }

        public static string OperandText(object? operand) {
            return operand switch {
                null => "",
                IMethod mr => $"{mr.DeclaringType?.FullName}::{mr.Name}",
                IField fr => $"{fr.DeclaringType?.FullName}::{fr.Name}",
                ITypeDefOrRef tdr => tdr.FullName,
                string s => $"\"{s}\"",
                Parameter p => p.Name ?? $"param{p.Index}",
                Local l => l.Type?.FullName ?? $"local{l.Index}",
                Instruction[] targets => string.Join(", ", targets.Select(t => $"IL_{t.Offset:X4}")),
                Instruction target => $"IL_{target.Offset:X4}",
                _ => operand.ToString() ?? "",
            };
        }

        /// <summary>Adds operand token metadata when the operand is a metadata entity.</summary>
        static void AppendOperandIdentity(JsonObject o, object? operand) {
            switch (operand) {
                case IMethod method: {
                    o["operandKind"] = MetadataIdentity.MethodKind;
                    SetToken(o, "operandToken", method.MDToken.Raw);
                    var def = method.ResolveMethodDefSafe();
                    o["operandResolved"] = def is not null;
                    o["operandDeclaringType"] = method.DeclaringType?.FullName?.ToString() ?? "";
                    o["operandName"] = method.Name?.String ?? "";
                    if (def is not null) {
                        o["operandModuleMvid"] = MetadataIdentity.Mvid(def.Module);
                        o["operandDefToken"] = def.MDToken.Raw;
                        o["operandDefTokenHex"] = TokenParser.Format(def.MDToken.Raw);
                    }
                    break;
                }
                case IField field: {
                    o["operandKind"] = MetadataIdentity.FieldKind;
                    SetToken(o, "operandToken", field.MDToken.Raw);
                    var def = field.ResolveFieldDefSafe();
                    o["operandResolved"] = def is not null;
                    o["operandDeclaringType"] = field.DeclaringType?.FullName?.ToString() ?? "";
                    o["operandName"] = field.Name?.String ?? "";
                    if (def is not null) {
                        o["operandModuleMvid"] = MetadataIdentity.Mvid(def.Module);
                        o["operandDefToken"] = def.MDToken.Raw;
                        o["operandDefTokenHex"] = TokenParser.Format(def.MDToken.Raw);
                    }
                    break;
                }
                case ITypeDefOrRef type: {
                    o["operandKind"] = MetadataIdentity.TypeKind;
                    SetToken(o, "operandToken", type.MDToken.Raw);
                    o["operandName"] = type.Name?.String ?? "";
                    o["operandDeclaringType"] = type.Namespace ?? "";
                    break;
                }
                case Parameter parameter:
                    o["operandKind"] = "Parameter";
                    o["operandIndex"] = parameter.Index;
                    break;
                case Local local:
                    o["operandKind"] = "Local";
                    o["operandIndex"] = local.Index;
                    break;
                case Instruction target:
                    o["operandKind"] = "Instruction";
                    o["operandOffset"] = $"IL_{target.Offset:X4}";
                    break;
                case Instruction[] targets:
                    o["operandKind"] = "Instruction[]";
                    o["operandOffsets"] = new JsonArray(targets
                        .Select(t => (JsonNode)$"IL_{t.Offset:X4}").ToArray());
                    break;
            }
        }

        static void SetToken(JsonObject o, string name, uint raw) {
            if (raw == 0) {
                o[name] = null;
                o[name + "Hex"] = null;
                return;
            }
            o[name] = raw;
            o[name + "Hex"] = TokenParser.Format(raw);
        }
    }
}
