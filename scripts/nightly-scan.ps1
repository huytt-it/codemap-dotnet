<#
.SYNOPSIS
    Re-runs the full CodeMap index for one repo (spec section 1, "Do tuoi": index is scanned once a day via
    Windows Task Scheduler, not incrementally).

.DESCRIPTION
    Pure orchestration - calls the same CLI commands a person would type by hand, in the order the tool's own
    data dependencies require (scan before scan-git/scan-fe/link/map; link before map so entry points show their
    linked FE screens). No logic here that isn't already in the CLI itself.

    'scan' is the only step whose failure aborts the run - a missing/broken solution means every later step
    would operate on a stale or empty index, which is worse than not running at all. scan-git and the
    scan-fe/link pair are treated as optional enrichment: a repo with no git history yet, or no frontend
    configured, should still get a usable MAP.md instead of the whole job failing.

.PARAMETER Solution
    Path to the backend .sln to index.
.PARAMETER OutDir
    Output directory (will contain index/, MAP.md, modules/, logs/).
.PARAMETER FrontendRoot
    Optional Angular/jQuery frontend root. Omit to skip scan-fe/link entirely.
.PARAMETER CodeMapDll
    Path to CodeMap.Cli.dll. Defaults to the Release build next to this script's repo checkout.
.PARAMETER LogDir
    Defaults to <OutDir>\logs.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File nightly-scan.ps1 `
        -Solution "D:\Repos\MyApp\MyApp.sln" -OutDir "D:\CodeMapIndex\MyApp" -FrontendRoot "D:\Repos\MyApp.Web"

    See docs/OPS-NIGHTLY-SCAN.md for how to register this as a nightly Windows Task Scheduler job.
#>
param(
    [Parameter(Mandatory = $true)] [string]$Solution,
    [Parameter(Mandatory = $true)] [string]$OutDir,
    [string]$FrontendRoot,
    [string]$CodeMapDll = "$PSScriptRoot\..\CodeMap.Cli\bin\Release\net8.0\CodeMap.Cli.dll",
    [string]$LogDir
)

if (-not (Test-Path $CodeMapDll)) {
    Write-Error "CodeMap.Cli.dll not found at '$CodeMapDll'. Build it first: dotnet build CodeMap.Cli -c Release"
    exit 1
}
if (-not (Test-Path $Solution)) {
    Write-Error "Solution not found: '$Solution'"
    exit 1
}

if (-not $LogDir) { $LogDir = Join-Path $OutDir "logs" }
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$logFile = Join-Path $LogDir "scan-$(Get-Date -Format 'yyyy-MM-dd_HHmmss').log"

function Write-Log {
    param([string]$Message)
    "$(Get-Date -Format 'HH:mm:ss')  $Message" | Tee-Object -FilePath $logFile -Append
}

function Invoke-CodeMap {
    param([string[]]$CliArgs)
    Write-Log "codemap $($CliArgs -join ' ')"
    # Piping native stderr through ForEach-Object {"$_"} before writing to file avoids Windows PowerShell 5.1's
    # NativeCommandError wrapping (CategoryInfo/FullyQualifiedErrorId boilerplate around every stderr line) -
    # scan-git failing on a non-git repo is an EXPECTED, non-fatal case here, and the log should read like plain
    # command output, not a PowerShell exception.
    & dotnet $CodeMapDll @CliArgs 2>&1 | ForEach-Object { "$_" } | Add-Content -Path $logFile
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { Write-Log "  -> FAILED (exit $exitCode) - see $logFile" }
    return $exitCode -eq 0
}

$repoDir = Split-Path -Parent $Solution
$indexDir = Join-Path $OutDir "index"

Write-Log "=== CodeMap nightly scan started ==="

# 'map' reads the staleness banner's git comparison from the CURRENT WORKING DIRECTORY (the same convention
# every query command uses - see StalenessBanner.cs) - so this whole run happens from inside the target repo.
Push-Location $repoDir
try {
    if (-not (Invoke-CodeMap @("scan", "--solution", $Solution, "--out", $OutDir))) {
        Write-Log "ABORTED: 'scan' failed, skipping remaining steps."
        exit 1
    }

    Invoke-CodeMap @("scan-git", "--repo", $repoDir, "--out", $OutDir) | Out-Null

    if ($FrontendRoot) {
        Invoke-CodeMap @("scan-fe", "--root", $FrontendRoot, "--out", $OutDir) | Out-Null
        Invoke-CodeMap @("link", "--index", $indexDir) | Out-Null
    }

    Invoke-CodeMap @("map", "--index", $indexDir, "--out", $OutDir) | Out-Null
}
finally {
    Pop-Location
}

Write-Log "=== CodeMap nightly scan finished ==="
