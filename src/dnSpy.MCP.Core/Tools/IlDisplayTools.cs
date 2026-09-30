using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    /// <summary>
    /// Formatted IL listing. Token-addressed, token-echoing.
    /// </summary>
    public sealed class IlDisplayTools {
        const string ToolGetIlOpcodesFormatted = "get_il_opcodes_formatted";

        private readonly McpContext _ctx;
        public IlDisplayTools(McpContext ctx) => _ctx = ctx;

        [Description("Formatted IL opcode listing for a method (index, offset, opcode, operand) plus each operand's metadata token. Address the method by .NET metadata token (hex or decimal).")]
        public string GetIlOpcodesFormatted(
            [Description("Metadata token of the MethodDef, e.g. '0x06000001' or '100663297'")] string token,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetIlOpcodesFormatted, "No assemblies loaded.");

            var method = _ctx.Resolver.ResolveAs<MethodDef>(token, moduleMvid, out _, out var error);
            if (method is null)
                return ToolResponse.Failure(ToolGetIlOpcodesFormatted, error!);

            var identity = MetadataIdentity.ForMethod(method);
            if (method.Body is null)
                return ToolResponse.Failure(ToolGetIlOpcodesFormatted, $"Method {TokenParser.Format(method.MDToken.Raw)} has no body.");

            var instructions = method.Body.Instructions;
            var lines = ToolResponse.EmptyArray();
            var text = new StringBuilder();
            text.AppendLine($"// IL for {method.DeclaringType?.FullName}::{method.Name}");
            text.AppendLine($"// token {TokenParser.Format(method.MDToken.Raw)}  moduleMvid {MetadataIdentity.Mvid(method.Module)}");
            text.AppendLine("// #   Offset  OpCode            Operand");
            text.AppendLine("// --------------------------------------------------------------");

            for (int i = 0; i < instructions.Count; i++) {
                var ins = instructions[i];
                var operand = IlJson.OperandText(ins.Operand);
                var item = IlJson.Instruction(ins);
                item["index"] = i;
                lines.Add(item);
                text.AppendLine($"{i,3}  {ins.Offset:X4}    {ins.OpCode.Name,-16} {operand}");
            }

            return ToolResponse.Success(ToolGetIlOpcodesFormatted, identity, new JsonObject {
                ["instructionCount"] = instructions.Count,
                ["lines"] = lines,
                ["disassembly"] = text.ToString(),
            });
        }
    }
}
