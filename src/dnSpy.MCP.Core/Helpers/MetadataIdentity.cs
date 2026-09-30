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
/// Canonical JSON identity for a .NET metadata element.
/// </summary>
/// <remarks>
/// Every payload this server returns carries BOTH halves of the identity:
///   - <c>token</c> / <c>tokenHex</c> / <c>moduleMvid</c> — the immutable address.
///   - <c>name</c> / <c>fullName</c> / <c>namespace</c> — mutable metadata only.
/// The token is never derived from the name and is never adjusted when a rename
/// changes the name, so a client can hold a token across a rename and still
/// address the same element (that is what makes the model idempotent).
/// </remarks>
public static class MetadataIdentity {
    public const string TypeKind = "TypeDef";
    public const string MethodKind = "MethodDef";
    public const string FieldKind = "FieldDef";
    public const string PropertyKind = "PropertyDef";
    public const string EventKind = "EventDef";
    public const string ModuleKind = "ModuleDef";
    public const string AssemblyRefKind = "AssemblyRef";
    public const string ResourceKind = "Resource";
    public const string NamespaceKind = "Namespace";

    public static string Mvid(ModuleDef? module) =>
        module?.Mvid?.ToString("D") ?? "";

    public static string AssemblyName(ModuleDef? module) =>
        module?.Assembly?.Name?.String ?? module?.Name?.String ?? "";

    public static string Name(ModuleDef? module) =>
        module?.Name?.String ?? "";

    /// <summary>
    /// Builds the core identity block. <paramref name="entity"/> may be null only for
    /// entities that genuinely have no metadata token (none today — modules use the
    /// Module table token 0x00000001); a null entity emits <c>token: null</c> rather
    /// than a fake 0 so callers can tell "no token" from "token zero".
    /// </summary>
    public static JsonObject For(
        IMDTokenProvider? entity,
        string kind,
        ModuleDef? module,
        string? name,
        string? fullName = null) {
        var o = new JsonObject {
            ["kind"] = kind,
        };

        if (entity is null) {
            o["token"] = null;
            o["tokenHex"] = null;
            o["tokenized"] = false;
        }
        else {
            var raw = entity.MDToken.Raw;
            o["token"] = raw;
            o["tokenHex"] = TokenParser.Format(raw);
            o["tokenized"] = true;
        }

        o["moduleMvid"] = Mvid(module);
        o["assembly"] = AssemblyName(module);
        // Name last and explicitly flagged as mutable: the token above is the identity.
        o["name"] = name ?? "";
        o["nameIsMutable"] = true;
        if (fullName is not null)
            o["fullName"] = fullName;
        return o;
    }

    public static JsonObject ForModule(ModuleDef module) {
        var o = For(module, ModuleKind, module, Name(module), module.Assembly?.FullName ?? Name(module));
        o["path"] = module.Location ?? "";
        o["runtimeVersion"] = module.RuntimeVersion ?? "";
        return o;
    }

    public static JsonObject ForType(TypeDef type) {
        var o = For(type, TypeKind, type.Module, type.Name?.String, type.FullName?.ToString());
        o["namespace"] = type.Namespace?.String ?? "";
        return o;
    }

    public static JsonObject ForMethod(MethodDef method) {
        var o = For(method, MethodKind, method.Module, method.Name?.String, method.FullName);
        var declaring = method.DeclaringType;
        o["declaringType"] = declaring?.FullName?.ToString() ?? "";
        o["declaringTypeToken"] = declaring is null ? null : declaring.MDToken.Raw;
        o["declaringTypeTokenHex"] = declaring is null ? null : TokenParser.Format(declaring.MDToken.Raw);
        return o;
    }

    public static JsonObject ForField(FieldDef field) {
        var o = For(field, FieldKind, field.Module, field.Name?.String, field.FullName);
        var declaring = field.DeclaringType;
        o["declaringType"] = declaring?.FullName?.ToString() ?? "";
        o["declaringTypeToken"] = declaring is null ? null : declaring.MDToken.Raw;
        return o;
    }

    public static JsonObject ForProperty(PropertyDef property) {
        var o = For(property, PropertyKind, property.Module, property.Name?.String, property.FullName);
        var declaring = property.DeclaringType;
        o["declaringType"] = declaring?.FullName?.ToString() ?? "";
        o["declaringTypeToken"] = declaring is null ? null : declaring.MDToken.Raw;
        return o;
    }

    public static JsonObject ForEvent(EventDef ev) {
        var o = For(ev, EventKind, ev.Module, ev.Name?.String, ev.FullName);
        var declaring = ev.DeclaringType;
        o["declaringType"] = declaring?.FullName?.ToString() ?? "";
        o["declaringTypeToken"] = declaring is null ? null : declaring.MDToken.Raw;
        return o;
    }

    public static JsonObject ForAssemblyRef(AssemblyRef assemblyRef, ModuleDef? module) {
        var o = For(assemblyRef, AssemblyRefKind, module, assemblyRef.Name?.String, assemblyRef.FullName);
        o["version"] = assemblyRef.Version?.ToString() ?? "";
        return o;
    }

    public static JsonObject ForResource(Resource resource, ModuleDef? module) {
        var o = For(resource, ResourceKind, module, resource.Name?.String, resource.Name?.String);
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
        var o = For(anchor, NamespaceKind, module, @namespace, @namespace);
        if (anchor is not null)
            o["anchorTypeToken"] = anchor.MDToken.Raw;
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
/// Shape:
/// <code>
/// { "ok": true, "tool": "...", "identityModel": "metadata-token",
///   "primaryId": "token", "target": { ... identity ... }, "result": { ... } }
/// </code>
/// Failures use the same envelope with <c>ok:false</c> and an <c>error</c> string,
/// so a client never has to distinguish prose from payload.
/// </remarks>
public static class ToolResponse {
    public const string IdentityModel = "metadata-token";

    static readonly JsonSerializerOptions s_options = new() { WriteIndented = false };

    public static string Success(string tool, JsonObject result) =>
        Success(tool, null, result);

    public static string Success(string tool, JsonObject? target, JsonObject result) {
        var o = new JsonObject {
            ["ok"] = true,
            ["tool"] = tool,
            ["identityModel"] = IdentityModel,
            ["primaryId"] = "token",
        };
        if (target is not null)
            o["target"] = target;
        o["result"] = result;
        return Serialize(o);
    }

    public static string Failure(string tool, string message) {
        var o = new JsonObject {
            ["ok"] = false,
            ["tool"] = tool,
            ["identityModel"] = IdentityModel,
            ["primaryId"] = "token",
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
}
