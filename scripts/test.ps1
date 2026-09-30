<#
.SYNOPSIS
    Runs the xUnit suite in-process, with no VSTest testhost (and therefore no elevation).

.DESCRIPTION
    `dotnet test` needs VSTest's testhost, which calls Process.EnableRaisingEvents on the process
    that launched it. Where the environment denies OpenProcess on that handle, the testhost dies
    with "Win32Exception (5): Access is denied" before a single test runs — the BUILD succeeds and
    only the runner fails. On this repository's development machines, running `dotnet test` from an
    ELEVATED shell fixes it (73/73 pass); this script is the non-elevated equivalent, and produces
    the same 73/73 by running the same test assembly in-process.

    What it does, and why each step is needed:
      1. Builds the solution in the requested configuration, because the E2E fixture locates the
         headless server under src/dnSpy.MCP.Headless/bin/<cfg>/ — it looks for Release first, so
         Release is the default here.
      2. Redirects the process temp path (--temp) into the test project's obj/ folder. Some
         environments make the real %TEMP% read-only, which fails the tests that create scratch
         folders — an environment limitation, not a test bug.
      3. Overrides AppContext.BaseDirectory to the test assembly's own output directory. The E2E
         fixture derives the headless server's location from it, and in-process the base directory
         would otherwise point at this harness instead of the tests.
      4. Runs the suite. -E2E additionally enables the classes that spawn the real headless server
         as a child process.

.PARAMETER Configuration
    Build/test configuration. Default Release (matches the headless lookup order).

.PARAMETER E2E
    Also run the headless end-to-end tests that spawn a child server process.

.PARAMETER NoBuild
    Skip the solution build (assumes it is already current).

.EXAMPLE
    pwsh scripts/test.ps1
    pwsh scripts/test.ps1 -E2E
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$E2E,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'dnspy_mcp.sln'
$verifyProject = Join-Path $repoRoot 'tools/DnSpy.MCP.Verify'
$testDll = Join-Path $repoRoot "src/dnSpy.MCP.Tests/bin/$Configuration/net10.0-windows/dnSpy.MCP.Tests.dll"
# A temp directory inside obj/ is writable wherever the repository is writable.
$tempDir = Join-Path $repoRoot "src/dnSpy.MCP.Tests/obj/verify-temp-$Configuration"

if (-not $NoBuild) {
    Write-Host "=== Building $Configuration ===" -ForegroundColor Cyan
    # -m:1 for the same cross-project-restore reason documented in scripts/build.ps1.
    & dotnet build $solution -c $Configuration -m:1 --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed." -ForegroundColor Red
        exit 1
    }
}

if (-not (Test-Path $testDll)) {
    Write-Host "Test assembly not found: $testDll" -ForegroundColor Red
    Write-Host "Build it first (or drop -NoBuild)." -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "=== In-process xUnit run ===" -ForegroundColor Cyan
Write-Host "  assembly: $testDll"
Write-Host "  temp:     $tempDir"
Write-Host "  e2e:      $(if ($E2E) { 'included' } else { 'skipped (pass -E2E)' })"
Write-Host ""

# --base-directory is the load-bearing flag: the suite resolves sibling build output (the
# headless server) from AppContext.BaseDirectory, which answers "where did the ENTRY assembly
# come from". In-process that is this harness's output, not the tests'; overriding it to the test
# assembly's own directory reproduces exactly what `dotnet test` presents to the suite.
$runArgs = @('run', '--project', $verifyProject, '-c', $Configuration, '--no-build', '--',
    'xunit', $testDll, '--temp', $tempDir, '--base-directory', (Split-Path -Parent $testDll))
if ($E2E) { $runArgs += '--e2e' }

& dotnet @runArgs
$exitCode = $LASTEXITCODE

# Leave nothing behind (best effort: a locked directory must not fail the run).
try { if (Test-Path $tempDir) { Remove-Item -LiteralPath $tempDir -Recurse -Force } } catch { }

exit $exitCode
