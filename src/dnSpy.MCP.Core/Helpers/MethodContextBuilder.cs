using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.MCP.Core.Helpers;

/// <summary>
/// Per-method operand text plus the (moduleMvid, token) pair it denotes.
/// </summary>
/// <remarks>
/// The token here is the operand's <em>definition</em> token when the operand resolves
/// inside the loaded set, otherwise the reference row's own token (mirroring
/// <c>XrefTools.ReferenceItem</c>). <see cref="Resolved"/> says which of the two it is, so
/// a caller never mistakes an unresolved MemberRef row for a loaded definition.
/// </remarks>
public readonly struct OperandRef {
    /// <summary>Classification of the operand's target: "method", "field", or "type".</summary>
    public string ReferenceKind { get; init; }
    /// <summary>Module owning <see cref="Token"/>.</summary>
    public ModuleDef? Module { get; init; }
    /// <summary>Definition token (resolved) or reference-row token (unresolved).</summary>
    public uint Token { get; init; }
    public string Name { get; init; }
    public string FullName { get; init; }
    public string DeclaringTypeFullName { get; init; }
    /// <summary>True when the operand resolved to a definition inside a loaded module.</summary>
    public bool Resolved { get; init; }
    /// <summary>Number of IL sites in the analyzed method that carry this operand.</summary>
    public int Count { get; init; }
}

/// <summary>
/// Builds the token-first context of a single method: its parent type token, its raw IL,
/// and the deduplicated set of metadata tokens it explicitly calls or references.
/// </summary>
/// <remarks>
/// <para>
/// Shared by <c>get_method_context</c> (graph ingestion) and <c>get_xrefs_to</c>
/// (reverse edges) so both sides of the graph agree on what a reference is.
/// </para>
/// <para>
/// Everything here is a read over dnlib metadata, so it is safe to run concurrently
/// against a frozen workspace. Writers (rename/patch) are serialized by the transport
/// (see <c>McpServerHost._mutationLock</c>); a reader that races a writer sees either
/// the pre- or post-mutation metadata, never a torn structure, because dnlib exposes
/// only whole-object field assignments to the collections read here.
/// </para>
/// </remarks>
public static class MethodContextBuilder {
    /// <summary>Global cap on distinct operands reported per method.</summary>
    public const int DefaultReferenceLimit = 512;
    /// <summary>Maximum length of a raw IL byte dump (reconstructed from the instruction stream).</summary>
    public const int MaxIlBytes = 64 * 1024;

    /// <summary>
    /// Enumerates every metadata reference in <paramref name="method"/>'s body: instruction
    /// operands, call/callvirt/newobj targets, field accesses, exception-handler catch types,
    /// and local variable types.
    /// </summary>
    /// <param name="method">Method whose body is sliced. A null body yields an empty set.</param>
    /// <param name="maxReferences">
    /// Stop after this many DISTINCT references. Distinctness is by (moduleMvid, token), so a
    /// call site repeated 500 times still counts once. Pass 0 for <see cref="DefaultReferenceLimit"/>.
    /// </param>
    /// <param name="truncated">True when the cap stopped enumeration early.</param>
    public static List<OperandRef> CollectReferences(MethodDef method, int maxReferences, out bool truncated) {
        // A local, not the out parameter directly: the closure below cannot touch an out param.
        var hitLimit = false;
        var results = new List<OperandRef>();
        var body = method.Body;
        if (body is null) {
            truncated = false;
            return results;
        }

        var limit = maxReferences <= 0 ? DefaultReferenceLimit : maxReferences;
        // Key includes the referenceKind because the same token can legitimately surface as
        // both a type and a field/method reference (e.g. a value type's boxed call site).
        var seen = new Dictionary<(string Kind, string Mvid, uint Token), int>();

        void Add(string referenceKind, ModuleDef? module, uint token, string name, string fullName, string declaringType, bool resolved) {
            if (token == 0)
                return;
            var mvid = MetadataIdentity.Mvid(module);
            var key = (referenceKind, mvid, token);
            if (seen.TryGetValue(key, out var count)) {
                seen[key] = count + 1;
                return;
            }
            if (results.Count >= limit) {
                hitLimit = true;
                return;
            }
            seen[key] = 1;
            results.Add(new OperandRef {
                ReferenceKind = referenceKind,
                Module = module,
                Token = token,
                Name = name,
                FullName = fullName,
                DeclaringTypeFullName = declaringType,
                Resolved = resolved,
                Count = 1,
            });
        }

        foreach (var instr in body.Instructions) {
            switch (instr.Operand) {
                case IMethod methodRef: {
                    var def = methodRef.ResolveMethodDefSafe();
                    if (def is not null)
                        Add("method", def.Module, def.MDToken.Raw, def.Name?.String ?? "",
                            def.FullName, def.DeclaringType?.FullName?.ToString() ?? "", true);
                    else
                        Add("method", (methodRef as IMemberRef).OwnerModule(), methodRef.MDToken.Raw,
                            methodRef.Name?.String ?? "", methodRef.FullName,
                            methodRef.DeclaringType?.FullName?.ToString() ?? "", false);
                    break;
                }
                case IField fieldRef: {
                    var def = fieldRef.ResolveFieldDefSafe();
                    if (def is not null)
                        Add("field", def.Module, def.MDToken.Raw, def.Name?.String ?? "",
                            def.FullName, def.DeclaringType?.FullName?.ToString() ?? "", true);
                    else
                        Add("field", (fieldRef as IMemberRef).OwnerModule(), fieldRef.MDToken.Raw,
                            fieldRef.Name?.String ?? "", fieldRef.FullName,
                            fieldRef.DeclaringType?.FullName?.ToString() ?? "", false);
                    break;
                }
                case ITypeDefOrRef typeRef: {
                    var def = typeRef.ResolveTypeDefSafe();
                    if (def is not null)
                        Add("type", def.Module, def.MDToken.Raw, def.Name?.String ?? "",
                            def.FullName?.ToString() ?? "", def.Namespace?.String ?? "", true);
                    else
                        Add("type", (typeRef as IMemberRef).OwnerModule(), typeRef.MDToken.Raw,
                            typeRef.Name?.String ?? "", typeRef.FullName?.ToString() ?? "",
                            typeRef.Namespace ?? "", false);
                    break;
                }
            }
        }

        foreach (var eh in body.ExceptionHandlers) {
            var catchType = eh.CatchType;
            if (catchType is null)
                continue;
            var def = catchType.ResolveTypeDefSafe();
            if (def is not null)
                Add("catchType", def.Module, def.MDToken.Raw, def.Name?.String ?? "",
                    def.FullName?.ToString() ?? "", def.Namespace?.String ?? "", true);
            else
                Add("catchType", (catchType as IMemberRef).OwnerModule(), catchType.MDToken.Raw,
                    catchType.Name?.String ?? "", catchType.FullName?.ToString() ?? "",
                    catchType.Namespace ?? "", false);
        }

        foreach (var local in body.Variables) {
            // A local's type is a TypeSig; only the TypeDefOrRef form (not a core-library
            // primitive TypeSig) addresses a metadata row the caller could act on.
            var localType = AsTypeDefOrRef(local.Type);
            if (localType is null)
                continue;
            var def = localType.ResolveTypeDefSafe();
            if (def is not null)
                Add("localType", def.Module, def.MDToken.Raw, def.Name?.String ?? "",
                    def.FullName?.ToString() ?? "", def.Namespace?.String ?? "", true);
            else
                Add("localType", (localType as IMemberRef).OwnerModule(), localType.MDToken.Raw,
                    localType.Name?.String ?? "", localType.FullName?.ToString() ?? "",
                    localType.Namespace ?? "", false);
        }

        // Propagate multiplicity so a caller can tell a hot call site from a cold one
        // without re-walking the IL.
        for (int i = 0; i < results.Count; i++) {
            var r = results[i];
            var key = (r.ReferenceKind, MetadataIdentity.Mvid(r.Module), r.Token);
            if (seen.TryGetValue(key, out var count) && count != r.Count)
                results[i] = new OperandRef {
                    ReferenceKind = r.ReferenceKind,
                    Module = r.Module,
                    Token = r.Token,
                    Name = r.Name,
                    FullName = r.FullName,
                    DeclaringTypeFullName = r.DeclaringTypeFullName,
                    Resolved = r.Resolved,
                    Count = count,
                };
        }

        truncated = hitLimit;
        return results;
    }

    /// <summary>
    /// Renders one collected reference as a token-keyed JSON node carrying the canonical
    /// <c>id</c>/<c>current_name</c>/<c>type</c> triple plus how it is used.
    /// </summary>
    public static JsonObject ToJson(OperandRef reference) {
        var (typeOfTarget, kindOfTarget) = TargetDescriptors(reference.ReferenceKind);
        return new JsonObject {
            ["id"] = TokenParser.Format(reference.Token),
            ["type"] = typeOfTarget,
            ["current_name"] = reference.Name,
            ["nameIsMutable"] = true,
            ["idIsImmutable"] = true,
            ["referenceType"] = reference.ReferenceKind,
            ["moduleMvid"] = MetadataIdentity.Mvid(reference.Module),
            ["assembly"] = MetadataIdentity.AssemblyName(reference.Module),
            ["token"] = reference.Token,
            ["tokenHex"] = TokenParser.Format(reference.Token),
            ["kind"] = kindOfTarget,
            ["name"] = reference.Name,
            ["fullName"] = reference.FullName,
            ["declaringType"] = reference.DeclaringTypeFullName,
            // resolved=false means the token addresses a reference ROW (AssemblyRef/MemberRef)
            // in the listed module, not a definition — the definition lives in a module that
            // is not loaded. Callers must qualify such ids by moduleMvid before writing.
            ["resolved"] = reference.Resolved,
            ["occurrences"] = reference.Count,
        };
    }

    /// <summary>Canonical <c>type</c>/<c>kind</c> pair for the entity a reference kind targets.</summary>
    public static (string Type, string Kind) TargetDescriptors(string referenceKind) => referenceKind switch {
        "method" => (MetadataIdentity.MethodType, MetadataIdentity.MethodKind),
        "field" => (MetadataIdentity.FieldType, MetadataIdentity.FieldKind),
        "type" or "catchType" or "localType" => (MetadataIdentity.TypeType, MetadataIdentity.TypeKind),
        _ => (MetadataIdentity.ReferenceKind, MetadataIdentity.ReferenceKind),
    };

    /// <summary>
    /// Reconstructs the method's IL as hex, best-effort, from the decoded instruction stream.
    /// </summary>
    /// <remarks>
    /// This re-encodes opcodes and operands rather than reading the original bytes at the
    /// method's RVA. It is faithful for the instructions that survive decoding (which is all
    /// the pipeline models by token can act on) and is capped at <see cref="MaxIlBytes"/>.
    /// </remarks>
    public static string ToIlHex(CilBody? body) {
        if (body is null)
            return "";
        var bytes = new List<byte>(Math.Min(body.Instructions.Count * 3, MaxIlBytes));
        foreach (var instr in body.Instructions) {
            var op = instr.OpCode;
            if (op.Size == 2)
                bytes.Add(0xFE);
            bytes.Add((byte)((int)op.Code & 0xFF));

            switch (instr.Operand) {
                case sbyte sb: bytes.Add(unchecked((byte)sb)); break;
                case byte b: bytes.Add(b); break;
                case int i: AppendInt(bytes, i); break;
                case long l: AppendInt(bytes, unchecked((int)l)); AppendInt(bytes, (int)(l >> 32)); break;
                case float f: AppendInt(bytes, BitConverter.SingleToInt32Bits(f)); break;
                case double d: {
                    var bits = BitConverter.DoubleToInt64Bits(d);
                    AppendInt(bytes, unchecked((int)bits));
                    AppendInt(bytes, (int)(bits >> 32));
                    break;
                }
                case string s: {
                    var utf16 = System.Text.Encoding.Unicode.GetBytes(s);
                    AppendInt(bytes, utf16.Length);
                    bytes.AddRange(utf16);
                    break;
                }
                case Instruction target: AppendInt(bytes, unchecked((int)target.Offset)); break;
                case Instruction[] targets: {
                    AppendInt(bytes, targets.Length);
                    foreach (var t in targets)
                        AppendInt(bytes, unchecked((int)t.Offset));
                    break;
                }
                case Local local: AppendInt(bytes, local.Index); break;
                case Parameter parameter: AppendInt(bytes, parameter.Index); break;
                case IMethod m: AppendInt(bytes, unchecked((int)m.MDToken.Raw)); break;
                case IField f2: AppendInt(bytes, unchecked((int)f2.MDToken.Raw)); break;
                case ITypeDefOrRef t2: AppendInt(bytes, unchecked((int)t2.MDToken.Raw)); break;
                case null: break;
                default: break; // unknown operand shape: opcode alone (still a usable anchor)
            }

            if (bytes.Count >= MaxIlBytes)
                break;
        }
        return Convert.ToHexString(bytes.ToArray());
    }

    static void AppendInt(List<byte> bytes, int value) {
        bytes.Add(unchecked((byte)value));
        bytes.Add(unchecked((byte)(value >> 8)));
        bytes.Add(unchecked((byte)(value >> 16)));
        bytes.Add(unchecked((byte)(value >> 24)));
    }

    /// <summary>
    /// Narrows a <see cref="TypeSig"/> to its <see cref="ITypeDefOrRef"/> form, and unwraps
    /// by-ref/pointer/array wrappers first so <c>ref Foo</c> still yields Foo's token.
    /// </summary>
    static ITypeDefOrRef? AsTypeDefOrRef(TypeSig? type) {
        while (type is not null) {
            if (type is TypeDefOrRefSig defOrRef)
                return defOrRef.TypeDefOrRef;
            type = type.Next;
        }
        return null;
    }
}
