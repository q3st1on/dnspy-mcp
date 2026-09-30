using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Core.Helpers {
    /// <summary>
    /// Token-only element resolver. Host-agnostic port of the Extension
    /// MethodResolver: depends on <see cref="IAssemblyLoader"/> instead of
    /// dnSpy's IDsDocumentService.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Element addressing is by .NET metadata token, full stop. The former
    /// name/signature paths — <c>ResolveMethod(fullName)</c>,
    /// <c>ResolveType(fullName)</c>, <c>ResolveMethodFlexible(identifier)</c> and the
    /// short-name fallback — were REMOVED, not deprecated: in an obfuscated binary a
    /// name is not an identity (every type and method can be named <c>a</c>), so a
    /// name lookup silently returns an arbitrary element. Discovery is the job of the
    /// search_* tools; everything downstream addresses the token those tools return.
    /// </para>
    /// <para>
    /// A token is only unique within its module (every assembly has its own
    /// 0x02000001). Resolution therefore treats an unqualified token that matches
    /// definitions in more than one loaded module as an ERROR instead of silently
    /// picking the first module — that silent pick is exactly the collision the
    /// token model exists to eliminate. Every identity payload carries
    /// <c>moduleMvid</c> so callers can always qualify.
    /// </para>
    /// </remarks>
    public sealed class MethodResolver {
        private readonly IAssemblyLoader _loader;

        public MethodResolver(IAssemblyLoader loader) {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        }

        /// <summary>Gets the first module from loaded documents.</summary>
        public ModuleDef? GetCurrentModule() {
            foreach (var loaded in _loader.GetDocuments())
                return loaded.Module;
            return null;
        }

        /// <summary>Gets all loaded modules.</summary>
        public IEnumerable<ModuleDef> GetAllModules() {
            foreach (var loaded in _loader.GetDocuments())
                yield return loaded.Module;
        }

        /// <summary>
        /// Gets modules filtered by assembly name (case-insensitive), or all if null/empty.
        /// Used by DISCOVERY tools only (search/list scoping) — never to address an element.
        /// </summary>
        public IEnumerable<ModuleDef> GetModules(string? assemblyName) {
            var modules = GetAllModules();
            if (string.IsNullOrEmpty(assemblyName))
                return modules;
            return modules.Where(m => string.Equals(m.Assembly?.Name?.String, assemblyName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Name?.String, assemblyName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Finds a loaded module by its MVID (the immutable module identity).</summary>
        public ModuleDef? FindModuleByMvid(string? moduleMvid) {
            if (string.IsNullOrWhiteSpace(moduleMvid))
                return null;
            var wanted = moduleMvid.Trim();
            foreach (var module in GetAllModules()) {
                if (string.Equals($"{module.Mvid:D}", wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals($"{module.Mvid:N}", wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals($"{module.Mvid:B}", wanted, StringComparison.OrdinalIgnoreCase))
                    return module;
            }
            return null;
        }

        /// <summary>Human-readable inventory of loaded modules, for error messages.</summary>
        public string DescribeModules() {
            var parts = new List<string>();
            foreach (var module in GetAllModules())
                parts.Add($"{MetadataIdentity.AssemblyName(module)} (moduleMvid={module.Mvid:D})");
            return parts.Count == 0 ? "(none loaded)" : string.Join(", ", parts);
        }

        /// <summary>
        /// Every loaded module that DEFINES <paramref name="raw"/>.
        /// </summary>
        /// <remarks>
        /// Used by workspace-wide propagation, where an unqualified token that exists in
        /// several modules is expected rather than ambiguous: a rename must update every
        /// module that owns a definition with this token so cross-assembly call sites stay
        /// consistent. Never used to pick "the" element for a read — that path is
        /// <see cref="Resolve(uint, string?)"/>, which rejects ambiguity.
        /// </remarks>
        public List<ModuleDef> FindDefiningModules(uint raw) {
            var hits = new List<ModuleDef>();
            foreach (var module in GetAllModules()) {
                if (TryResolveToken(module, raw) is not null)
                    hits.Add(module);
            }
            return hits;
        }

        // ---------------------------------------------------------------------
        // Token-keyed resolution — the ONLY element addressing path.
        // ---------------------------------------------------------------------

        /// <summary>Resolves a token string (hex or decimal) to its element.</summary>
        public TokenResolution Resolve(string? tokenText, string? moduleMvid) {
            if (!TokenParser.TryParse(tokenText, out var raw))
                return new TokenResolution {
                    Success = false,
                    Error = $"Invalid metadata token '{tokenText}'. Elements are addressed by metadata token only: "
                        + "pass the 'id' emitted by a discovery tool (search_types, search_methods, "
                        + "assembly_list_types, get_type_members, ...) in hex ('0x060012AB') or decimal form. "
                        + "Names and signatures are not addresses.",
                };
            return Resolve(raw, moduleMvid);
        }

        /// <summary>
        /// Resolves a raw token within an optional module scope (MVID).
        /// Unqualified tokens that exist in several loaded modules are rejected as
        /// ambiguous rather than resolved by "first match".
        /// </summary>
        public TokenResolution Resolve(uint raw, string? moduleMvid) {
            if (!TokenParser.IsMetadataToken(raw))
                return new TokenResolution {
                    Success = false,
                    Token = raw,
                    Error = $"'{TokenParser.Format(raw)}' is not a well-formed metadata token "
                        + "(expected token table 0x00-0x2C with a non-zero row id).",
                };

            if (!string.IsNullOrWhiteSpace(moduleMvid)) {
                var scoped = FindModuleByMvid(moduleMvid);
                if (scoped is null)
                    return new TokenResolution {
                        Success = false,
                        Token = raw,
                        Error = $"No loaded module has moduleMvid '{moduleMvid}'. Loaded modules: {DescribeModules()}",
                    };

                var scopedEntity = TryResolveToken(scoped, raw);
                if (scopedEntity is null)
                    return new TokenResolution {
                        Success = false,
                        Token = raw,
                        Module = scoped,
                        Error = $"Token {TokenParser.Format(raw)} is not defined in module "
                            + $"'{MetadataIdentity.AssemblyName(scoped)}' ({scoped.Mvid:D}).",
                    };

                return new TokenResolution { Success = true, Token = raw, Entity = scopedEntity, Module = scoped };
            }

            var hits = new List<(ModuleDef Module, IMDTokenProvider Entity)>();
            foreach (var module in GetAllModules()) {
                var entity = TryResolveToken(module, raw);
                if (entity is not null)
                    hits.Add((module, entity));
            }

            if (hits.Count == 0)
                return new TokenResolution {
                    Success = false,
                    Token = raw,
                    Error = $"Token {TokenParser.Format(raw)} is not defined in any loaded module. "
                        + $"Loaded modules: {DescribeModules()}",
                };

            if (hits.Count > 1)
                return new TokenResolution {
                    Success = false,
                    Token = raw,
                    Error = $"Token {TokenParser.Format(raw)} is ambiguous: it is defined in {hits.Count} loaded modules "
                        + $"({string.Join(", ", hits.Select(h => $"{MetadataIdentity.AssemblyName(h.Module)}={h.Module.Mvid:D}"))}). "
                        + "Pass moduleMvid to address exactly one element.",
                };

            return new TokenResolution {
                Success = true,
                Token = raw,
                Entity = hits[0].Entity,
                Module = hits[0].Module,
            };
        }

        /// <summary>Resolves a token and requires the element to be a <typeparamref name="T"/>.</summary>
        public T? ResolveAs<T>(string? tokenText, string? moduleMvid, out ModuleDef? module, out string? error)
            where T : class, IMDTokenProvider {
            var resolution = Resolve(tokenText, moduleMvid);
            module = resolution.Module;
            error = resolution.Error;
            if (!resolution.Success)
                return null;

            if (resolution.Entity is T typed)
                return typed;

            error = $"Token {TokenParser.Format(resolution.Token)} resolves to "
                + $"{resolution.Entity!.GetType().Name}, not {typeof(T).Name}.";
            return null;
        }

        /// <summary>Resolves a token without type narrowing (kind checks stay in the tool).</summary>
        public TokenResolution ResolveAny(string? tokenText, string? moduleMvid) =>
            Resolve(tokenText, moduleMvid);

        /// <summary>
        /// Resolves a manifest-resource token to the host-facing <see cref="Resource"/> model.
        /// </summary>
        /// <remarks>
        /// dnlib keeps TWO parallel models for the ManifestResource table (0x28):
        /// <c>ModuleDefMD.ResolveToken</c> returns a <c>ManifestResourceMD</c> (the low-level
        /// metadata row, which derives from <c>ManifestResource</c> and is NOT a
        /// <c>Resource</c>), while <c>ModuleDef.Resources</c> holds
        /// <see cref="Resource"/>/<see cref="EmbeddedResource"/> instances. They share the
        /// same token, so the token remains the address — this method just projects the MD
        /// row onto the matching <see cref="Resource"/> by row id. Without the projection a
        /// perfectly valid resource token fails a naive <c>is Resource</c> check.
        /// </remarks>
        public Resource? ResolveResource(string? tokenText, string? moduleMvid, out ModuleDef? module, out string? error) {
            module = null;
            error = null;

            var resolution = Resolve(tokenText, moduleMvid);
            module = resolution.Module;
            if (!resolution.Success) {
                error = resolution.Error;
                return null;
            }

            if (resolution.Entity is Resource direct)
                return direct;

            if (module is null) {
                error = $"Token {TokenParser.Format(resolution.Token)} has no owning module in the loaded set.";
                return null;
            }

            var raw = resolution.Token;
            foreach (var resource in module.Resources) {
                if (resource.MDToken.Raw == raw)
                    return resource;
            }

            error = $"Token {TokenParser.Format(raw)} resolves to {resolution.Entity!.GetType().Name}, "
                + $"which is not a manifest resource of module '{MetadataIdentity.AssemblyName(module)}'.";
            return null;
        }

        /// <summary>Safe <see cref="ModuleDef.ResolveToken(uint)"/> wrapper (invalid rows resolve to null).</summary>
        public static IMDTokenProvider? TryResolveToken(ModuleDef module, uint raw) {
            try {
                return module.ResolveToken(raw);
            }
            catch {
                // A malformed/absent row must read as "not defined here", never as a
                // hard failure that hides the other candidate modules.
                return null;
            }
        }

        // ---------------------------------------------------------------------
        // Discovery (pattern search). These tools EXIST to turn a human-supplied
        // pattern into tokens; they are never used to address an element.
        // ---------------------------------------------------------------------

        /// <summary>Finds types matching a pattern, across the given assembly scope.</summary>
        public IEnumerable<TypeDef> SearchTypes(string pattern, string? assemblyName = null, bool caseSensitive = false) {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var mod in GetModules(assemblyName)) {
                foreach (var type in mod.GetTypes()) {
                    if (MatchesPattern(type.FullName?.ToString(), pattern, comparison))
                        yield return type;
                }
            }
        }

        /// <summary>
        /// Finds methods matching a pattern. When <paramref name="scopeType"/> is given
        /// (resolved from a token by the caller), only that type's methods are searched.
        /// </summary>
        public IEnumerable<MethodDef> SearchMethods(string pattern, TypeDef? scopeType = null, string? assemblyName = null, bool caseSensitive = false) {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            if (scopeType is not null) {
                foreach (var method in scopeType.Methods) {
                    if (MatchesPattern(method.Name?.ToString(), pattern, comparison))
                        yield return method;
                }
                yield break;
            }

            foreach (var mod in GetModules(assemblyName)) {
                foreach (var type in mod.GetTypes()) {
                    foreach (var method in type.Methods) {
                        if (MatchesPattern(method.Name?.ToString(), pattern, comparison))
                            yield return method;
                    }
                }
            }
        }

        /// <summary>Finds fields matching a pattern (discovery).</summary>
        public IEnumerable<FieldDef> SearchFields(string pattern, string? assemblyName = null, bool caseSensitive = false) {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var mod in GetModules(assemblyName)) {
                foreach (var type in mod.GetTypes()) {
                    foreach (var field in type.Fields) {
                        if (MatchesPattern(field.Name?.ToString(), pattern, comparison))
                            yield return field;
                    }
                }
            }
        }

        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

        private static bool MatchesPattern(string? input, string pattern, StringComparison comparison) {
            if (string.IsNullOrEmpty(input)) return false;
            if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)) {
                var regex = pattern.Substring(6);
                try {
                    return System.Text.RegularExpressions.Regex.IsMatch(input, regex, System.Text.RegularExpressions.RegexOptions.None, RegexTimeout);
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException) {
                    return false;
                }
            }
            return input.Contains(pattern, comparison);
        }
    }
}
