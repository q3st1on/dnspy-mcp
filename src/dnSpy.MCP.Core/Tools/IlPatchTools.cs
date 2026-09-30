using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace dnSpy.MCP.Core.Tools {
    /// <summary>Thrown when patch compilation fails with a user-facing error.</summary>
    public sealed class PatchCompileException : Exception {
        public PatchCompileException(string message) : base(message) { }
    }

    public sealed class IlPatchTools {
        const string ToolUpdateMethodBody = "update_method_body";

        private readonly McpContext _ctx;
        public IlPatchTools(McpContext ctx) => _ctx = ctx;

        [Description("Patch a method body using C# statements. The method is located strictly by its .NET metadata token (hex or decimal) — pass moduleMvid when several loaded modules define the same token. dryRun=true (default) compiles and previews without touching IL; the response always carries the method identity (token + name) and the resulting instruction count. NOTE: the patch is compiled against the assembly image on disk, so a patch issued AFTER an in-memory rename of the declaring type may fail to compile (CS0234) — patch before renaming, or re-save/reload in between. Example methodBody: Console.WriteLine(\"patched\"); return 1;")]
        public string UpdateMethodBody(
            [Description("Metadata token of the MethodDef, e.g. '0x06000001' or '100663297'")] string token,
            [Description("C# statements for the method body only")] string methodBody,
            [Description("If true, only validates and previews without modifying IL")] bool dryRun = true,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolUpdateMethodBody, "No assemblies loaded.");

            if (string.IsNullOrWhiteSpace(methodBody))
                return ToolResponse.Failure(ToolUpdateMethodBody, "methodBody is required.");

            var method = _ctx.Resolver.ResolveAs<MethodDef>(token, moduleMvid, out _, out var resolveError);
            if (method is null)
                return ToolResponse.Failure(ToolUpdateMethodBody, resolveError!);

            if (method.Body is null)
                return ToolResponse.Failure(ToolUpdateMethodBody,
                    $"Method {TokenParser.Format(method.MDToken.Raw)} has no body.");

            var module = method.Module;
            if (module is null)
                return ToolResponse.Failure(ToolUpdateMethodBody, "Method module not available.");

            CilBody? clonedBody;
            try {
                clonedBody = BuildClonedBodyFromPatch(method, methodBody);
            }
            catch (PatchCompileException ex) {
                return ToolResponse.Failure(ToolUpdateMethodBody, ex.Message);
            }
            catch (Exception ex) {
                return ToolResponse.Failure(ToolUpdateMethodBody, $"Error building patch: {ex.GetType().Name} — {ex.Message}");
            }

            if (clonedBody is null)
                return ToolResponse.Failure(ToolUpdateMethodBody, "Compiled patch method has no body.");

            if (dryRun) {
                return ToolResponse.Success(ToolUpdateMethodBody, MetadataIdentity.ForMethod(method), new JsonObject {
                    ["dryRun"] = true,
                    ["patched"] = false,
                    ["compiledInstructionCount"] = clonedBody.Instructions.Count,
                    ["previousInstructionCount"] = method.Body.Instructions.Count,
                    ["message"] = "Patch compiled successfully; IL untouched. Re-issue with dryRun=false to apply.",
                });
            }

            method.Body = clonedBody;
            _ctx.TreeRefresh.RefreshAll();

            // Broadcast the updated metadata payload: the token is unchanged (identity is
            // immutable), the name is echoed so the caller can re-render its view.
            var updated = MetadataIdentity.ForMethod(method);
            return ToolResponse.Success(ToolUpdateMethodBody, updated, new JsonObject {
                ["dryRun"] = false,
                ["patched"] = true,
                ["instructionCount"] = clonedBody.Instructions.Count,
                ["persisted"] = false,
                ["message"] = "Changes applied in-memory. Use dnSpy's File > Save Module to persist to disk.",
            });
        }

        // ---------------------------------------------------------------------------
        // Roslyn compilation
        // ---------------------------------------------------------------------------

        private static readonly Dictionary<string, string> s_csharpTypeMap = new(StringComparer.Ordinal) {
            { "System.Int32", "int" }, { "System.Int64", "long" },
            { "System.Int16", "short" }, { "System.Byte", "byte" },
            { "System.SByte", "sbyte" }, { "System.Boolean", "bool" },
            { "System.Char", "char" }, { "System.String", "string" },
            { "System.Object", "object" }, { "System.Void", "void" },
            { "System.Single", "float" }, { "System.Double", "double" },
            { "System.Decimal", "decimal" },
            { "System.UInt32", "uint" }, { "System.UInt64", "ulong" },
            { "System.UInt16", "ushort" },
        };

        internal static string NormalizeMetadataTypeName(string name) {
            var tickIndex = name.IndexOf('`');
            if (tickIndex >= 0)
                name = name.Substring(0, tickIndex);
            return name.Replace('/', '.');
        }

        internal static string ToCSharpTypeName(TypeSig? type) {
            if (type == null)
                return "object";

            if (type.IsByRef) {
                var elementType = type.Next;
                if (elementType != null && elementType.IsPointer)
                    return ToCSharpTypeName(elementType.Next) + "*";
                return "ref " + ToCSharpTypeName(elementType);
            }

            if (type.IsPointer) {
                var element = type.Next;
                return element != null ? ToCSharpTypeName(element) + "*" : "void*";
            }

            if (type.IsSZArray)
                return ToCSharpTypeName(type.Next) + "[]";

            if (type is ArraySig arraySig) {
                var rank = Math.Max(1, (int)arraySig.Rank);
                var commas = new string(',', rank - 1);
                return ToCSharpTypeName(arraySig.Next) + $"[{commas}]";
            }

            if (type.IsGenericInstanceType) {
                var genericType = (GenericInstSig)type;
                var baseName = NormalizeMetadataTypeName(genericType.GenericType.FullName ?? "object");
                var args = string.Join(", ", genericType.GenericArguments.Select(ToCSharpTypeName));
                return $"{baseName}<{args}>";
            }

            if (type.IsGenericParameter)
                return "object";

            var fullName = type.FullName;
            if (string.IsNullOrEmpty(fullName))
                return "object";
            if (s_csharpTypeMap.TryGetValue(fullName, out var mapped))
                return mapped;
            return NormalizeMetadataTypeName(fullName);
        }

        private static string BuildPatchSource(MethodDef method, string methodBody) {
            var parameters = new List<string>();

            if (!method.IsStatic && method.DeclaringType != null) {
                var thisType = ToCSharpTypeName(method.DeclaringType.ToTypeSig());
                if (method.DeclaringType.IsValueType)
                    parameters.Add($"ref {thisType} @this");
                else
                    parameters.Add($"{thisType} @this");
            }

            foreach (var p in method.Parameters.Where(p => !p.IsHiddenThisParameter)) {
                var paramName = p.Name?.ToString() ?? $"arg{p.Index}";
                var paramType = ToCSharpTypeName(p.Type);
                parameters.Add($"{paramType} {MakeSafeIdentifier(paramName)}");
            }

            var returnType = ToCSharpTypeName(method.ReturnType);

            var signatureParams = string.Join(", ", parameters);

            return $@"using System;
	public static class __Patch {{
	    public static {returnType} PatchedMethod({signatureParams}) {{
	        {methodBody}
	    }}
	}}";
        }

        internal static string MakeSafeIdentifier(string name) {
            if (string.IsNullOrWhiteSpace(name))
                return "arg";

            var sb = new StringBuilder(name.Length + 1);
            if (!(char.IsLetter(name[0]) || name[0] == '_'))
                sb.Append('_');

            foreach (var ch in name) {
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
            }

            var normalized = sb.ToString();
            if (SyntaxFacts.GetKeywordKind(normalized) != SyntaxKind.None ||
                SyntaxFacts.GetContextualKeywordKind(normalized) != SyntaxKind.None) {
                return "@" + normalized;
            }
            return normalized;
        }

        private static readonly string[] s_allowedTpaNames = new[] {
            "System.Runtime", "netstandard", "System.Private.CoreLib",
            "System.Collections", "System.Collections.Generic",
        };

        private List<MetadataReference> BuildRoslynReferences() {
            var refs = new List<MetadataReference>();
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAdd(string? path) {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;
                if (!addedPaths.Add(path))
                    return;
                refs.Add(MetadataReference.CreateFromFile(path));
            }

            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpaList) {
                foreach (var path in tpaList.Split(Path.PathSeparator)) {
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (s_allowedTpaNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        TryAdd(path);
                }
            }

            var coreAssemblies = new[] {
                typeof(object).Assembly,
                typeof(List<>).Assembly,
            };

            foreach (var asm in coreAssemblies) {
                try { TryAdd(asm.Location); }
                catch (Exception ex) {
                    // Best-effort: if a core BCL assembly can't be added as a
                    // MetadataReference (e.g. in-memory or path issues), skip it
                    // rather than abort the whole Roslyn reference set.
                    _ctx.Log.Warn($"Could not add core assembly reference '{asm.Location}': {ex.Message}");
                }
            }

            return refs;
        }

        private MethodDef? CompilePatch(MethodDef targetMethod, string methodBody) {
            var source = BuildPatchSource(targetMethod, methodBody);

            var syntaxTree = CSharpSyntaxTree.ParseText(source);

            var references = BuildRoslynReferences();
            var targetModule = targetMethod.Module;
            // Trust boundary: the target assembly is added as a MetadataReference so the patch
            // body can call its members. This is required for a working patch, but it means a
            // patch body can invoke ANY reachable method in the target assembly. Mitigations:
            //   1. dryRun=true by default — compilation only, no IL is written until explicitly confirmed.
            //   2. Pass assemblyName (UpdateMethodBody) to scope resolution and avoid patching the wrong binary.
            //   3. Only 5 BCL assemblies are referenced (see BuildRoslynReferences) — not the full TPA.
            if (!string.IsNullOrWhiteSpace(targetModule?.Location) && File.Exists(targetModule.Location)) {
                references.Add(MetadataReference.CreateFromFile(targetModule.Location));
            }

            var compilation = CSharpCompilation.Create(
                "__PatchAsm",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var ms = new MemoryStream();
            try {
                EmitResult result;
                try {
                    result = Task.Run(() => compilation.Emit(ms)).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
                catch (TimeoutException) {
                    throw new PatchCompileException("Compilation timed out (10s). Simplify the patch body.");
                }
                if (!result.Success) {
                    var errors = string.Join("\n", result.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.ToString()));
                    throw new PatchCompileException($"Compilation errors:\n{errors}");
                }

                var patchModule = ModuleDefMD.Load(ms.ToArray());
                var patchType = patchModule.Types.FirstOrDefault(t => t.Name == "__Patch");
                if (patchType == null)
                    throw new PatchCompileException("Cannot find compiled __Patch type.");

                var patchMethod = patchType.Methods.FirstOrDefault(m => m.Name != ".ctor" && m.Name != ".cctor");
                if (patchMethod == null)
                    throw new PatchCompileException("No user method found in compiled patch module.");

                return patchMethod;
            }
            finally {
                ms.Dispose();
            }
        }

        // ---------------------------------------------------------------------------
        // CilBody construction
        // ---------------------------------------------------------------------------

        private CilBody BuildClonedBodyFromPatch(MethodDef targetMethod, string methodBody) {
            var patchMethod = CompilePatch(targetMethod, methodBody);
            if (patchMethod?.Body == null)
                throw new PatchCompileException("Compiled patch method has no body.");

            var patchBody = patchMethod.Body;
            var targetModule = targetMethod.Module;

            var importer = new Importer(targetModule, ImporterOptions.TryToUseDefs);

            var instructionMap = new Dictionary<Instruction, Instruction>();

            var newBody = new CilBody(patchBody.InitLocals, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>());

            foreach (var src in patchBody.Instructions) {
                var dst = CreateInstructionClone(src, importer);
                newBody.Instructions.Add(dst);
                instructionMap[src] = dst;
            }

            for (int i = 0; i < patchBody.Instructions.Count; i++) {
                var src = patchBody.Instructions[i];
                var dst = newBody.Instructions[i];

                if (src.Operand is Instruction srcTarget && instructionMap.TryGetValue(srcTarget, out var remappedTarget)) {
                    dst.Operand = remappedTarget;
                }
                else if (src.Operand is Instruction[] srcTargets) {
                    var remapped = new Instruction[srcTargets.Length];
                    for (int j = 0; j < srcTargets.Length; j++) {
                        remapped[j] = instructionMap.TryGetValue(srcTargets[j], out var m) ? m : srcTargets[j];
                    }
                    dst.Operand = remapped;
                }
            }

            foreach (var v in patchBody.Variables) {
                var importedType = importer.Import(v.Type);
                newBody.Variables.Add(new Local(importedType));
            }

            foreach (var eh in patchBody.ExceptionHandlers) {
                var clone = new ExceptionHandler(eh.HandlerType) {
                    CatchType = eh.CatchType != null ? importer.Import(eh.CatchType) : null,
                    TryStart     = eh.TryStart     != null && instructionMap.TryGetValue(eh.TryStart,     out var ts) ? ts : null,
                    TryEnd       = eh.TryEnd       != null && instructionMap.TryGetValue(eh.TryEnd,       out var te) ? te : null,
                    HandlerStart = eh.HandlerStart != null && instructionMap.TryGetValue(eh.HandlerStart, out var hs) ? hs : null,
                    HandlerEnd   = eh.HandlerEnd   != null && instructionMap.TryGetValue(eh.HandlerEnd,   out var he) ? he : null,
                    FilterStart  = eh.FilterStart  != null && instructionMap.TryGetValue(eh.FilterStart,  out var fs) ? fs : null,
                };
                newBody.ExceptionHandlers.Add(clone);
            }

            newBody.UpdateInstructionOffsets();

            return newBody;
        }

        private static Instruction CreateInstructionClone(Instruction src, Importer importer) {
            var operand = ImportOperand(importer, src.Operand);

            return operand switch {
                null => Instruction.Create(src.OpCode),
                Instruction target => Instruction.Create(src.OpCode, target),
                Instruction[] targets => Instruction.Create(src.OpCode, targets),
                sbyte v => Instruction.Create(src.OpCode, v),
                byte v => Instruction.Create(src.OpCode, v),
                int v => Instruction.Create(src.OpCode, v),
                long v => Instruction.Create(src.OpCode, v),
                float v => Instruction.Create(src.OpCode, v),
                double v => Instruction.Create(src.OpCode, v),
                string v => Instruction.Create(src.OpCode, v),
                UTF8String v => Instruction.Create(src.OpCode, v),
                Local v => Instruction.Create(src.OpCode, v),
                Parameter v => Instruction.Create(src.OpCode, v),
                ITypeDefOrRef v => Instruction.Create(src.OpCode, v),
                IMethod v => Instruction.Create(src.OpCode, v),
                IField v => Instruction.Create(src.OpCode, v),
                _ => throw new PatchCompileException($"Unsupported IL operand type: {operand.GetType().FullName} ({src.OpCode})"),
            };
        }

        private static object? ImportOperand(Importer importer, object? operand) {
            return operand switch {
                null                            => null,
                Instruction                     => operand,
                Instruction[]                   => operand,
                ITypeDefOrRef tdor              => importer.Import(tdor),
                IMethodDefOrRef mdor            => importer.Import(mdor),
                IField ifd                      => importer.Import(ifd),
                _                               => operand,
            };
        }
    }
}
