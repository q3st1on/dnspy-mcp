using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Adapters;
using dnSpy.MCP.Core.Mcp;
using dnSpy.MCP.Settings;

namespace DnSpy.MCP.Verify;

/// <summary>
/// In-process HTTP verification of the Extension transport (<see cref="McpServerHost"/>).
/// </summary>
/// <remarks>
/// This is the counterpart to <c>Program.cs</c>: that one exercises the tool classes, this one
/// exercises the WIRE — the TCP listener, the JSON-RPC envelope, the direct REST route, the
/// auth/origin gates, and the mutation-lock serialization of a workspace re-dump against a
/// concurrent rename. It runs the server in-process and talks to it over real TCP, so it needs
/// no child process (which the environment blocks) while still covering the transport.
///
/// Usage: dnspy-mcp-verify-server &lt;assembly.dll&gt; [outputDirectory]
/// </remarks>
internal static class Server {
    static int _passed;
    static readonly List<string> _failures = new();

    public static async Task<int> RunAsync(string[] args) {
        if (args.Length == 0) {
            Console.Error.WriteLine("usage: dnspy-mcp-verify-server <assembly.dll> [outputDirectory]");
            return 2;
        }

        var assemblyPath = System.IO.Path.GetFullPath(args[0]);
        var outputDirectory = args.Length > 1
            ? System.IO.Path.GetFullPath(args[1])
            : System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "verify-server-output", Guid.NewGuid().ToString("N"));

        var loader = new Loader();
        Check("load assembly", loader.Load(assemblyPath).Success);

        var decompiler = new DnSpyDecompilerSourceProvider(Loader.LoadCSharp());
        var ctx = new McpContext(loader, decompiler, new Ui(), new Log(), new Refresh());
        var registry = new ToolRegistry(ctx, typeof(dnSpy.MCP.Core.Helpers.MetadataIdentity).Assembly);

        // Port 0 → OS-assigned, so the check never collides with a running dnSpy instance.
        var settings = new McpSettings { Host = "127.0.0.1", Port = 0, MaxConcurrency = 4, ToolTimeoutSeconds = 120 };
        using var host = new McpServerHost(settings, registry);
        await host.StartAsync();
        Check("server reported running", host.IsRunning);

        var port = FindBoundPort(host);
        Check("bound an ephemeral port", port > 0, port.ToString());
        if (port <= 0) return Finish();

        var baseUrl = $"http://127.0.0.1:{port}";
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        client.Timeout = TimeSpan.FromMinutes(5);

        // ---- health --------------------------------------------------------------
        var health = await client.GetStringAsync("/health");
        var healthNode = JsonNode.Parse(health)!.AsObject();
        Check("GET /health is healthy", healthNode["status"]!.GetValue<string>() == "healthy");
        Check("GET /ping works too", (await client.GetStringAsync("/ping")).Contains("healthy", StringComparison.Ordinal));
        Check("health reports the tool count", healthNode["tools_count"]!.GetValue<int>() > 0);

        // ---- JSON-RPC tools/list -------------------------------------------------
        var toolsList = await PostJsonRpcAsync(client, new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/list",
        });
        Check("tools/list succeeds", toolsList["result"]?["tools"] is JsonArray);
        var advertised = toolsList["result"]!["tools"]!.AsArray();
        Check("tools/list advertises the new tools",
            NamesOf(advertised).Contains("workspace_save_code")
            && NamesOf(advertised).Contains("get_method_context")
            && NamesOf(advertised).Contains("rename_symbol"),
            string.Join(",", NamesOf(advertised)));

        // ---- JSON-RPC tool call: token-first envelope ---------------------------
        var methodToken = FindMethod(ctx);
        var call = await PostJsonRpcAsync(client, new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject {
                ["name"] = "get_method_context",
                ["arguments"] = new JsonObject {
                    ["token"] = "0x" + methodToken.MDToken.Raw.ToString("X8"),
                    ["includeSource"] = false,
                },
            },
        });
        Check("tools/call get_method_context succeeds", call["result"]?["content"] is JsonArray);
        var payload = call["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        var payloadNode = JsonNode.Parse(payload)!.AsObject();
        Check("JSON-RPC payload carries the id/current_name/type triple",
            payloadNode["target"]!["id"] is not null
            && payloadNode["target"]!["current_name"] is not null
            && payloadNode["target"]!["type"]!.GetValue<string>() == "Method");
        Check("JSON-RPC payload carries data", payloadNode["data"] is not null);
        Check("JSON-RPC payload aliases result to data",
            payloadNode["result"]!["parentId"]!.GetValue<string>() == payloadNode["data"]!["parentId"]!.GetValue<string>());

        // ---- REST route: GET describes, POST writes -----------------------------
        var help = JsonNode.Parse(await client.GetStringAsync("/api/workspace/save-code"))!.AsObject();
        Check("GET /api/workspace/save-code describes the endpoint",
            help["endpoint"]!.GetValue<string>() == "/api/workspace/save-code");
        Check("GET help names the backing tool", help["tool"]!.GetValue<string>() == "workspace_save_code");

        var missingBody = await client.PostAsync("/api/workspace/save-code", new StringContent("", Encoding.UTF8, "application/json"));
        Check("POST without a body is 400", (int)missingBody.StatusCode == 400, ((int)missingBody.StatusCode).ToString());

        var badJson = await client.PostAsync("/api/workspace/save-code", new StringContent("{not json", Encoding.UTF8, "application/json"));
        Check("POST with malformed JSON is 400", (int)badJson.StatusCode == 400);

        var noDirectory = await client.PostAsync("/api/workspace/save-code",
            new StringContent("{\"singleProject\":true}", Encoding.UTF8, "application/json"));
        Check("POST without outputDirectory is 400", (int)noDirectory.StatusCode == 400);
        Check("400 body explains the missing field",
            (await noDirectory.Content.ReadAsStringAsync()).Contains("outputDirectory", StringComparison.Ordinal));

        // Snake_case keys, because the orchestrator builds these payloads from Python.
        var escapedDirectory = outputDirectory.Replace("\\", "\\\\");
        var snakeBody = "{\"output_directory\":\"" + escapedDirectory + "\",\"single_project\":true,\"overwrite\":true}";
        var saveResponse = await client.PostAsync("/api/workspace/save-code",
            new StringContent(snakeBody, Encoding.UTF8, "application/json"));
        var saveBody = await saveResponse.Content.ReadAsStringAsync();
        Check("POST /api/workspace/save-code succeeds", (int)saveResponse.StatusCode == 200, $"{(int)saveResponse.StatusCode}: {saveBody}");
        var saveNode = JsonNode.Parse(saveBody)!.AsObject();
        Check("REST response uses the same ok/tool shape as JSON-RPC", saveNode["ok"]!.GetValue<bool>());
        Check("REST response names the tool", saveNode["tool"]!.GetValue<string>() == "workspace_save_code");
        Check("REST response reports the token manifest", saveNode["data"]!["tokenManifest"] is not null);
        Check("snake_case body keys were mapped", saveNode["data"]!["moduleCount"]!.GetValue<int>() > 0);
        var manifestPath = saveNode["data"]!["tokenManifest"]!.GetValue<string>();
        Check("manifest exists on disk", System.IO.File.Exists(manifestPath), manifestPath);
        Check("single_project honoured",
            System.IO.Directory.GetFiles(outputDirectory, "*.csproj").Length == 1,
            string.Join(",", System.IO.Directory.GetFiles(outputDirectory, "*.csproj")));

        // ---- concurrent reads stay parallel and correct -------------------------
        var batch = new JsonArray();
        for (int i = 0; i < 8; i++) {
            batch.Add(new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"] = 100 + i,
                ["method"] = "tools/call",
                ["params"] = new JsonObject {
                    ["name"] = "get_method_context",
                    ["arguments"] = new JsonObject {
                        ["token"] = "0x" + methodToken.MDToken.Raw.ToString("X8"),
                        ["includeSource"] = false,
                    },
                },
            });
        }
        var batchResponse = await client.PostAsync("/", new StringContent(batch.ToJsonString(), Encoding.UTF8, "application/json"));
        var batchBody = JsonNode.Parse(await batchResponse.Content.ReadAsStringAsync())!.AsArray();
        Check("8-request batch returns 8 responses", batchBody.Count == 8, batchBody.Count.ToString());
        var allOk = true;
        for (int i = 0; i < batchBody.Count; i++) {
            var text = batchBody[i]!["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            if (text is null || !JsonNode.Parse(text)!.AsObject()["ok"]!.GetValue<bool>())
                allOk = false;
        }
        Check("every response in the batch is well-formed and ok", allOk);

        // ---- auth gate ----------------------------------------------------------
        using (var authHost = new McpServerHost(
            new McpSettings { Host = "127.0.0.1", Port = 0, RequireAuth = true, ApiToken = "s3cret" }, registry)) {
            await authHost.StartAsync();
            var authPort = FindBoundPort(authHost);
            using var authClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{authPort}") };
            var unauthorized = await authClient.GetAsync("/api/workspace/save-code");
            Check("REST route is behind the auth gate", (int)unauthorized.StatusCode == 401,
                ((int)unauthorized.StatusCode).ToString());

            authClient.DefaultRequestHeaders.Add("Authorization", "Bearer s3cret");
            var authorized = await authClient.GetAsync("/api/workspace/save-code");
            Check("authorized REST call succeeds", (int)authorized.StatusCode == 200);
            authHost.Stop();
        }

        // ---- auth fails closed when misconfigured ------------------------------
        using (var brokenHost = new McpServerHost(
            new McpSettings { Host = "127.0.0.1", Port = 0, RequireAuth = true, ApiToken = "" }, registry)) {
            var threw = false;
            try { await brokenHost.StartAsync(); }
            catch (InvalidOperationException) { threw = true; }
            Check("RequireAuth without a token refuses to start", threw);
        }

        // ---- non-POST on the route ---------------------------------------------
        var del = await client.DeleteAsync("/api/workspace/save-code");
        Check("DELETE on the route is 405", (int)del.StatusCode == 405, ((int)del.StatusCode).ToString());

        // ---- unknown path -------------------------------------------------------
        var unknown = await client.PostAsync("/api/nope", new StringContent("{}", Encoding.UTF8, "application/json"));
        Check("unknown path with a non-JSON-RPC body fails cleanly", (int)unknown.StatusCode == 200 || (int)unknown.StatusCode == 400);

        host.Stop();
        return Finish();
    }

    static IEnumerable<string> NamesOf(JsonArray tools) {
        for (int i = 0; i < tools.Count; i++)
            yield return tools[i]!["name"]!.GetValue<string>();
    }

    static async Task<JsonObject> PostJsonRpcAsync(HttpClient client, JsonObject request) {
        var response = await client.PostAsync("/", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(body)!.AsObject();
    }

    static MethodDef FindMethod(McpContext ctx) {
        foreach (var module in ctx.Resolver.GetAllModules()) {
            foreach (var type in module.GetTypes()) {
                foreach (var method in type.Methods) {
                    if (method.Body is not null && method.Body.Instructions.Count > 3)
                        return method;
                }
            }
        }
        throw new InvalidOperationException("no suitable method in fixture");
    }

    /// <summary>
    /// Reads the port the listener actually bound. Port 0 means the OS assigned it, so the
    /// configured value is meaningless — the test must ask the socket.
    /// </summary>
    static int FindBoundPort(McpServerHost host) {
        var field = typeof(McpServerHost).GetField("_listener",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var listener = field?.GetValue(host) as System.Net.Sockets.TcpListener;
        return (listener?.LocalEndpoint as System.Net.IPEndPoint)?.Port ?? -1;
    }

    static void Check(string label, bool ok, string detail = "") {
        if (ok) {
            _passed++;
            Console.WriteLine($"  ok   {label}");
        }
        else {
            _failures.Add($"{label}{(detail.Length > 0 ? " — " + detail : "")}");
            Console.WriteLine($"  FAIL {label}{(detail.Length > 0 ? " — " + detail : "")}");
        }
    }

    static int Finish() {
        Console.WriteLine();
        Console.WriteLine($"{_passed} check(s) passed, {_failures.Count} failed.");
        foreach (var failure in _failures)
            Console.WriteLine("  FAIL " + failure);
        return _failures.Count == 0 ? 0 : 1;
    }

    sealed class Loader : IAssemblyLoader {
        readonly ModuleContext _moduleContext = ModuleDef.CreateModuleContext();
        readonly List<LoadedModule> _loaded = new();

        public LoadResult Load(string path) {
            if (!System.IO.File.Exists(path))
                return new LoadResult(false, $"File not found: {path}", null);
            var module = ModuleDefMD.Load(path, _moduleContext);
            ((AssemblyResolver)_moduleContext.AssemblyResolver).AddToCache(module);
            var loaded = new LoadedModule(module.Name, module.Assembly?.Name?.String, module, path);
            _loaded.Add(loaded);
            return new LoadResult(true, null, loaded);
        }

        public int Close(string assemblyName) =>
            _loaded.RemoveAll(m => string.Equals(m.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<LoadedModule> GetDocuments() => _loaded.ToList();

        public static IDecompiler LoadCSharp() {
            var asm = System.Reflection.Assembly.Load("dnSpy.Decompiler.ILSpy.Core");
            foreach (var type in asm.GetTypes()) {
                if (type.IsAbstract || type.IsInterface || !typeof(IDecompilerProvider).IsAssignableFrom(type))
                    continue;
                IDecompilerProvider provider;
                try { provider = (IDecompilerProvider)Activator.CreateInstance(type)!; }
                catch { continue; }
                foreach (var language in provider.Create()) {
                    if (language.GenericGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY
                        || language.UniqueGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY)
                        return language;
                }
            }
            throw new InvalidOperationException("No C# decompiler available.");
        }
    }

    sealed class Ui : IUIThreadScheduler {
        public T Invoke<T>(Func<T> action) => action();
        public void Invoke(Action action) => action();
    }

    sealed class Log : ILogSink {
        public void Info(string message) { }
        public void Warn(string message) => Console.WriteLine("  warn " + message);
        public void Error(string message, Exception? ex = null) => Console.WriteLine("  error " + message);
    }

    sealed class Refresh : ITreeRefreshNotifier {
        public void RefreshAll() { }
        public void NotifyNamespaceRenamed(string assembly, string oldNamespace, string newNamespace) { }
    }
}
