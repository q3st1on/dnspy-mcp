using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Xunit;

namespace DnSpy.MCP.Verify;

/// <summary>
/// Minimal in-process xUnit runner.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: <c>dotnet test</c> cannot run in every environment. VSTest's testhost calls
/// <c>Process.EnableRaisingEvents</c> on the process that launched it, and where the environment
/// denies <c>OpenProcess</c> on that handle the testhost dies with
/// <c>Win32Exception (5): Access is denied</c> before a single test executes. The build succeeds;
/// only the runner cannot start. (Verified: spawning a child and watching it works fine here, and
/// redirected stdio works too — the denial is specific to the handle VSTest opens on ITS parent.)
/// </para>
/// <para>
/// This runner discovers and executes the same test assembly directly in this process: no
/// testhost, no child process, no handle on a parent. It supports <c>[Fact]</c>, <c>[Theory]</c>
/// with <c>[InlineData]</c>, async tests, and <c>IDisposable</c> test classes.
/// </para>
/// <para>
/// It is a runner, not a reimplementation: the tests themselves are untouched, so a suite that
/// passes here passes under <c>dotnet test</c> wherever that works.
/// </para>
/// </remarks>
internal static class XunitRunner {
    /// <summary>Test classes that need a spawned child server; opt-in via <c>--e2e</c>.</summary>
    static readonly string[] s_processSpawningTestClasses = { "HeadlessE2ETests" };

    /// <summary>
    /// Runs the suite. <paramref name="includeE2E"/> enables the classes that spawn the real
    /// headless server as a child process; <paramref name="tempDirectory"/> redirects the
    /// process temp path for the duration of the run (some environments deny the default);
    /// <paramref name="baseDirectory"/> overrides what <see cref="AppContext.BaseDirectory"/>
    /// reports, which fixtures use to locate sibling build output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base-directory override is not cosmetic. <c>AppContext.BaseDirectory</c> answers "where
    /// did MY entry assembly come from", so when this harness loads the test assembly it still
    /// reports the harness's own output directory — and the E2E fixture, which computes the
    /// headless server's location as four levels up from there, then looks in the wrong tree and
    /// fails with "headless build output not found". Overriding it to the test assembly's own
    /// directory reproduces exactly what <c>dotnet test</c> presents to the suite, without editing
    /// a single test.
    /// </para>
    /// <para>
    /// It must be installed by an assembly-level hook: <c>AppContext.BaseDirectory</c> is a
    /// get-only property over run-time state, so there is nothing to assign and no earlier place
    /// to set it than static initialization.
    /// </para>
    /// </remarks>
    public static int Run(string testAssemblyPath, bool includeE2E, string? tempDirectory, string? baseDirectory = null) {
        var testAssemblyFullPath = System.IO.Path.GetFullPath(testAssemblyPath);
        var testDirectory = System.IO.Path.GetDirectoryName(testAssemblyFullPath)!;

        var effectiveBase = string.IsNullOrWhiteSpace(baseDirectory)
            ? testDirectory
            : System.IO.Path.GetFullPath(baseDirectory);
        Environment.SetEnvironmentVariable(BaseDirectoryOverride.VariableName, effectiveBase);

        // Probe the TEST output directory for anything the suite references. Assembly.LoadFrom
        // does not change AppContext.BaseDirectory, and the runner's own output dir stays the
        // process base, so this makes the test assembly's dependencies load from where they
        // actually live.
        ResolveEventHandler resolver = (_, eventArgs) => {
            var simpleName = new AssemblyName(eventArgs.Name).Name;
            if (string.IsNullOrEmpty(simpleName))
                return null;
            var candidate = System.IO.Path.Combine(testDirectory, simpleName + ".dll");
            return System.IO.File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolver;
        try {
            BaseDirectoryOverride.Install();
            Console.WriteLine($"AppContext.BaseDirectory -> {effectiveBase}");
            return RunCore(testAssemblyFullPath, includeE2E, tempDirectory);
        }
        finally {
            AppDomain.CurrentDomain.AssemblyResolve -= resolver;
        }
    }

    static int RunCore(string testAssemblyPath, bool includeE2E, string? tempDirectory) {
        var assembly = Assembly.LoadFrom(testAssemblyPath);

        // Path.GetTempPath() is read once and cached, so these must be set before any test calls
        // it. Pointing TEMP/TMP at a writable directory is what lets the tests that create scratch
        // folders run in an environment whose real %TEMP% is read-only.
        if (!string.IsNullOrWhiteSpace(tempDirectory)) {
            var full = System.IO.Path.GetFullPath(tempDirectory);
            System.IO.Directory.CreateDirectory(full);
            Environment.SetEnvironmentVariable("TEMP", full);
            Environment.SetEnvironmentVariable("TMP", full);
            Console.WriteLine($"Temp directory redirected to {full}");
        }

        var cases = new List<(string Name, Func<Task> Body)>();
        var skipped = new List<(string Name, string Reason)>();

        foreach (var type in assembly.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal)) {
            if (!type.IsClass || type.IsAbstract)
                continue;
            var isTestClass = type.GetMethods().Any(m =>
                m.GetCustomAttribute<FactAttribute>() is not null);
            if (!isTestClass)
                continue;

            var needsChildProcess = s_processSpawningTestClasses.Contains(type.Name, StringComparer.Ordinal);
            if (needsChildProcess && !includeE2E) {
                skipped.Add((type.Name, "spawns the headless server as a child process; pass --e2e to include it"));
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .OrderBy(m => m.Name, StringComparer.Ordinal)) {
                var fact = method.GetCustomAttribute<FactAttribute>();
                if (fact is null)
                    continue;

                var displayName = $"{type.Name}.{method.Name}";

                // [Fact(Skip = "...")] is the author's statement, not a regression.
                if (!string.IsNullOrEmpty(fact.Skip)) {
                    skipped.Add((displayName, fact.Skip!));
                    continue;
                }

                if (method.GetCustomAttribute<TheoryAttribute>() is not null) {
                    var inlineData = method.GetCustomAttributes<InlineDataAttribute>().ToList();
                    if (inlineData.Count == 0) {
                        skipped.Add((displayName, "theory with no InlineData (MemberData/ClassData are not supported by this runner)"));
                        continue;
                    }
                    foreach (var data in inlineData) {
                        var arguments = data.GetData(method).FirstOrDefault();
                        var label = $"{displayName}({string.Join(", ", (arguments ?? Array.Empty<object>()).Select(Format))})";
                        cases.Add((label, () => InvokeAsync(type, method, arguments)));
                    }
                    continue;
                }

                cases.Add((displayName, () => InvokeAsync(type, method, null)));
            }
        }

        Console.WriteLine($"Discovered {cases.Count} test case(s), {skipped.Count} skipped.");
        Console.WriteLine();

        var passed = 0;
        var failures = new List<(string Name, Exception Error)>();

        foreach (var (name, body) in cases) {
            try {
                body().GetAwaiter().GetResult();
                passed++;
                Console.WriteLine($"  PASS {name}");
            }
            catch (Exception ex) {
                var inner = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : ex;
                failures.Add((name, inner));
                Console.WriteLine($"  FAIL {name}");
                Console.WriteLine($"       {inner.GetType().Name}: {FirstLine(inner.Message)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failures.Count} failed, {skipped.Count} skipped.");

        if (skipped.Count > 0) {
            Console.WriteLine();
            Console.WriteLine("Skipped:");
            foreach (var (name, reason) in skipped)
                Console.WriteLine($"  {name} — {reason}");
        }

        if (failures.Count > 0) {
            Console.WriteLine();
            Console.WriteLine("Failures:");
            foreach (var (name, error) in failures) {
                Console.WriteLine($"  {name}");
                Console.WriteLine($"    {error.GetType().Name}: {FirstLine(error.Message)}");
                if (error.StackTrace is not null) {
                    foreach (var frame in error.StackTrace.Split('\n').Take(4))
                        Console.WriteLine($"      {frame.Trim()}");
                }
            }
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Runs one test method. Constructs a fresh instance per case (xUnit semantics: no shared
    /// state between cases) and disposes it if it implements IDisposable.
    /// </summary>
    static async Task InvokeAsync(Type type, MethodInfo method, object?[]? arguments) {
        object? instance = null;
        try {
            instance = Activator.CreateInstance(type);
            var result = method.Invoke(instance, arguments);
            if (result is Task task)
                await task;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null) {
            // Rethrow with the ORIGINAL stack so the failure points at the assertion, not at
            // reflection's invocation frame.
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
        finally {
            if (instance is IDisposable disposable) {
                try { disposable.Dispose(); }
                catch { /* a disposal failure must not mask the assertion result */ }
            }
        }
    }

    static string Format(object? value) => value switch {
        null => "null",
        string s => "\"" + (s.Length > 24 ? s.Substring(0, 24) + "…" : s) + "\"",
        _ => value.ToString() ?? "?",
    };

    static string FirstLine(string text) {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text.Substring(0, newline).TrimEnd('\r');
    }
}

/// <summary>
/// Repoints <see cref="AppContext.BaseDirectory"/> at the test assembly's own directory.
/// </summary>
/// <remarks>
/// <para>
/// <c>AppContext.BaseDirectory</c> answers "where did the ENTRY assembly come from". A fixture
/// that computes a sibling project's output path from it only works when the test assembly IS the
/// entry assembly — true under <c>dotnet test</c>, false when a host loads the test assembly
/// in-process. The E2E fixture does exactly that (four levels up from the base directory → the
/// headless server's build output), so without this it looks in the wrong tree.
/// </para>
/// <para>
/// The mechanism is an <see cref="AppDomain"/> data slot, NOT the private backing field. Verified
/// on .NET 10: setting <c>s_defaultBaseDirectory</c> changes that field's value but leaves
/// <c>AppContext.BaseDirectory</c> reporting the original directory, while
/// <c>SetData("APP_CONTEXT_BASE_DIRECTORY", …)</c> changes what the property returns. Setting the
/// wrong one is a silent no-op, which is how this override first appeared to work while the
/// fixture still read the host's directory.
/// </para>
/// <para>
/// The slot is re-asserted on every assembly load because the runtime can initialize it while the
/// test assembly is still loading. The whole mechanism is inert unless the environment variable
/// behind <see cref="VariableName"/> is set, so a plain run is unaffected.
/// </para>
/// </remarks>
internal static class BaseDirectoryOverride {
    internal const string VariableName = "DNSPY_MCP_VERIFY_BASE_DIRECTORY";

    /// <summary>The AppDomain data slot <c>AppContext.BaseDirectory</c> reads.</summary>
    const string AppContextBaseDirectorySlot = "APP_CONTEXT_BASE_DIRECTORY";

    public static void Install() {
        var configured = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrWhiteSpace(configured))
            return;

        var directory = System.IO.Path.GetFullPath(configured);
        Apply(directory);

        // The runtime sets this slot while loading the entry assembly; re-assert so nothing that
        // loads later can restore the host's directory underneath us.
        AppDomain.CurrentDomain.AssemblyLoad += (_, _) => Apply(directory);
    }

    static void Apply(string directory) {
        AppDomain.CurrentDomain.SetData(AppContextBaseDirectorySlot, directory);
        // Belt and braces: if a future runtime stops consulting the slot and goes back to the
        // field, keep them in step. Harmless when the field does not exist or is unused.
        typeof(AppContext)
            .GetField("s_defaultBaseDirectory", BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, directory);
    }
}
