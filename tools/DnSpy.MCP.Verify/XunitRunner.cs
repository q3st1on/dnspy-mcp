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
    public static int Run(string testAssemblyPath, bool includeE2E) {
        var assembly = Assembly.LoadFrom(System.IO.Path.GetFullPath(testAssemblyPath));

        // The headless E2E fixtures spawn the real stdio server as a child process. That works in
        // this environment, but it is slow and environment-sensitive, so it is opt-in — the
        // default run answers "did my change break the tool contract" without it.
        Environment.SetEnvironmentVariable("DNSPY_MCP_VERIFY_SKIP_E2E", includeE2E ? null : "1");

        var cases = new List<(string Name, Func<Task> Body)>();
        var skipped = 0;

        foreach (var type in assembly.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal)) {
            if (!type.IsClass || type.IsAbstract)
                continue;
            var isTestClass = type.GetMethods().Any(m =>
                m.GetCustomAttribute<FactAttribute>() is not null);
            if (!isTestClass)
                continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .OrderBy(m => m.Name, StringComparer.Ordinal)) {
                var fact = method.GetCustomAttribute<FactAttribute>();
                if (fact is null)
                    continue;

                var displayName = $"{type.Name}.{method.Name}";

                // [Fact(Skip = "...")] and TheoryData-less theories we cannot expand are skipped
                // rather than failed: a skip is the author's statement, not a regression.
                if (!string.IsNullOrEmpty(fact.Skip)) {
                    skipped++;
                    Console.WriteLine($"  SKIP {displayName} — {fact.Skip}");
                    continue;
                }

                if (method.GetCustomAttribute<TheoryAttribute>() is not null) {
                    var inlineData = method.GetCustomAttributes<InlineDataAttribute>().ToList();
                    if (inlineData.Count == 0) {
                        skipped++;
                        Console.WriteLine($"  SKIP {displayName} — theory with no InlineData (MemberData/ClassData not supported by this runner)");
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

        Console.WriteLine($"Discovered {cases.Count} test case(s), {skipped} skipped.");
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
        Console.WriteLine($"{passed} passed, {failures.Count} failed, {skipped} skipped.");

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
