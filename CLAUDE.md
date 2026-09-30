# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

### Setup (one-time)

```powershell
mkdir deps
# Option A (recommended): run the sync script against a local dnSpy install
pwsh scripts/sync-deps.ps1  # uses D:\ProgramFiles\StandaloneTools\RETools\dnSpy\win64\bin by default
pwsh scripts/sync-deps.ps1 -DnSpyBin "C:\path\to\dnSpy\bin"  # override path

# Option B (manual): copy these DLLs from a dnSpy installation's bin/ folder:
#   dnSpy.Contracts.DnSpy.dll
#   dnSpy.Contracts.Logic.dll
#   dnlib.dll
#   ICSharpCode.Decompiler.dll
#   # Headless-only:
#   dnSpy.Decompiler.dll
#   dnSpy.Decompiler.ILSpy.Core.dll
#   ICSharpCode.NRefactory.dll
#   ICSharpCode.NRefactory.CSharp.dll
#   ICSharpCode.NRefactory.VB.dll
```

### Local Development

```powershell
# Build entire solution (Core + Extension + Headless + Tests)
dotnet build dnspy_mcp.sln -c Release

# Deploy extension only (requires dnSpy closed)
pwsh scripts/build.ps1 -DnSpyPath "D:\tools\dnSpy" -Deploy

# Run Headless (stdio MCP server)
dotnet run --project src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj -- --load path\to\file.dll
```

Options: `-Clean`, `-Deploy`, `-DeployDir <path>`, `-Configuration <Debug|Release>`

### CI

GitHub Actions (`build.yml`) auto-downloads dnSpy deps and runs `dotnet build dnspy_mcp.sln -c Release`. No manual setup needed.

**Tool-count guard**: after adding/removing a tool, run `pwsh scripts/verify-tool-count.ps1`. It cross-checks the count discovered by reflection against the `## Available MCP Tools (NN)` header here — scans BOTH Core tools (instance methods on sealed classes with McpContext ctor) AND Extension-only tools (static methods, e.g. TreeViewTools). Fails on drift so docs and code can't silently diverge.

### Build output

- **Core lib**: `src/dnSpy.MCP.Core/bin/Release/net10.0-windows/dnSpy.MCP.Core.dll`
- **Extension DLL**: `src/dnSpy.MCP/bin/Release/net10.0-windows/dnSpy.MCP.x.dll`
- **Headless exe**: `src/dnSpy.MCP.Headless/bin/Release/net10.0-windows/dnspy-mcp-headless.dll`
- **Deploy to dnSpy**: copy `dnSpy.MCP.x.dll`, `.deps.json`, `.pdb`, AND `dnSpy.MCP.Core.dll` to `<dnSpy>/bin/Extensions/`

## Project Layout

```
dnspy_mcp/
├── dnspy_mcp.sln                # Solution referencing all 3 projects + tests
├── src/
│   ├── dnSpy.MCP.Core/          # Pure analysis library (no WPF, net10.0-windows)
│   │   ├── Abstractions/        # 5 host-agnostic interfaces
│   │   │   ├── IAssemblyLoader.cs
│   │   │   ├── ISourceDecompiler.cs
│   │   │   ├── IUIThreadScheduler.cs
│   │   │   ├── ILogSink.cs
│   │   │   └── ITreeRefreshNotifier.cs
│   │   ├── Adapters/
│   │   │   └── DnSpyDecompilerSourceProvider.cs  # Shared IDecompiler bridge
│   │   ├── Mcp/
│   │   │   ├── McpContext.cs        # Instance composition root (5 deps + Resolver)
│   │   │   ├── ToolRegistry.cs      # Hybrid instance/static reflection discovery
│   │   │   ├── McpServerHost.cs     # TcpListener + JSON-RPC 2.0 dispatch (HTTP)
│   │   │   ├── JsonRpc.cs           # JSON-RPC 2.0 protocol helpers
│   │   │   ├── BufferedLineReader.cs # HTTP/stdio line reader
│   │   │   └── McpLogger.cs         # File-only logging (Level enum)
│   │   ├── Helpers/
│   │   │   ├── MethodResolver.cs    # ctor(IAssemblyLoader) — token-only, host-agnostic
│   │   │   ├── MetadataIdentity.cs  # TokenParser + identity JSON + ToolResponse envelope
│   │   │   ├── IlJson.cs            # IL instruction JSON (operands carry tokens)
│   │   │   └── DnlibSafeResolve.cs  # null-safe reference→definition resolution
│   │   ├── Settings/
│   │   │   └── McpSettings.cs       # POCO base (ViewModelBase)
│   │   └── Tools/                   # 13 instance tool classes, 36 tools
│   │       ├── DecompilerTools.cs
│   │       ├── AssemblyTools.cs
│   │       ├── SearchTools.cs
│   │       ├── AnalysisTools.cs
│   │       ├── XrefTools.cs
│   │       ├── IlDisplayTools.cs
│   │       ├── IlPatchTools.cs      # IL patching via Roslyn compilation
│   │       ├── ResourceTools.cs
│   │       ├── TypeInspectorTools.cs
│   │       ├── AttributeTools.cs
│   │       ├── ConstantTools.cs
│   │       ├── NamespaceTools.cs
│   │       └── RenameTools.cs
│   │
│   ├── dnSpy.MCP/               # Extension (WPF, net10.0-windows) — refs Core
│   │   ├── TheExtension.cs       # MEF [ExportExtension] entry, composes McpContext
│   │   ├── IMcpExtension.cs      # Internal contract for menu commands
│   │   ├── MenuCommands.cs       # MCP Server menu items
│   │   ├── Adapters/             # dnSpy-backed adapter implementations
│   │   │   ├── DnSpyAssemblyLoader.cs       # Wraps IDsDocumentService
│   │   │   ├── WpfUIThreadScheduler.cs      # Dispatcher.Invoke
│   │   │   ├── DnSpyLogSink.cs              # File + Output Pane
│   │   │   └── DnSpyTreeRefreshNotifier.cs  # TreeView + tab refresh
│   │   ├── Settings/             # dnSpy Options integration
│   │   │   ├── McpSettingsImpl.cs       # MEF-exported subclass (load/save via ISettingsService)
│   │   │   ├── McpSettingsPage.cs       # Options dialog integration
│   │   │   ├── McpSettingsControl.xaml  # Settings UI
│   │   │   └── McpSettingsControl.xaml.cs
│   │   └── Tools/
│   │       └── TreeViewTools.cs  # Extension-only (2 tools: get_selected_node, refresh_ui)
│   │
│   ├── dnSpy.MCP.Headless/      # Standalone exe (no WPF, net10.0-windows) — refs Core + MCP SDK
│       ├── Program.cs            # Host + DI + CLI parse + stdio MCP transport
│       ├── CliOptions.cs         # --load / --config / --help args
│       └── Adapters/             # Headless-specific adapter implementations
│           ├── DnlibAssemblyLoader.cs       # ModuleDefMD.Load + registry
│           ├── DnSpyDecompilerLoader.cs     # Reflection load IDecompilerProvider
│           ├── InlineUIThreadScheduler.cs   # No-op (no UI thread)
│           ├── StderrLogSink.cs             # stderr only (MCP stdio rule)
│           ├── NoOpTreeRefreshNotifier.cs   # No-op
│           └── AutoToolRegistration.cs      # Reflection wrap Core tools to MCP SDK
│   │
│   └── dnSpy.MCP.Tests/          # Unit + headless-E2E + server-gate tests (xUnit, spawn real processes)
│
├── deps/                         # dnSpy DLL references (Contracts, Logic, dnlib, Decompilers)
└── scripts/
    ├── build.ps1                 # Build + deploy script
    └── verify-tool-count.ps1     # Tool-count guard (scans Core + Extension tools dirs)
```

## Architecture

### Three-Project Structure (B3' DI-based Hybrid)

- **`dnSpy.MCP.Core`** (lib, net10.0-windows, no WPF): pure analysis library with 5 abstraction interfaces, `McpContext` composition root, 13 instance tool classes, `ToolRegistry` reflection discovery, `McpServerHost` TCP transport.
- **`dnSpy.MCP`** (Extension, net10.0-windows, WPF): MEF entry, composes `McpContext` with dnSpy-backed adapters, references Core. Hosts the in-dnSpy MCP server.
- **`dnSpy.MCP.Headless`** (Exe, net10.0-windows, no WPF): standalone stdio MCP server for batch analysis. Uses MCP SDK + dnSpy decompiler DLLs via reflection (`IDecompilerProvider`).

### Why TcpListener Instead of MCP SDK in Extension?

The official MCP SDK (`ModelContextProtocol` 1.4.0) pulls `Microsoft.Extensions.*` 10.x which may conflict with dnSpy's transitive dependencies on .NET 10. Solution: Extension uses a minimal custom transport over `System.Net.Sockets.TcpListener` (see `McpServerHost` / `BufferedLineReader`). Headless uses MCP SDK's stdio transport (no conflict because it runs in its own process).

### Extension Lifecycle

```
dnSpy starts
  → MEF discovers dnSpy.MCP.x.dll
  → TheExtension constructor: [Import] gets services
  → OnEvent(ExtensionEvent.AppLoaded):
      - Resolve IDocumentTreeView + IDocumentTabService via IServiceLocator
      - TreeViewTools.Initialize(treeView, tabService)
      - Create Output Pane lazily
  → User clicks Start (or AutoStart=true in Settings):
      - Compose McpContext with 5 dnSpy-backed adapters (DnSpyAssemblyLoader, DnSpyDecompilerSourceProvider via DecompilerService.Decompiler, WpfUIThreadScheduler, DnSpyLogSink, DnSpyTreeRefreshNotifier)
      - Build ToolRegistry(ctx, Core assembly + Extension assembly)
      - McpServerHost(Settings, registry) → TcpListener server starts
```

Server starts on **manual click** (not at launch) so Output Pane creation runs on a fully initialized WPF UI thread.

### Tool Discovery

Tools are `public` methods on classes in namespace `dnSpy.MCP.Tools*` with `[Description("...")]` attribute. `ToolRegistry.DiscoverTools()` accepts BOTH:

- **Instance classes** with `ctor(McpContext)` — Core's 13 tool classes
- **Static classes** — Extension-only `TreeViewTools` (provides `get_selected_node`, `refresh_ui`)

Tool names auto-convert to `snake_case` via `ToolRegistry.ToSnakeCase`.

### Service Access (McpContext)

`McpContext` is an **instance class** (typed composition root) holding 5 dependencies + derived `MethodResolver`. Tools receive it via constructor injection:

```csharp
public sealed class DecompilerTools {
    private readonly McpContext _ctx;
    public DecompilerTools(McpContext ctx) => _ctx = ctx;

    public string DecompileMethod(string token, string? moduleMvid = null) {
        var method = _ctx.Resolver.ResolveAs<MethodDef>(token, moduleMvid, out _, out var error);
        if (method is null)
            return ToolResponse.Failure("decompile_method", error!);
        return ToolResponse.Success("decompile_method", MetadataIdentity.ForMethod(method), new JsonObject {
            ["source"] = _ctx.SourceDecompiler.DecompileMethod(method),
        });
    }
}
```

The 5 abstraction interfaces (`IAssemblyLoader`, `ISourceDecompiler`, `IUIThreadScheduler`, `ILogSink`, `ITreeRefreshNotifier`) seam host-specific dependencies:

- **Extension adapters** (`src/dnSpy.MCP/Adapters/`): wrap dnSpy contracts
- **Headless adapters** (`src/dnSpy.MCP.Headless/Adapters/`): use dnlib directly + dnSpy decompiler via reflection

The shared `DnSpyDecompilerSourceProvider` (in Core/Adapters) wraps dnSpy's `IDecompiler` for BOTH hosts — output is byte-identical to dnSpy.exe.

### Element Identity: token-only (no string routing)

`MethodResolver` (in `_ctx.Resolver`) is the single element-addressing path, and it is
**token-only**. It resolves `(token, moduleMvid)` → element:

```csharp
var resolution = _ctx.Resolver.Resolve(tokenText, moduleMvid);      // TokenResolution
var method = _ctx.Resolver.ResolveAs<MethodDef>(tokenText, moduleMvid, out var module, out var error);
```

Rules:

1. The former name/signature paths — `ResolveMethod(fullName)`, `ResolveType(fullName)`,
   `ResolveMethodFlexible(identifier)` and the short-name fallback — are **deleted**. Do not
   reintroduce them, and do not add a name/full-name/signature parameter to a tool.
2. Tokens are module-scoped. An unqualified token defined in more than one loaded module
   resolves to an **error asking for `moduleMvid`**, never to the first hit.
3. Discovery tools (`search_*`, `assembly_list_*`, `get_type_members`, `get_resources`, ...)
   are the only place a name pattern is legitimate — they exist to turn patterns into tokens.
4. `MetadataIdentity` (Helpers) builds the identity block: `kind`, `token`, `tokenHex`,
   `moduleMvid`, then `name`/`fullName`/`nameIsMutable`. Token = identity (never changes);
   name = mutable metadata.
5. Namespaces are not metadata rows: they are identified by an **anchor type token** (lowest
   TypeDef token in the namespace). `rename_namespace` accepts the token of any TypeDef in it.
6. Tools return `ToolResponse.Success/Failure` JSON, never bare prose — every payload must
   carry the target's token *and* name.

### Assembly Scoping

dnSpy can open multiple binaries simultaneously. To avoid ambiguous results:

- **`load_assembly`** — load a DLL/EXE into dnSpy programmatically (no manual UI step).
- **`close_assembly`** — unload an assembly by name (assemblies are file-level containers; the simple name is the handle, the module identity is its MVID).
- **`list_loaded_assemblies`** — always call first: it yields the MVIDs needed to qualify metadata tokens.
- **`assembly` parameter (discovery only)** — the search/list tools (`search_types`, `search_methods`, `search_strings`, `grep`, `search_constants`, `assembly_overview`, `assembly_list_namespaces`, `assembly_list_types`, `assembly_get_references`, `get_metadata`) accept an optional `assembly` simple name to scope which binaries are enumerated. It is never an element address.
- **Element tools** (`decompile_*`, `get_type_members`, `get_fields`, `get_properties`, `get_attributes`, `get_enum_values`, `rename_*`, `update_method_body`, `get_xrefs_to`, `get_callees`, ...) take a metadata `token` plus an optional `moduleMvid`. There is no name-based resolution: a token defined in more than one loaded module is an error until you pass `moduleMvid`.

### Batch Processing

JSON-RPC batch requests (arrays) are processed **sequentially within one connection** — requests in a batch are awaited one by one and results collected in order. Concurrency comes from **multiple simultaneous connections** (up to `MaxConcurrency`, default 4): batch pipelines should send batches over several connections for throughput. Batch example:

```
POST /  [{"method":"tools/call","params":{"name":"load_assembly","arguments":{"path":"D:\\bin\\A.dll"}},...},
         {"method":"tools/call","params":{"name":"load_assembly","arguments":{"path":"D:\\bin\\B.dll"}},...}]
```

### Server Hardening

`McpServerHost` has these protections:
- **Request body limit**: 1MB max (checked from the `Content-Length` header before the body is read)
- **Loopback Host validation**: when bound to a loopback address, any request whose `Host` header doesn't name a loopback origin (`127.0.0.1`/`localhost`/`[::1]`, with or without port) is rejected with 403 — blocks DNS rebinding (public hostname resolving to 127.0.0.1, browser reads responses same-origin). Not enforced when deliberately bound non-loopback (LAN mode). Gate config is snapshotted at `StartAsync` using the actual bound port.
- **Origin gate**: requests carrying an `Origin` header (i.e., sent from a browser) are rejected 403 unless the origin is listed in `AllowedOrigins` (comma-separated; `*` = allow all) — blocks drive-by no-preflight `text/plain` POSTs that any webpage can fire at localhost. Non-browser clients (MCP clients, curl) send no `Origin` and are unaffected. `Origin: null` is rejected.
- **Concurrency limit**: `SemaphoreSlim(4)` — max 4 simultaneous requests
- **`volatile _running`**: thread-safe flag, set after listener starts
- **Auth fail-closed**: if `RequireAuth=true` but `ApiToken` is empty, the server refuses to start (`InvalidOperationException`). Auth config is snapshotted at `StartAsync` so in-flight settings edits can't race the comparison. Token compared with `CryptographicOperations.FixedTimeEquals` (constant-time, no timing leak).
- **Mutation serialization**: destructive tools (`update_method_body`, `rename_*`) run under an exclusive `_mutationLock` so parallel batch requests can't race on dnlib metadata. Tool mutated-ness is detected by name prefix in `ToolRegistry.IsMutationTool`.
- **Non-blocking shutdown**: `Stop()` fire-and-forgets a short (3s) graceful drain so it never freezes the dnSpy UI thread.
- **Roslyn sandbox**: `BuildRoslynReferences()` loads only 5 core BCL assemblies (not full TPA). The target assembly is added as a `MetadataReference` so patch bodies can call its members — see the trust-boundary comment in `CompilePatch`.
- **Compilation timeout**: 10 seconds max via `Task.Run().WaitAsync()`
- **Tool execution timeout**: configurable via `ToolTimeoutSeconds` (default 30s). On timeout the in-flight work is **cancelled** (not just abandoned): `McpServerHost` opens a `ToolCallScope` (AsyncLocal) that `DnSpyDecompilerSourceProvider` forwards into dnSpy's `DecompilationContext.CancellationToken` — slow obfuscated-method decompiles stop burning CPU once the client gets the timeout error.

### WPF Thread Safety
 MCP tools run on **background threads** (accepted-connection handler tasks). All WPF TreeView/UI access must marshal to the UI thread:
```csharp
// CORRECT
var dispatcher = Application.Current?.Dispatcher;
if (dispatcher?.CheckAccess() == false)
    dispatcher.Invoke(() => { /* WPF access here */ });

// WRONG: direct access from background thread throws InvalidOperationException
```

`TreeViewTools.RunOnUIThread()` provides reusable helpers for Extension-only code. Core tool classes use `_ctx.UI.Invoke(...)` (the `IUIThreadScheduler` abstraction). Metadata mutation tools (rename, patch) auto-refresh tree view internally via `_ctx.TreeRefresh.RefreshAll()`.

### Server Endpoints & Auth

- **Health check**: `GET /health` or `GET /ping` — returns JSON with status, uptime, tools count
- **JSON-RPC**: `POST /` — all MCP tool calls go here
- **Auth**: When `RequireAuth=true`, requests must include `Authorization: Bearer <ApiToken>` header
- **Tool timeout**: Each tool call has a configurable timeout (default 30s via `ToolTimeoutSeconds`)
- **Configurable host/port**: Defaults to `127.0.0.1:5150`, configurable in dnSpy Options

## Tool Invocation Flow

```
AI agent POST http://127.0.0.1:5150/  (JSON-RPC 2.0 batch)
  → McpServerHost.HandleRequest()  (Core's HTTP transport)
    → ToolRegistry.GetTool("tool_name")
      → MethodInfo.Invoke(toolEntry.Instance, args)
        → instance is tool class injected with McpContext (e.g. DecompilerTools)
          → _ctx.AssemblyLoader / Resolver / SourceDecompiler / TreeRefresh
            → DnSpyDecompilerSourceProvider.DecompileMethod(method)
              → delegates to dnSpy.Contracts.Decompiler.IDecompiler (output identical to dnSpy.exe)
```

## Available MCP Tools (38)

Every element tool takes `token` (hex `'0x06000001'` or decimal) + optional `moduleMvid`, and returns the identity (token + name). `assembly`/pattern parameters exist only on discovery tools.

### Decompiler

| Tool | Description |
|------|-------------|
| `decompile_method` | C# source of a method (by MethodDef token) |
| `decompile_type` | C# source of an entire type (by TypeDef token) |
| `decompile_assembly` | First 10 types of assembly, each with its token |

### Search (discovery — returns tokens)

| Tool | Description |
|------|-------------|
| `search_types` | Find types by name pattern (`regex:` prefix for regex) |
| `search_methods` | Find methods by name, optionally scoped by `typeToken` |
| `search_strings` | Find string literals + the loading method's token |
| `grep` | Multi-scope search across types/methods/strings |

### Analysis

| Tool | Description |
|------|-------------|
| `get_method_il` | Raw IL instructions with stack/exception info (operands tokenized) |
| `get_il_opcodes_formatted` | Formatted IL opcodes with offsets (`IlDisplayTools`) |
| `get_method_signatures` | Method metadata: params, return, flags, generics |
| `get_type_hierarchy` | Inheritance chain, interfaces, member counts |
| `get_method_body` | IL bytes with MaxStack/InitLocals info |
| `update_method_body` | Patch method IL using C# statements (dry-run by default, token + optional `moduleMvid` scope, `IlPatchTools`) |

### Cross-References

| Tool | Description |
|------|-------------|
| `get_xrefs_to` | All references to a TypeDef/MethodDef/FieldDef token (token-keyed matching) |
| `get_callees` | Methods/fields called by a method token |

### Assembly

| Tool | Description |
|------|-------------|
| `load_assembly` | Load a DLL/EXE into dnSpy by absolute path |
| `close_assembly` | Unload an assembly by name |
| `list_loaded_assemblies` | List all binaries loaded in dnSpy (MVID + name) |
| `assembly_overview` | Module/assembly summary, type counts, entry point token |
| `assembly_list_namespaces` | All namespaces with their anchor type token |
| `assembly_list_types` | All types (optional regex filter) with metadata tokens |
| `assembly_get_references` | Assembly references (DLLs/NuGets) with their tokens |

### Resources & Metadata

| Tool | Description |
|------|-------------|
| `get_resources` | Embedded resources with manifest-resource tokens |
| `get_resource_data` | Raw bytes of a resource (by resource token) |
| `get_metadata` | PE headers, MVID, runtime version |
| `get_global_namespaces` | Types in the global namespace, with tokens |

### Type Inspection

| Tool | Description |
|------|-------------|
| `get_type_members` | List all members of a type with optional filter |
| `get_fields` | Detailed field info: type, access, static/const, values |
| `get_properties` | Property details: getter/setter (tokenized), type, access |

### Custom Attributes

| Tool | Description |
|------|-------------|
| `get_attributes` | Attributes on a type/method/field/property/event/module token with filter |
| `get_method_attributes` | Shortcut: attributes on a specific method token |

### Constants & Enums

| Tool | Description |
|------|-------------|
| `get_enum_values` | Enum members (by enum token) with name + value (hex + decimal) |
| `search_constants` | Search const/literal fields across assemblies |

### UI & Rename

| Tool | Description |
|------|-------------|
| `get_selected_node` | Currently selected node in TreeView, as token + name |
| `refresh_ui` | Refresh TreeView after metadata changes |
| `rename_namespace` | Rename a namespace located via the token of any TypeDef in it (dry-run by default) |
| `rename_class` | Rename one class located by TypeDef token (dry-run by default) |
| `rename_method` | Rename one method located by MethodDef token (dry-run by default) |

## API Conventions & Quirks

### Tool Response Envelope (token-keyed)

Every tool returns a JSON string (as MCP text content), built with
`ToolResponse.Success` / `ToolResponse.Failure`:

```csharp
// Success — target identity is the 2nd arg, tool-specific data the 3rd.
return ToolResponse.Success("get_type_members", MetadataIdentity.ForType(type), new JsonObject {
    ["methods"] = ToolResponse.Array(type.Methods.Select(MetadataIdentity.ForMethod)),
});

// Failure — same envelope, ok:false + error.
return ToolResponse.Failure("get_type_members", error!);
```

`MetadataIdentity.For*` emits `kind`, `token` (uint), `tokenHex`, `moduleMvid`,
`assembly`, then `name` + `nameIsMutable` (and `fullName`/`namespace` where
applicable). Token first, name last and flagged mutable — the token is the identity.

`McpServerHost` puts the returned string straight into `content[0].text`, so a tool must
return valid JSON (never prose). The 2 Extension-only UI tools use the same envelope.

### Decompiler API

```csharp
// CORRECT (3 params)
var output = new TextDecompilerOutput();
decompiler.Decompile(method, output, new DecompilationContext());
return output.ToString();  // NOT: output.Text

// WRONG: decompiler.Decompile(method, output)  // missing DecompilationContext
// WRONG: output.Text                              // property doesn't exist
```

### dnlib Quirks (token model)

```csharp
// ResolveToken returns the MD reader type. For most tables that type derives from the
// user model (TypeDefMD : TypeDef, MethodDefMD : MethodDef, AssemblyRefMD : AssemblyRef),
// so `is T` narrowing works.
var type = (TypeDef)mod.ResolveToken(0x02000001u);          // OK

// ManifestResource (table 0x28) is the exception: dnlib has TWO parallel families.
//   Resource          (abstract) -> EmbeddedResource / LinkedResource / AssemblyLinkedResource
//   ManifestResource  (abstract) -> ManifestResourceMD / ManifestResourceUser
// ModuleDefMD.ResolveToken(0x28000001) returns a ManifestResourceMD, which is NOT a
// Resource, while ModuleDef.Resources holds Resource instances with the SAME token.
// Use MethodResolver.ResolveResource(...), which projects the MD row onto the matching
// Resource by token/row id — never `is Resource` on a raw ResolveToken result.
```

### System.Text.Json 8.x Limitations

`JsonArray` does NOT implement LINQ — use for-loop iteration:

```csharp
var list = new List<JsonNode?>();
for (int i = 0; i < jsonArray.Count; i++)
    list.Add(jsonArray[i]);
```

### JSON-RPC Response

`HandleToolCall` returns the **full JSON-RPC response object**. Do NOT wrap again with `CreateResponse()`:

```csharp
// CORRECT
var callResult = HandleToolCall(req);
results.Add(isNotification ? null : callResult);

// WRONG: results.Add(CreateResponse(id, callResult)); // double-wraps!
```

<!-- code-review-graph MCP tools -->
## MCP Tools: code-review-graph

**IMPORTANT: This project has a knowledge graph. ALWAYS use the
code-review-graph MCP tools BEFORE using Grep/Glob/Read to explore
the codebase.** The graph is faster, cheaper (fewer tokens), and gives
you structural context (callers, dependents, test coverage) that file
scanning cannot.

### When to use graph tools FIRST

- **Exploring code**: `semantic_search_nodes` or `query_graph` instead of Grep
- **Understanding impact**: `get_impact_radius` instead of manually tracing imports
- **Code review**: `detect_changes` + `get_review_context` instead of reading entire files
- **Finding relationships**: `query_graph` with callers_of/callees_of/imports_of/tests_for
- **Architecture questions**: `get_architecture_overview` + `list_communities`

Fall back to Grep/Glob/Read **only** when the graph doesn't cover what you need.

### Key Tools

| Tool | Use when |
|------|----------|
| `detect_changes` | Reviewing code changes — gives risk-scored analysis |
| `get_review_context` | Need source snippets for review — token-efficient |
| `get_impact_radius` | Understanding blast radius of a change |
| `get_affected_flows` | Finding which execution paths are impacted |
| `query_graph` | Tracing callers, callees, imports, tests, dependencies |
| `semantic_search_nodes` | Finding functions/classes by name or keyword |
| `get_architecture_overview` | Understanding high-level codebase structure |
| `refactor_tool` | Planning renames, finding dead code |

### Workflow

1. The graph auto-updates on file changes (via hooks).
2. Use `detect_changes` for code review.
3. Use `get_affected_flows` to understand impact.
4. Use `query_graph` pattern="tests_for" to check coverage.
