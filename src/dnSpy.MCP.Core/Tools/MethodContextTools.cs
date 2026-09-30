using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    /// <summary>
    /// Graph-building context slicing: one method, addressed by metadata token, expanded
    /// into everything a supervisor needs to create its node and its outgoing edges —
    /// parent type token, raw IL, decompiled C#, and the deduplicated token set of every
    /// element the body calls or references.
    /// </summary>
    /// <remarks>
    /// This is the hot path of the pipeline: one call per method per worker. It is
    /// deliberately read-only, so any number of workers can slice concurrently without
    /// taking the transport's mutation lock. Decompilation is best-effort — a method that
    /// the decompiler cannot render still returns its IL and its reference tokens, because
    /// the reference set (not the C# text) is what the graph is built from.
    /// </remarks>
    public sealed class MethodContextTools {
        const string ToolGetMethodContext = "get_method_context";

        private readonly McpContext _ctx;
        public MethodContextTools(McpContext ctx) => _ctx = ctx;

        [Description(
            "Token-first context slice of ONE method: its immutable id, parent type id, raw IL, best-effort decompiled C#, and the deduplicated list of every metadata token it calls or references (methods, fields, types, catch types, local types). " +
            "This is the graph-ingestion primitive: each entry carries id/type/current_name/moduleMvid, so the caller can create nodes and edges without parsing names. " +
            "Use include* flags to trim the payload for high-volume fan-out.")]
        public string GetMethodContext(
            [Description("Metadata token ('id') of the MethodDef, e.g. '0x060012AB' or its decimal form")] string token,
            [Description("Include the raw IL instruction list (default true)")] bool includeIl = true,
            [Description("Include best-effort decompiled C# (default true). Set false for IL-only workers to cut payload size")] bool includeSource = true,
            [Description("Include the deduplicated reference-token set (default true)")] bool includeReferences = true,
            [Description("Maximum number of distinct references to report (default 512, 0 = default)")] int maxReferences = 0,
            [Description("Optional module MVID to disambiguate when several loaded modules define the same token")] string? moduleMvid = null) {

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return ToolResponse.Failure(ToolGetMethodContext, "No assemblies loaded.");

            var method = _ctx.Resolver.ResolveAs<MethodDef>(token, moduleMvid, out var module, out var error);
            if (method is null)
                return ToolResponse.Failure(ToolGetMethodContext, error!);

            var identity = MetadataIdentity.ForMethod(method);
            var declaringType = method.DeclaringType;
            var data = new JsonObject {
                // --- parent / placement: the "contains" edge of the graph ---------
                ["parentId"] = declaringType is null ? null : TokenParser.Format(declaringType.MDToken.Raw),
                ["parentType"] = declaringType is null ? null : MetadataIdentity.ForType(declaringType),
                ["moduleMvid"] = MetadataIdentity.Mvid(method.Module ?? module),
                ["signature"] = method.FullName,
                ["hasBody"] = method.Body is not null,
                ["isStatic"] = method.IsStatic,
                ["isVirtual"] = method.IsVirtual,
                ["isAbstract"] = method.IsAbstract,
                ["genericParameterCount"] = method.GenericParameters.Count,
            };

            if (method.Body is null) {
                // Abstract, extern, P/Invoke: nothing to slice, but the identity and the
                // parent edge are still valid graph input. Say so explicitly instead of
                // returning empty collections that read like "no references".
                data["sliceable"] = false;
                data["reason"] = method.IsAbstract ? "abstract method (no body)"
                    : method.ImplMap is not null ? "P/Invoke (no IL body)"
                    : "method has no IL body";
                data["references"] = ToolResponse.EmptyArray();
                data["referenceCount"] = 0;
                return ToolResponse.Success(ToolGetMethodContext, identity, data);
            }

            var body = method.Body;
            data["sliceable"] = true;
            data["maxStack"] = body.MaxStack;
            data["initLocals"] = body.InitLocals;
            data["localCount"] = body.Variables.Count;
            data["instructionCount"] = body.Instructions.Count;

            // --- raw IL -------------------------------------------------------
            data["ilHex"] = MethodContextBuilder.ToIlHex(body);
            if (includeIl)
                data["il"] = IlJson.Instructions(body.Instructions);

            // --- best-effort C# -------------------------------------------------
            if (includeSource) {
                try {
                    data["source"] = _ctx.SourceDecompiler.DecompileMethod(method);
                    data["language"] = "C#";
                    data["sourceAvailable"] = true;
                }
                catch (Exception ex) {
                    // Reference extraction below must still run: the graph is built from
                    // tokens, and an un-decompilable body (flattened/obfuscated control flow)
                    // is exactly the case this pipeline exists to handle.
                    data["source"] = null;
                    data["language"] = "C#";
                    data["sourceAvailable"] = false;
                    data["sourceError"] = $"{ex.GetType().Name}: {ex.Message}";
                    _ctx.Log.Warn($"get_method_context: decompilation failed for {TokenParser.Format(method.MDToken.Raw)}: {ex.Message}");
                }
            }

            // --- outgoing reference edges ---------------------------------------
            if (includeReferences) {
                var refs = MethodContextBuilder.CollectReferences(method, maxReferences, out var truncated);
                var items = new List<JsonObject>(refs.Count);
                foreach (var reference in refs)
                    items.Add(MethodContextBuilder.ToJson(reference));

                data["references"] = ToolResponse.Array(items);
                data["referenceCount"] = items.Count;
                data["referencesTruncated"] = truncated;
                data["referenceLimit"] = maxReferences <= 0 ? MethodContextBuilder.DefaultReferenceLimit : maxReferences;
            }

            return ToolResponse.Success(ToolGetMethodContext, identity, data);
        }
    }
}
