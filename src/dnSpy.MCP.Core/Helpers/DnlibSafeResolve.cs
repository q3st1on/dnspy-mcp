using dnlib.DotNet;

namespace dnSpy.MCP.Core.Helpers {
    /// <summary>
    /// Null-safe wrappers over dnlib's reference-to-definition resolution.
    /// </summary>
    /// <remarks>
    /// dnlib's <c>ResolveMethodDef()</c>/<c>ResolveFieldDef()</c>/<c>ResolveTypeDef()</c>
    /// can throw on a malformed or hostile assembly (bad resolution scope, cyclic
    /// reference). Analysis must never die because of that: an unresolvable reference
    /// is reported as "unresolved" in the payload, never as a crash.
    /// </remarks>
    internal static class DnlibSafeResolve {
        public static MethodDef? ResolveMethodDefSafe(this IMethod method) {
            try { return method.ResolveMethodDef(); }
            catch { return null; }
        }

        public static FieldDef? ResolveFieldDefSafe(this IField field) {
            try { return field.ResolveFieldDef(); }
            catch { return null; }
        }

        public static TypeDef? ResolveTypeDefSafe(this ITypeDefOrRef type) {
            try { return type.ResolveTypeDef(); }
            catch { return null; }
        }

        /// <summary>Owning module of a metadata reference (null for detached/synthetic refs).</summary>
        public static ModuleDef? OwnerModule(this IMemberRef? memberRef) {
            try { return memberRef?.Module; }
            catch { return null; }
        }
    }
}
