<#
.SYNOPSIS
    Guard against CLAUDE.md / tool-count contract drift.

.DESCRIPTION
    Counts MCP tools discovered by the same reflection rules ToolRegistry uses
    ([Description] on a public string method in dnSpy.MCP.Tools*) and
    cross-checks against the count advertised in CLAUDE.md. Fails (exit 1) on
    mismatch so CI catches a tool added in code but not documented (or vice
    versa) — the same drift that once let load_assembly/close_assembly be
    advertised but unimplemented.

    Scans BOTH Core tools (instance methods on sealed classes with McpContext ctor)
    and Extension-only tools (static methods on static classes, e.g. TreeViewTools).

    Usage in CI:
        pwsh scripts/verify-tool-count.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$coreToolsDir = Join-Path $RepoRoot 'src/dnSpy.MCP.Core/Tools'
$extensionToolsDir = Join-Path $RepoRoot 'src/dnSpy.MCP/Tools'
$claudeMd = Join-Path $RepoRoot 'CLAUDE.md'

if (-not (Test-Path $coreToolsDir)) { throw "Core tools dir not found: $coreToolsDir" }
if (-not (Test-Path $extensionToolsDir)) { throw "Extension tools dir not found: $extensionToolsDir" }
if (-not (Test-Path $claudeMd)) { throw "CLAUDE.md not found: $claudeMd" }

# A tool = a [Description(...)] attribute whose next non-blank line declares a
# `public string Method(` (instance, Core) OR `public static string Method(` (static, Extension).
# This mirrors ToolRegistry.DiscoverTools() which accepts both BindingFlags.Instance
# and BindingFlags.Static depending on whether the class has ctor(McpContext).
$toolNames = New-Object System.Collections.Generic.List[string]

# Regex matches BOTH `public string X(` and `public static string X(`.
# The (?:static\s+)? group makes 'static' optional.
$methodRegex = 'public\s+(?:static\s+)?string\s+(\w+)\s*\('

function Scan-ToolDir {
    param([string]$DirPath)
    # A tool is a `[Description(...)]`-attributed public string method. The attribute may be
    # multi-line (a long description wraps), so this tracks "a [Description( has been seen" and
    # pairs it with the NEXT `public string Method(` — the same rule ToolRegistry uses
    # (GetCustomAttribute<DescriptionAttribute>() on a public string method), just parsed from
    # text. Pairing on the immediately-following line would silently MISS a wrapped attribute
    # and under-count, which is exactly how workspace_save_code went undetected.
    Get-ChildItem -Path $DirPath -Filter *.cs | ForEach-Object {
        $lines = Get-Content -LiteralPath $_.FullName
        $descriptionSeen = $false
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\s*\[Description\(') {
                $descriptionSeen = $true
                continue
            }
            if (-not $descriptionSeen) { continue }
            if ($lines[$i] -match $methodRegex) {
                $method = $Matches[1]
                # snake_case conversion mirroring ToolRegistry.ToSnakeCase exactly:
                # '_' before an uppercase at a word boundary — after lower/digit, or when the
                # last uppercase of an acronym is followed by a lowercase. Trailing acronyms
                # stay clamped: "RefreshUI" -> "refresh_ui", not "refresh_u_i".
                $sb = New-Object System.Text.StringBuilder($method.Length + 10)
                for ($k = 0; $k -lt $method.Length; $k++) {
                    $ch = $method[$k]
                    if ($k -gt 0 -and [char]::IsUpper($ch)) {
                        $prev = $method[$k - 1]
                        $next = if ($k + 1 -lt $method.Length) { $method[$k + 1] } else { [char]0 }
                        $isBoundary = ([char]::IsLower($prev) -or [char]::IsDigit($prev)) -or
                                      ([char]::IsUpper($prev) -and [char]::IsLower($next))
                        if ($isBoundary) { [void]$sb.Append('_') }
                    }
                    [void]$sb.Append([char]::ToLowerInvariant($ch))
                }
                $toolNames.Add($sb.ToString())
                $descriptionSeen = $false
                continue
            }
            # A declaration that is not a tool method (a private helper, a property) ends the
            # search for this attribute rather than pairing it with something later in the file.
            if ($lines[$i] -match '^\s*(public|private|internal|protected)\s' -and $lines[$i] -notmatch '^\s*\[') {
                $descriptionSeen = $false
            }
        }
    }
}

Scan-ToolDir $coreToolsDir
Scan-ToolDir $extensionToolsDir

# Count advertised tools in CLAUDE.md header ("## Available MCP Tools (NN)")
$claudeText = Get-Content -Raw -LiteralPath $claudeMd
$advertised = $null
if ($claudeText -match '## Available MCP Tools \((\d+)\)') {
    $advertised = [int]$Matches[1]
}

Write-Host "CLAUDE.md advertises: $(if ($null -eq $advertised) { '(header not found)' } else { $advertised })"

# Fail closed: a missing/mangled header must not silently disable the drift check —
# that is exactly the failure class this guard exists to catch. (Previously a missing
# header left $advertised null and the mismatch check was skipped, exiting 0.)
if ($null -eq $advertised) {
    Write-Error "CLAUDE.md tool-count header '## Available MCP Tools (NN)' not found — cannot verify tool count."
    exit 1
}

# The registry is the authority on what the Headless/Core host actually exposes, so ask IT
# which Core tools it registers rather than re-deriving that half from a text scan. A long
# [Description] attribute can wrap across lines, and a line-pairing scan misses it — that is
# exactly how workspace_save_code went uncounted while the runtime registered it. Comparing NAME
# SETS (not just counts) also catches two errors cancelling out into a matching total.
#
# Extension-only tools (TreeViewTools) are counted from source: the verification harness is a
# plain console target and deliberately does NOT load the WPF/MEF Extension assembly.
$failed = $false
$registryCoreNames = @()
$verifyProject = Join-Path $RepoRoot 'tools/DnSpy.MCP.Verify/DnSpy.MCP.Verify.csproj'
$coreDll = Join-Path $RepoRoot 'src/dnSpy.MCP.Core/bin/Release/net10.0-windows/dnSpy.MCP.Core.dll'
$actual = $toolNames.Count

if ((Test-Path $verifyProject) -and (Test-Path $coreDll)) {
    $registryCoreNames = & dotnet run --project $verifyProject -c Release --no-build -- tools 2>$null |
        Where-Object { $_ -match '^[a-z0-9_]+$' } |
        Sort-Object
}
else {
    Write-Warning ("Could not run the registry counter (tools/DnSpy.MCP.Verify is not built, or the " +
        "Core Release output is missing) — falling back to the source scan alone.")
}

if ($registryCoreNames.Count -gt 0) {
    Write-Host "Registry reports $($registryCoreNames.Count) Core tool(s)."

    # Split the scan by origin: anything the registry also reports is a Core tool; the rest can
    # only come from the Extension tools directory.
    $scanCore = @($toolNames | Where-Object { $registryCoreNames -contains $_ })
    $scanExtensionOnly = @($toolNames | Where-Object { $registryCoreNames -notcontains $_ })

    $onlyInScan = Compare-Object -ReferenceObject ($scanCore | Sort-Object) -DifferenceObject $registryCoreNames |
        Where-Object { $_.SideIndicator -eq '<=' } | ForEach-Object { $_.InputObject }
    $onlyInRegistry = Compare-Object -ReferenceObject ($scanCore | Sort-Object) -DifferenceObject $registryCoreNames |
        Where-Object { $_.SideIndicator -eq '=>' } | ForEach-Object { $_.InputObject }
    if ($onlyInScan) {
        Write-Error ("MISMATCH: the source scan counts Core tools the registry does not register: " +
            ($onlyInScan -join ', '))
        $failed = $true
    }
    if ($onlyInRegistry) {
        Write-Error ("MISMATCH: the registry registers Core tools the source scan cannot find (a wrapped " +
            "[Description] attribute is the usual cause): " + ($onlyInRegistry -join ', '))
            $failed = $true
    }
    if ($scanExtensionOnly.Count -gt 0) {
        Write-Host "Extension-only tool(s) from source: $($scanExtensionOnly -join ', ')"
    }

    $expectedTotal = $registryCoreNames.Count + $scanExtensionOnly.Count
    if ($advertised -ne $expectedTotal) {
        Write-Error ("MISMATCH: CLAUDE.md advertises $advertised tools, but the registry exposes " +
            "$($registryCoreNames.Count) Core tool(s) and the scan found $($scanExtensionOnly.Count) " +
            "Extension-only tool(s) = $expectedTotal.")
        $failed = $true
    }
}
elseif ($advertised -ne $actual) {
    Write-Error "MISMATCH: CLAUDE.md advertises $advertised tools but $actual were discovered."
    $failed = $true
}

if ($failed) { exit 1 }
Write-Host ""
Write-Host "OK: tool counts consistent ($actual total = $($registryCoreNames.Count) Core + $($actual - $registryCoreNames.Count) Extension-only)."
exit 0
