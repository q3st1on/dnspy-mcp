using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using dnlib.DotNet;

namespace dnSpy.MCP.Core.Helpers;

/// <summary>
/// Parses .NET metadata tokens — the immutable address of every element this
/// server exposes.
/// </summary>
/// <remarks>
/// Accepted forms are the canonical hex string this server emits
/// (<c>"0x06000001"</c>) and its decimal equivalent (<c>"100663297"</c>).
/// Everything else — names, signatures, full names — is rejected on purpose:
/// string identifiers collide in obfuscated binaries (every obfuscator reuses
/// <c>a</c>, <c>aa</c>, <c>a::a</c>), so they can never address an element.
/// </remarks>
public static class TokenParser {
    /// <summary>Token table ids dnlib can resolve (Module 0x00 .. the assembly tables).</summary>
    const uint MaxTableId = 0x2C;

    /// <summary>Parses a token in hex (<c>0x...</c>) or decimal form. Names are rejected.</summary>
    public static bool TryParse(string? text, out uint token) {
        token = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
            var hex = s.Substring(2);
            if (hex.Length is 0 or > 8)
                return false;
            return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out token);
        }

        // Decimal. Cap the digit count so a full name like
        // "Namespace.Class::Method" can never sneak through uint.TryParse.
        if (s.Length > 10)
            return false;
        return uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out token);
    }

    /// <summary>Canonical hex form used for every token this server emits.</summary>
    public static string Format(uint token) =>
        "0x" + token.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>True when <paramref name="token"/> has a real table id and row id.</summary>
    public static bool IsMetadataToken(uint token) {
        var table = token >> 24;
        var rid = token & 0x00FFFFFF;
        return table <= MaxTableId && rid != 0;
    }
}

/// <summary>
/// Canonical JSON identity for a .NET metadata element — the unified schema every
/// tool response carries.
/// </summary>
/// <remarks>
/// <para>
/// The primary key is <c>id</c>: the element's metadata token in canonical hex
/// (<c>"0x060012AB"</c>). It is immutable — a rename never moves it — so a caller can
/// hold an <c>id</c> across arbitrarily many mutations and still address the same
/// element. Everything a human reads is secondary and explicitly mutable.
/// </para>
/// <code>
/// {
///   "id": "0x060012AB",                    // immutable address (primary key)
///   "type": "Method",                      // element class
///   "current_name": "Cursed_Method_Name",  // MUTABLE label
///   "moduleMvid": "...",                   // module scope; tokens are module-relative
///   "nameIsMutable": true, "idIsImmutable": true,
///   "token": 100667051, "tokenHex": "0x060012AB",   // numeric/legacy alias of id
///   "kind": "MethodDef", "name": "Cursed_Method_Name" // legacy alias of type/current_name
/// }
/// </code>
/// <para>
/// Both the canonical keys and the pre-existing ones are emitted, <c>name</c> byte-identical
/// to <c>current_name</c>, so a client can migrate without a breaking change while the
/// address that routing depends on stays purely numeric.
/// </para>
/// </remarks>
public static class MetadataIdentity {
    // "kind" — metadata-table-level name (legacy alias, still emitted).
    public const string TypeKind = "TypeDef";
    public const string MethodKind = "MethodDef";
    public const string FieldKind = "FieldDef";
    public const string PropertyKind = "PropertyDef";
    public const string EventKind = "EventDef";
    public const string ModuleKind = "ModuleDef";
    public const string AssemblyRefKind = "AssemblyRef";
    public const string ResourceKind = "Resource";
    public const string NamespaceKind = "Namespace";
    /// <summary>Kind of a reference row that resolves to no loaded definition.</summary>
    public const string ReferenceKind = "Reference";

    // "type" — human-facing element class (canonical).
    public const string TypeType = "Type";
    public const string MethodType = "Method";
    public const string FieldType = "Field";
    public const string PropertyType = "Property";
    public const string EventType = "Event";
    public const string ModuleType = "Module";
    public const string AssemblyRefType = "AssemblyRef";
    public const string ResourceType = "Resource";
    public const string NamespaceType = "Namespace";

    public static string Mvid(ModuleDef? module) =>
        module?.Mvid?.ToString("D") ?? "";

    public static string AssemblyName(ModuleDef? module) =>
        module?.Assembly?.Name?.String ?? module?.Name?.String ?? "";

    public static string Name(ModuleDef? module) =>
        module?.Name?.String ?? "";

    /// <summary>
    /// Maps a dnlib entity onto the canonical <c>type</c> discriminator. The fallback is
    /// the dnlib type name — a compile-time type test, never a string-routing decision —
    /// so an element from an unexpected metadata table still round-trips.
    /// </summary>
    public static string TypeOf(IMDTokenProvider? entity) => entity switch {
        TypeDef => TypeType,
        MethodDef => MethodType,
        FieldDef => FieldType,
        PropertyDef => PropertyType,
        EventDef => EventType,
        ModuleDef => ModuleType,
        AssemblyRef => AssemblyRefType,
        Resource => ResourceType,
        ManifestResource => ResourceType,
        ParamDef => "Parameter",
        GenericParam => "GenericParameter",
        null => "Unknown",
        _ => entity.GetType().Name,
    };

    /// <summary>Canonicalizes a caller-supplied element-class name onto a <c>type</c> value.</summary>
    public static string NormalizeTypeName(string? typeName) => typeName?.Trim().ToLowerInvariant() switch {
        "method" or "methoddef" => MethodType,
        "type" or "typedef" or "class" => TypeType,
        "field" or "fielddef" => FieldType,
        "property" or "propertydef" => PropertyType,
        "event" or "eventdef" => EventType,
        "module" or "moduledef" => ModuleType,
        "namespace" or "ns" => NamespaceType,
        "resource" or "resourcedef" => ResourceType,
        "assemblyref" => AssemblyRefType,
        _ => typeName?.Trim() ?? "",
    };

    /// <summary>
    /// Builds the unified identity block. <paramref name="entity"/> may be null only for
    /// entities that genuinely have no metadata token (none today — modules use the
    /// Module table token 0x00000001); a null entity emits <c>id: null</c> rather
    /// than a fake 0 so callers can tell "no token" from "token zero".
    /// </summary>
    public static JsonObject For(
        IMDTokenProvider? entity,
        string kind,
        ModuleDef? module,
        string? name,
        string? fullName = null) =>
        For(entity, kind, TypeOf(entity), module, name, fullName);

    /// <summary>Overload that states the canonical <c>type</c> discriminator explicitly.</summary>
    public static JsonObject For(
        IMDTokenProvider? entity,
        string kind,
        string type,
        ModuleDef? module,
        string? name,
        string? fullName = null) {
        var o = new JsonObject {
            // Canonical schema, in priority order: address, class, mutable label.
            ["id"] = entity is null ? null : TokenParser.Format(entity.MDToken.Raw),
            ["type"] = type,
            ["current_name"] = name ?? "",
            ["nameIsMutable"] = true,
            ["idIsImmutable"] = true,
            // Module scope: a token is unique only inside its module, so the MVID is
            // part of the identity rather than decoration.
            ["moduleMvid"] = Mvid(module),
            ["assembly"] = AssemblyName(module),
            // Numeric + legacy spellings of the same address (token/name/kind), kept so
            // clients written against the previous schema keep working unchanged.
            ["token"] = entity is null ? null : entity.MDToken.Raw,
            ["tokenHex"] = entity is null ? null : TokenParser.Format(entity.MDToken.Raw),
            ["kind"] = kind,
            ["name"] = name ?? "",
            ["tokenized"] = entity is not null,
        };
        if (fullName is not null)
            o["fullName"] = fullName;
        return o;
    }

    public static JsonObject ForModule(ModuleDef module) {
        var o = For(module, ModuleKind, ModuleType, module, Name(module), module.Assembly?.FullName ?? Name(module));
        o["path"] = module.Location ?? "";
        o["runtimeVersion"] = module.RuntimeVersion ?? "";
        return o;
    }

    public static JsonObject ForType(TypeDef type) {
        var o = For(type, TypeKind, TypeType, type.Module, type.Name?.String, type.FullName?.ToString());
        o["namespace"] = type.Namespace?.String ?? "";
        return o;
    }

    public static JsonObject ForMethod(MethodDef method) {
        var o = For(method, MethodKind, MethodType, method.Module, method.Name?.String, method.FullName);
        var declaring = method.DeclaringType;
        o["declaringType"] = declaring?.FullName?.ToString() ?? "";
        o["declaringTypeToken"] = declaring is null ? null : declaring.MDToken.Raw;
        o["declaringTypeTokenHex"] = declaring is null ? null : TokenParser.Format(declaring.MDToken.Raw);
        // "parentId" is the graph edge the supervisor needs: this method belongs to that type.
        o["parentId"] = declaring is null ? null : TokenParser.Format(declaring.MDToken.Raw);
        return o;
    }

    public static JsonObject ForField(FieldDef field) {
        var o = For(field, FieldKind, FieldType, field.Module, field.Name?.String, field.FullName);
        var declaring = field.DeclaringType;
        o["declaringType"] = declaring?.FullName?.ToString() ?? "";
        o["declaringTypeToken"] = declaring is null ? null : declaring.MDToken.Raw;
        o["declaringTypeTokenHex"] = declaring is null ? null : TokenParser.Format(declaring.MDToken.Raw);
        o["parentId"] = declaring is null ? null : TokenParser.Format(declaring.MDToken.Raw);
        return o;
    }

    public static JsonObject ForProperty(PropertyDef property) {
        var o = For(property, PropertyKind, PropertyType, property.Module, property.Name?.String, property.FullName);
        var declaring = property.DeclaringType;
        o["declaringType"] = declaring?.FullName?.ToString() ?? "";
        o["declaringTypeToken"] = declaring is null ? null : declaring.MDToken.Raw;
        o["parentId"] = declaring is null ? null : TokenParser.Format(declaring.MDToken.Raw);
        return o;
    }

    public static JsonObject ForEvent(EventDef ev) {
        var o = For(ev, EventKind, EventType, ev.Module, ev.Name?.String, ev.FullName);
        var declaring = ev.DeclaringType;
        o["declaringType"] = declaring?.FullName?.ToString() ?? "";
        o["declaringTypeToken"] = declaring is null ? null : declaring.MDToken.Raw;
        o["parentId"] = declaring is null ? null : TokenParser.Format(declaring.MDToken.Raw);
        return o;
    }

    public static JsonObject ForAssemblyRef(AssemblyRef assemblyRef, ModuleDef? module) {
        var o = For(assemblyRef, AssemblyRefKind, AssemblyRefType, module, assemblyRef.Name?.String, assemblyRef.FullName);
        o["version"] = assemblyRef.Version?.ToString() ?? "";
        return o;
    }

    public static JsonObject ForResource(Resource resource, ModuleDef? module) {
        var o = For(resource, ResourceKind, ResourceType, module, resource.Name?.String, resource.Name?.String);
        o["resourceType"] = resource.ResourceType.ToString();
        o["offset"] = resource.Offset is null ? null : resource.Offset.Value;
        if (resource is EmbeddedResource embedded)
            o["size"] = (long)embedded.Length;
        return o;
    }

    /// <summary>
    /// Identity for a namespace.
    /// </summary>
    /// <remarks>
    /// .NET namespaces are not metadata entities — they have no row, hence no token.
    /// To keep namespace addressing token-keyed anyway, a namespace is identified by
    /// its ANCHOR TYPE: the TypeDef with the lowest metadata token in that namespace.
    /// The anchor is deterministic for a given module revision, and rename operations
    /// accept the token of ANY TypeDef in the namespace (see rename_namespace), so the
    /// anchor is only the canonical form emitted for round-tripping.
    /// </remarks>
    public static JsonObject ForNamespace(ModuleDef module, string @namespace) {
        var anchor = FindAnchorType(module, @namespace);
        var o = For(anchor, NamespaceKind, NamespaceType, module, @namespace, @namespace);
        if (anchor is not null) {
            o["anchorTypeToken"] = anchor.MDToken.Raw;
            o["anchorId"] = TokenParser.Format(anchor.MDToken.Raw);
        }
        o["anchorIsIdentity"] = anchor is not null;
        return o;
    }

    /// <summary>Lowest-token TypeDef declared in <paramref name="namespace"/> (null when none).</summary>
    public static TypeDef? FindAnchorType(ModuleDef module, string @namespace) {
        TypeDef? anchor = null;
        foreach (var type in module.GetTypes()) {
            if (!string.Equals(type.Namespace?.String ?? "", @namespace, StringComparison.Ordinal))
                continue;
            if (anchor is null || type.MDToken.Raw < anchor.MDToken.Raw)
                anchor = type;
        }
        return anchor;
    }
}

/// <summary>
/// Envelope for every tool response. Token-first, always.
/// </summary>
/// <remarks>
/// <para>Shape:</para>
/// <code>
/// { "ok": true, "tool": "...", "identityModel": "metadata-token", "primaryId": "id",
///   "target": { "id": "0x060012AB", "type": "Method", "current_name": "...", ... },
///   "data":   { ...tool-specific payload... } }
/// </code>
/// <para>
/// <c>target</c> is the addressed element's identity (absent for workspace-wide tools);
/// <c>data</c> is the tool-specific payload. Failures use the same envelope with
/// <c>ok:false</c> plus an <c>error</c> string, so a client never has to distinguish
/// prose from payload. <c>result</c> is emitted as an alias of <c>data</c> for clients
/// written against the previous key.
/// </para>
/// </remarks>
public static class ToolResponse {
    public const string IdentityModel = "metadata-token";

    /// <summary>Response key carrying the addressed element's immutable id.</summary>
    public const string PrimaryId = "id";

    static readonly JsonSerializerOptions s_options = new() { WriteIndented = false };

    public static string Success(string tool, JsonObject data) =>
        Success(tool, null, data);

    public static string Success(string tool, JsonObject? target, JsonObject data) {
        var o = new JsonObject {
            ["ok"] = true,
            ["tool"] = tool,
            ["identityModel"] = IdentityModel,
            ["primaryId"] = PrimaryId,
        };
        if (target is not null)
            o["target"] = target;
        o["data"] = data;
        // Alias, not a second copy of the same object graph: DeepClone keeps later
        // mutation of `data` by a caller from rewriting the historical alias too.
        o["result"] = data.DeepClone();
        return Serialize(o);
    }

    public static string Failure(string tool, string message) {
        var o = new JsonObject {
            ["ok"] = false,
            ["tool"] = tool,
            ["identityModel"] = IdentityModel,
            ["primaryId"] = PrimaryId,
            ["error"] = message,
        };
        return Serialize(o);
    }

    public static JsonArray Array(IEnumerable<JsonObject> items) {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add(item);
        return array;
    }

    public static JsonArray EmptyArray() => new();

    public static string Serialize(JsonObject node) => node.ToJsonString(s_options);
}

/// <summary>
/// Outcome of addressing an element by metadata token. Either <see cref="Success"/>
/// is true and <see cref="Entity"/>/<see cref="Module"/> are set, or
/// <see cref="Error"/> explains exactly why the token could not be addressed.
/// </summary>
public readonly struct TokenResolution {
    public bool Success { get; init; }
    public uint Token { get; init; }
    public IMDTokenProvider? Entity { get; init; }
    public ModuleDef? Module { get; init; }
    public string? Error { get; init; }

    /// <summary>Canonical <c>id</c> of the resolved (or attempted) token.</summary>
    public string Id => TokenParser.Format(Token);
}
