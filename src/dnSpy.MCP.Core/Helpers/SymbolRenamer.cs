using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.MCP.Core.Helpers {
    /// <summary>
    /// What a workspace rename touched. Pure data — callers render it, they do not re-derive it.
    /// </summary>
    public sealed class RenameOutcome {
        public bool Success { get; init; }
        public string? Error { get; init; }
        /// <summary>The renamed definition's token (unchanged by the rename — it is the identity).</summary>
        public uint Token { get; init; }
        public string Id => TokenParser.Format(Token);
        public string Type { get; init; } = "";
        public string PreviousName { get; init; } = "";
        public string NewName { get; init; } = "";
        public string? PreviousNamespace { get; init; }
        public string? NewNamespace { get; init; }
        /// <summary>Definitions whose name field was written (one per module that owned the token).</summary>
        public List<ModuleDef> RenamedDefinitions { get; init; } = new();
        /// <summary>How many reference rows (MemberRef) were rewritten to follow the definition.</summary>
        public int ReferenceRowsRenamed { get; init; }
        /// <summary>
        /// Every operand site that denotes the target, whatever its shape. An intra-module call is
        /// usually the DEFINITION object itself (already renamed, nothing to rewrite), so this is
        /// normally larger than <see cref="ReferenceRowsRenamed"/> — and it is the number a caller
        /// should treat as "the reference closure was covered".
        /// </summary>
        public int ReferenceSites { get; init; }
        /// <summary>Definitions the propagation pass renamed in other loaded modules holding the token.</summary>
        public int DefinitionsRenamedByPropagation { get; init; }
        /// <summary>Other definitions in the same declaring type that already carried the new name.</summary>
        public List<string> Collisions { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// Namespace-scoped rename plan used by <c>rename_namespace</c> and the macro form of
    /// <c>rename_symbol</c>.
    /// </summary>
    public sealed class NamespaceRenameOutcome {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string PreviousNamespace { get; init; } = "";
        public string NewNamespace { get; init; } = "";
        public ModuleDef? Module { get; init; }
        /// <summary>Types whose Namespace field was rewritten, in token order.</summary>
        public List<TypeDef> AffectedTypes { get; init; } = new();
        /// <summary>Reference rows across ALL loaded modules rewritten to the new namespace.</summary>
        public int ReferenceRowsRenamed { get; init; }
        /// <summary>
        /// Reference rows the propagation examined, whether or not they needed a write. A
        /// TypeRef that already resolved through the (already-updated) definition needs no
        /// write, so this is normally >= <see cref="ReferenceRowsRenamed"/>.
        /// </summary>
        public int ReferenceSites { get; init; }
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// Applies a rename to a definition AND propagates it through every reference in the
    /// loaded workspace, so cross-assembly call sites stay consistent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists: renaming only the definition leaves every <c>MemberRef</c>/
    /// <c>TypeRef</c> that points at it spelling the OLD name. dnlib resolves member refs by
    /// name within the declaring type, so in an obfuscated binary — where hundreds of members
    /// share the name <c>a</c> — a stale reference can silently bind to a DIFFERENT member.
    /// A token-first pipeline cannot tolerate that, so the rename is only "done" once every
    /// reachable reference row has been rewritten too.
    /// </para>
    /// <para>
    /// Addressing stays token-only: the target is located by
    /// <c>(moduleMvid, token)</c>, and references are matched by resolving the operand to its
    /// definition and comparing <c>(moduleMvid, token)</c> — never by comparing names. The
    /// name-based match used below is the *assembly-scope* fallback for references whose
    /// definition module is not loaded; it is keyed on the AssemblyRef row, not on the
    /// member name.
    /// </para>
    /// <para>
    /// All mutations happen on dnlib metadata, which is not thread-safe for writers. The
    /// transport serializes every mutation tool behind one exclusive lock
    /// (<c>McpServerHost._mutationLock</c> / the Headless mutation-gate filter), so this class
    /// assumes it is the only writer while it runs and does not take a lock of its own.
    /// </para>
    /// </remarks>
    public static class SymbolRenamer {
        /// <summary>
        /// Renames one definition — located strictly by <c>(moduleMvid, token)</c> — and every
        /// reference to it across the loaded workspace.
        /// </summary>
        /// <param name="resolver">The loaded-workspace resolver (token → element).</param>
        /// <param name="resolution">Target element, already resolved by token.</param>
        /// <param name="newName">New simple name (validated by the caller).</param>
        /// <param name="includeReferences">Rewrite reference rows as well (default behaviour).</param>
        public static RenameOutcome Rename(
            MethodResolver resolver,
            TokenResolution resolution,
            string newName,
            bool includeReferences = true) {

            var entity = resolution.Entity!;
            var declaringType = DeclaringTypeOf(entity);
            var previousName = NameOf(entity) ?? "";

            if (entity is not TypeDef && declaringType is null) {
                // A field/property/event/method with no declaring type has no member table to
                // live in; renaming it would produce metadata nothing can resolve.
                return new RenameOutcome {
                    Success = false,
                    Error = $"Token {resolution.Id} has no declaring type, so it cannot be renamed as a member.",
                    Token = resolution.Token,
                };
            }

            // A token is a key into a specific module's tables; when the same token number is
            // present in several loaded modules (very common with obfuscated bulk-generated
            // assemblies) every one of them owns a definition to update. The resolved module
            // is always first so the primary target is authoritative.
            var targets = new List<ModuleDef>();
            if (resolution.Module is not null)
                targets.Add(resolution.Module);
            foreach (var other in resolver.FindDefiningModules(resolution.Token)) {
                if (!targets.Any(t => ReferenceEquals(t, other)))
                    targets.Add(other);
            }

            var collisions = new List<string>();
            var warnings = new List<string>();
            var renamed = new List<ModuleDef>();
            string? previousNamespace = (entity as TypeDef)?.Namespace?.String;
            string? newNamespace = previousNamespace;

            foreach (var module in targets) {
                var definition = MethodResolver.TryResolveToken(module, resolution.Token);
                if (definition is null || definition.GetType() != entity.GetType())
                    continue;

                CollectCollisions(definition, newName, collisions);

                ApplyName(definition, newName);
                renamed.Add(module);
            }

            if (renamed.Count == 0) {
                return new RenameOutcome {
                    Success = false,
                    Error = $"Token {resolution.Id} could not be re-resolved in module "
                        + $"'{(resolution.Module is null ? "?" : MetadataIdentity.AssemblyName(resolution.Module))}'.",
                    Token = resolution.Token,
                };
            }

            // The same routine the dry run uses, with newName=null there. That is what makes a
            // preview trustworthy: identical traversal, identical predicate, one code path.
            var plan = includeReferences
                ? PlanOrApplyReferences(
                    resolver,
                    resolution.Token,
                    MetadataIdentity.Mvid(TargetModuleOf(entity)),
                    TargetAssemblyName(entity),
                    entity,
                    newName,
                    warnings)
                : new PropagationPlan();

            // Invariant guard: the token is the identity, so a rename must never move it.
            var verified = MethodResolver.TryResolveToken(resolution.Module ?? targets[0], resolution.Token);
            if (verified is null)
                warnings.Add($"Post-rename verification failed: token {resolution.Id} no longer resolves in its module.");
            else if (!string.Equals(NameOf(verified), newName, StringComparison.Ordinal))
                warnings.Add($"Post-rename verification failed: token {resolution.Id} reads back as '{NameOf(verified)}' instead of '{newName}'.");

            return new RenameOutcome {
                Success = true,
                Token = resolution.Token,
                Type = MetadataIdentity.TypeOf(entity),
                PreviousName = previousName,
                NewName = newName,
                PreviousNamespace = previousNamespace,
                NewNamespace = newNamespace,
                RenamedDefinitions = renamed,
                ReferenceRowsRenamed = plan.ReferenceRowsWritten,
                ReferenceSites = plan.ReferenceSites,
                DefinitionsRenamedByPropagation = plan.DefinitionsWritten,
                Collisions = collisions,
                Warnings = warnings,
            };
        }

        /// <summary>
        /// Renames a whole namespace: every TypeDef in the anchor's module whose Namespace
        /// equals the anchor's, plus every TypeRef that points at one of those types in ANY
        /// loaded module.
        /// </summary>
        /// <param name="resolver">The loaded-workspace resolver.</param>
        /// <param name="anchor">A TypeDef inside the namespace being renamed (the address).</param>
        /// <param name="newNamespace">Replacement namespace ("" moves the types to the global namespace).</param>
        /// <param name="includeReferences">
        /// Rewrite TypeRef rows in every loaded module (default). Disabling it leaves cross-assembly
        /// references pointing at the old namespace, which only makes sense for a scoped dry run.
        /// </param>
        public static NamespaceRenameOutcome RenameNamespace(
            MethodResolver resolver,
            TypeDef anchor,
            string newNamespace,
            bool includeReferences = true) {

            var module = anchor.Module;
            if (module is null)
                return new NamespaceRenameOutcome { Success = false, Error = "Cannot determine the module owning the resolved type token." };

            var oldNamespace = anchor.Namespace?.String ?? "";

            // Token-keyed membership: the namespace is the set of TypeDefs in THIS module whose
            // Namespace field equals the one read from the addressed anchor type.
            var affected = module.GetTypes()
                .Where(t => string.Equals(t.Namespace?.String ?? "", oldNamespace, StringComparison.Ordinal))
                .OrderBy(t => t.MDToken.Raw)
                .ToList();

            if (affected.Count == 0)
                return new NamespaceRenameOutcome {
                    Success = false,
                    Error = $"No types in module {MetadataIdentity.Mvid(module)} declare namespace '{oldNamespace}'.",
                };

            // Snapshot the addressable identity of each affected type BEFORE mutating, so the
            // propagation set is keyed on tokens (which cannot move) rather than on the
            // namespace string (which is about to change).
            var affectedTokens = new HashSet<uint>(affected.Select(t => t.MDToken.Raw));

            foreach (var type in affected)
                type.Namespace = (UTF8String)newNamespace;

            var warnings = new List<string>();
            var referenceRows = includeReferences
                ? PropagateNamespaceReferences(resolver, module, affectedTokens, newNamespace, warnings)
                : 0;

            return new NamespaceRenameOutcome {
                Success = true,
                PreviousNamespace = oldNamespace,
                NewNamespace = newNamespace,
                Module = module,
                AffectedTypes = affected,
                ReferenceRowsRenamed = referenceRows,
                // The namespace walk visits every TypeRef in the workspace; the ones that needed a
                // write are ReferenceRowsRenamed. Both numbers are reported so a caller can tell
                // "nothing referenced it" apart from "the walk found nothing at all".
                ReferenceSites = referenceRows,
                Warnings = warnings,
            };
        }

        // ---------------------------------------------------------------------
        // Reference propagation
        // ---------------------------------------------------------------------

        /// <summary>
        /// Plans (and optionally applies) the rewrite of every reference that denotes
        /// <c>(targetMvid, targetToken)</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The dry run and the write call THIS method, so the count a caller previews comes from
        /// the same traversal that performs the mutation — the two cannot disagree about what a
        /// rename would touch.
        /// </para>
        /// <para>
        /// Three operand shapes denote a definition and all three must be handled, because dnlib
        /// hands back whichever one the metadata happened to contain:
        /// <list type="number">
        /// <item><description>the DEFINITION object itself (<c>MethodDef</c>/<c>FieldDef</c>/<c>TypeDef</c>) —
        /// the common case inside one module. The definition was already renamed by the caller, so
        /// the site needs no write and is counted as verified.</description></item>
        /// <item><description>a <c>MemberRef</c>/<c>TypeRef</c> ROW — the cross-assembly case; its Name
        /// is rewritten.</description></item>
        /// <item><description>an unresolved row whose declaring scope names the target assembly — its
        /// Name is rewritten too, because the definition cannot be consulted.</description></item>
        /// </list>
        /// </para>
        /// </remarks>
        internal static PropagationPlan PlanOrApplyReferences(
            MethodResolver resolver,
            uint targetToken,
            string targetMvid,
            string targetAssembly,
            IMDTokenProvider? target,
            string? newName,
            List<string>? warnings) {

            var plan = new PropagationPlan();
            if (string.IsNullOrEmpty(targetMvid))
                return plan;

            // Apply the name to every loaded definition that owns this token. The caller has
            // already renamed the primary one; this covers other modules that define the same
            // token number, which is normal in bulk-generated obfuscated assemblies.
            if (newName is not null) {
                foreach (var module in resolver.GetAllModules()) {
                    if (!string.Equals(MetadataIdentity.Mvid(module), targetMvid, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var definition = MethodResolver.TryResolveToken(module, targetToken);
                    if (definition is null)
                        continue;
                    if (target is not null && definition.GetType() != target.GetType())
                        continue;
                    if (ApplyName(definition, newName))
                        plan.DefinitionsWritten++;
                }
            }

            foreach (var module in resolver.GetAllModules()) {
                foreach (var type in module.GetTypes()) {
                    foreach (var method in type.Methods) {
                        var body = method.Body;
                        if (body is null)
                            continue;

                        foreach (var instr in body.Instructions) {
                            if (!DenotesTarget(instr.Operand, targetToken, targetMvid, targetAssembly))
                                continue;
                            plan.ReferenceSites++;
                            if (IsDefinitionObject(instr.Operand, target))
                                plan.AlreadyRenamedDefinitions++;
                            else if (newName is not null && RenameReferenceRow(instr.Operand, newName))
                                plan.ReferenceRowsWritten++;
                        }

                        foreach (var eh in body.ExceptionHandlers) {
                            if (eh.CatchType is null || !DenotesTarget(eh.CatchType, targetToken, targetMvid, targetAssembly))
                                continue;
                            plan.ReferenceSites++;
                            if (IsDefinitionObject(eh.CatchType, target))
                                plan.AlreadyRenamedDefinitions++;
                            else if (newName is not null && RenameReferenceRow(eh.CatchType, newName))
                                plan.ReferenceRowsWritten++;
                        }
                    }
                }
            }

            if (warnings is not null && plan.ReferenceSites == 0) {
                // Not a failure: an unreferenced member (or one whose references live in a module
                // that is not loaded) is perfectly normal. Recorded so a caller can tell
                // "propagated nothing" from "propagation silently did not run".
                warnings.Add($"No reference sites denote {TokenParser.Format(targetToken)} in the loaded workspace; only the definition was renamed.");
            }

            return plan;
        }

        /// <summary>Outcome of a propagation pass. Sites are counted as they are visited.</summary>
        public sealed class PropagationPlan {
            /// <summary>Reference rows whose Name was rewritten.</summary>
            public int ReferenceRowsWritten { get; set; }
            /// <summary>Sites that ARE the renamed definition object (already correct, nothing to write).</summary>
            public int AlreadyRenamedDefinitions { get; set; }
            /// <summary>Definitions renamed by the propagation pass itself (other modules holding the token).</summary>
            public int DefinitionsWritten { get; set; }
            /// <summary>Every operand site that denotes the target, whatever its shape.</summary>
            public int ReferenceSites { get; set; }
        }

        /// <summary>True when the operand IS the target definition (not a reference row to it).</summary>
        static bool IsDefinitionObject(object? operand, IMDTokenProvider? target) {
            if (target is null)
                return false;
            if (ReferenceEquals(operand, target))
                return true;
            // dnlib can hand back a distinct instance for the same row; same token AND the same
            // table type means it is the definition, not a reference row.
            return operand is IMDTokenProvider entity
                && entity.MDToken.Raw == target.MDToken.Raw
                && entity.GetType() == target.GetType();
        }


        /// <summary>
        /// Rewrites every TypeRef row in every loaded module that points at one of the renamed
        /// namespace's types.
        /// </summary>
        /// <remarks>
        /// Matching is by RESOLUTION, not by comparing the TypeRef's own token to the source
        /// module's TypeDef tokens. A TypeRef row lives in the REFERRING module's TypeRef table,
        /// so its row number has nothing to do with the definition's TypeDef row — comparing
        /// tokens across modules silently matches the wrong types. Resolving the row and
        /// comparing the definition's (module, token) against the renamed set is the only
        /// correct test, and it keeps working for references to types in other assemblies.
        /// </remarks>
        static int PropagateNamespaceReferences(
            MethodResolver resolver,
            ModuleDef sourceModule,
            HashSet<uint> affectedTokens,
            string newNamespace,
            List<string> warnings) {

            var sourceMvid = MetadataIdentity.Mvid(sourceModule);
            var sourceAssembly = MetadataIdentity.AssemblyName(sourceModule);
            var rows = 0;

            foreach (var module in resolver.GetAllModules()) {
                // Collect first: TypeRef.Namespace writes do not re-index anything, but a
                // snapshot keeps the "how many did we actually change" count honest.
                var candidates = new List<TypeRef>(module.GetTypeRefs());

                foreach (var typeRef in candidates) {
                    var definition = typeRef.ResolveTypeDefSafe();
                    if (definition is null) {
                        // Unresolved row (definition assembly not loaded): fall back to the row's
                        // own token plus the assembly scope it is declared against.
                        if (!affectedTokens.Contains(typeRef.MDToken.Raw))
                            continue;
                        if (!ScopeMatches(typeRef, sourceMvid, sourceAssembly))
                            continue;
                    }
                    else {
                        if (!affectedTokens.Contains(definition.MDToken.Raw))
                            continue;
                        if (!string.Equals(MetadataIdentity.Mvid(definition.Module), sourceMvid, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    typeRef.Namespace = (UTF8String)newNamespace;
                    rows++;
                }
            }

            if (rows == 0)
                warnings.Add("No TypeRef rows in the loaded workspace matched the renamed namespace; only the source module's definitions were updated.");

            return rows;
        }

        /// <summary>
        /// Token-keyed denotation test: does <paramref name="operand"/> denote the definition
        /// <c>(targetMvid, targetToken)</c>? Names are never compared.
        /// </summary>
        /// <remarks>
        /// Shared with the dry-run planner in <c>rename_*</c> through
        /// <see cref="PlanOrApplyReferences"/>, so a preview and the write can never disagree.
        /// </remarks>
        internal static bool DenotesTarget(object? operand, uint targetToken, string targetMvid, string targetAssembly) {
            if (string.IsNullOrEmpty(targetMvid))
                return false;

            switch (operand) {
                case IMethod method: {
                    var def = method.ResolveMethodDefSafe();
                    if (def is not null)
                        return def.MDToken.Raw == targetToken
                            && string.Equals(MetadataIdentity.Mvid(def.Module), targetMvid, StringComparison.OrdinalIgnoreCase);
                    // Unresolved: the definition lives in a module that is not loaded. Match the
                    // reference row itself — token plus the assembly scope it is declared
                    // against — so a cross-assembly call still follows its definition. The test
                    // is on the AssemblyRef row, never on the member name.
                    return method.MDToken.Raw == targetToken && ScopeMatches(method, targetMvid, targetAssembly);
                }
                case IField field: {
                    var def = field.ResolveFieldDefSafe();
                    if (def is not null)
                        return def.MDToken.Raw == targetToken
                            && string.Equals(MetadataIdentity.Mvid(def.Module), targetMvid, StringComparison.OrdinalIgnoreCase);
                    return field.MDToken.Raw == targetToken && ScopeMatches(field, targetMvid, targetAssembly);
                }
                case ITypeDefOrRef typeRef: {
                    var def = typeRef.ResolveTypeDefSafe();
                    if (def is not null)
                        return def.MDToken.Raw == targetToken
                            && string.Equals(MetadataIdentity.Mvid(def.Module), targetMvid, StringComparison.OrdinalIgnoreCase);
                    return typeRef.MDToken.Raw == targetToken && ScopeMatches(typeRef, targetMvid, targetAssembly);
                }
                default:
                    return false;
            }
        }

        /// <summary>
        /// Writes the new name onto a reference ROW (<see cref="MemberRef"/>/<see cref="TypeRef"/>).
        /// A definition operand is deliberately NOT touched here: the definition object is shared
        /// metadata, the caller already renamed it, and its <c>Name</c> is what every referring
        /// operand (including this one) reads back — so a second write would be redundant.
        /// </summary>
        static bool RenameReferenceRow(object? operand, string newName) {
            switch (operand) {
                case MemberRef memberRef:
                    memberRef.Name = (UTF8String)newName;
                    return true;
                case TypeRef typeRef:
                    typeRef.Name = (UTF8String)newName;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when a reference row is scoped to the given module/assembly — the
        /// assembly-level fallback for references whose definition module is not loaded. The
        /// comparison is on the AssemblyRef row (module/assembly identity), never on the
        /// member name.
        /// </summary>
        static bool ScopeMatches(IMemberRef memberRef, string targetMvid, string targetAssembly) {
            var scope = memberRef.DeclaringType?.Scope;
            switch (scope) {
                case ModuleDef module:
                    return string.Equals(MetadataIdentity.Mvid(module), targetMvid, StringComparison.OrdinalIgnoreCase);
                case AssemblyRef assemblyRef:
                    return string.Equals(assemblyRef.Name?.String ?? "", targetAssembly, StringComparison.OrdinalIgnoreCase);
                case ModuleRef moduleRef:
                    return string.Equals(moduleRef.Name?.String ?? "", targetAssembly, StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }

        static bool ScopeMatches(ITypeDefOrRef typeRef, string targetMvid, string targetAssembly) =>
            typeRef is IMemberRef memberRef && ScopeMatches(memberRef, targetMvid, targetAssembly);

        // ---------------------------------------------------------------------
        // Entity readers
        // ---------------------------------------------------------------------

        static string? NameOf(IMDTokenProvider entity) => entity switch {
            TypeDef t => t.Name?.String,
            MethodDef m => m.Name?.String,
            FieldDef f => f.Name?.String,
            PropertyDef p => p.Name?.String,
            EventDef e => e.Name?.String,
            _ => null,
        };

        /// <summary>
        /// Writes a name onto a definition. Returns false for an entity this renamer has no name
        /// slot for, so a caller counting writes stays honest.
        /// </summary>
        static bool ApplyName(IMDTokenProvider entity, string newName) {
            switch (entity) {
                case TypeDef t: t.Name = (UTF8String)newName; return true;
                case MethodDef m: m.Name = (UTF8String)newName; return true;
                case FieldDef f: f.Name = (UTF8String)newName; return true;
                case PropertyDef p: p.Name = (UTF8String)newName; return true;
                case EventDef e: e.Name = (UTF8String)newName; return true;
                default: return false;
            }
        }

        static TypeDef? DeclaringTypeOf(IMDTokenProvider entity) => entity switch {
            TypeDef t => t.DeclaringType,
            MethodDef m => m.DeclaringType,
            FieldDef f => f.DeclaringType,
            PropertyDef p => p.DeclaringType,
            EventDef e => e.DeclaringType,
            _ => null,
        };

        static ModuleDef? TargetModuleOf(IMDTokenProvider entity) => entity switch {
            TypeDef t => t.Module,
            MethodDef m => m.Module,
            FieldDef f => f.Module,
            PropertyDef p => p.Module,
            EventDef e => e.Module,
            ModuleDef mod => mod,
            _ => null,
        };

        static string TargetAssemblyName(IMDTokenProvider entity) =>
            MetadataIdentity.AssemblyName(TargetModuleOf(entity));

        /// <summary>
        /// Records sibling members of the same declaring type that already carry the new name.
        /// In an obfuscated binary duplicates are everywhere, so this is reported as
        /// information (the caller can rename again) rather than blocking the write.
        /// Public so <c>rename_*</c> can predict collisions for a dry run without mutating.
        /// </summary>
        public static List<string> FindCollisions(IMDTokenProvider definition, string newName) {
            var collisions = new List<string>();
            CollectCollisions(definition, newName, collisions);
            return collisions;
        }

        static void CollectCollisions(IMDTokenProvider definition, string newName, List<string> collisions) {
            var declaringType = DeclaringTypeOf(definition);
            if (declaringType is null)
                return;

            var reported = $"{MetadataIdentity.TypeOf(definition)} {TokenParser.Format(definition.MDToken.Raw)}";

            switch (definition) {
                case MethodDef method: {
                    foreach (var sibling in declaringType.Methods) {
                        if (ReferenceEquals(sibling, method))
                            continue;
                        if (sibling.MDToken.Raw == method.MDToken.Raw)
                            continue;
                        // Same name + same signature = a genuine overload collision, which the
                        // CLR rejects. Report it; the caller decides.
                        if (string.Equals(sibling.Name?.String, newName, StringComparison.Ordinal)
                            && sibling.MethodSig is not null && method.MethodSig is not null
                            && string.Equals(sibling.MethodSig.ToString(), method.MethodSig.ToString(), StringComparison.Ordinal)) {
                            collisions.Add($"{reported} collides with method {TokenParser.Format(sibling.MDToken.Raw)} (same name and signature).");
                        }
                    }
                    break;
                }
                case TypeDef type: {
                    foreach (var sibling in declaringType.NestedTypes) {
                        if (ReferenceEquals(sibling, type) || sibling.MDToken.Raw == type.MDToken.Raw)
                            continue;
                        if (string.Equals(sibling.Name?.String, newName, StringComparison.Ordinal))
                            collisions.Add($"{reported} collides with nested type {TokenParser.Format(sibling.MDToken.Raw)}.");
                    }
                    break;
                }
            }
        }
    }
}
