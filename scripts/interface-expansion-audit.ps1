<#
.SYNOPSIS
    Measures the false-positive rate of via:"interface" edges in edges.jsonl against di.json.

.DESCRIPTION
    Review Fix Pass v1, Task 2. This is a MEASUREMENT-ONLY script — it does not change any scan/renderer
    logic. When a call through an interface is expanded (interface-expand pass) to every known
    implementation, only 1 (or a few) of those edges is the actual DI-bound path at runtime; the rest are
    over-inference.

    Method: for each sampled via:interface edge, find its "sibling group" (every edge sharing the same
    from/file/line — the same original call site, expanded into N edges, one per implementation).
    Cross-check the group's set of containing types against the implementations di.json records as
    actually DI-bound:
      - match   : this edge points at the implementation di.json says is bound
      - excess  : this edge points at a DIFFERENT implementation, while the interface DOES appear in
                  di.json (i.e. at least one correct edge exists in the group — this is genuine
                  over-inference)
      - unknown : the interface isn't in di.json at all (e.g. assembly-scanning/Scrutor-style
                  registration, or never registered) — not enough data to judge, EXCLUDED from the false
                  positive rate.

.PARAMETER IndexDir
    Index directory (containing edges.jsonl, di.json, symbols.jsonl) — e.g. <out>/index after `codemap scan`.
.PARAMETER SampleSize
    Number of via:interface edges to sample (default 20).
.PARAMETER Seed
    Random seed, so results are reproducible across runs (default 42).

.NOTES
    KNOWN LIMITATION (found running this against eShopOnWeb, see docs/BENCHMARK-INTERFACE-EXPANSION.md):
    di.json is NOT a clean "confirmed DI binding" source. SemanticScanner.BuildDiJson merges THREE sources
    into the same interface -> [implementations] map: (1) real fluent AddScoped/AddSingleton/AddTransient
    calls, (2) the [Injectable]-attribute convention, and (3) a "structural" fallback = EVERY type that
    implements the interface, found the exact same way (FindImplementationForInterfaceMember) that drives
    the interface-expand edge pass itself. Because (3) is populated from essentially the same semantic fact
    as the edges being audited, a "match" verdict below is a NECESSARY but not SUFFICIENT signal — it only
    proves the type implements the interface (which was never in question), not that it's the one DI
    actually resolves. Treat "match" here as "plausible, not excess by the structural fallback" rather than
    "confirmed correct". The real false-positive rate in docs/BENCHMARK-INTERFACE-EXPANSION.md was obtained
    by manually reading the actual AddScoped/Decorate call sites in source, not from this script alone.

.EXAMPLE
    powershell -File scripts/interface-expansion-audit.ps1 -IndexDir /tmp/bench-index/index
#>
param(
    [Parameter(Mandatory = $true)] [string]$IndexDir,
    [int]$SampleSize = 20,
    [int]$Seed = 42
)

$edgesPath = Join-Path $IndexDir "edges.jsonl"
$diPath = Join-Path $IndexDir "di.json"
$symbolsPath = Join-Path $IndexDir "symbols.jsonl"

foreach ($p in @($edgesPath, $diPath, $symbolsPath)) {
    if (-not (Test-Path $p)) { Write-Error "Not found: $p (run 'codemap scan' first)"; exit 1 }
}

$edges = Get-Content $edgesPath | Where-Object { $_.Trim() -ne "" } | ForEach-Object { $_ | ConvertFrom-Json }
$di = Get-Content $diPath -Raw | ConvertFrom-Json
$symbols = Get-Content $symbolsPath | Where-Object { $_.Trim() -ne "" } | ForEach-Object { $_ | ConvertFrom-Json }

# symbolId -> containingType, so a via:interface edge's `to` can be mapped to the TYPE it belongs to.
$containingTypeById = @{}
foreach ($s in $symbols) { $containingTypeById[$s.id] = $s.containingType }

# Flatten every implementation di.json records as DI-bound (across all interfaces) into one lookup set.
# di.json: { "T:Ns.IFoo": ["T:Ns.Foo"], ... } - values are TYPE docIds ("T:" prefix); symbols.jsonl's
# containingType field is plain dotted text with NO prefix (e.g. "Ns.Foo") - strip "T:" so the two are
# comparable. Missing this the first time made every single edge come back "unknown" (verified by hand).
$allBoundImpls = New-Object System.Collections.Generic.HashSet[string]
foreach ($prop in $di.PSObject.Properties) {
    foreach ($impl in @($prop.Value)) {
        $stripped = if ($impl -match '^[A-Z]:(.+)$') { $Matches[1] } else { $impl }
        [void]$allBoundImpls.Add($stripped)
    }
}

$interfaceEdges = @($edges | Where-Object { $_.kind -eq "call" -and $_.via -eq "interface" })
if ($interfaceEdges.Count -eq 0) {
    Write-Host "No via:interface edges found in $edgesPath."
    exit 0
}

# Group ALL via:interface edges by call site (from, file, line) - used to look up the "sibling group" of
# any sampled edge, regardless of whether every sibling itself got sampled.
$groupKey = { param($e) "$($e.from)|$($e.file)|$($e.line)" }
$groupsBySite = @{}
foreach ($e in $interfaceEdges) {
    $key = & $groupKey $e
    if (-not $groupsBySite.ContainsKey($key)) { $groupsBySite[$key] = New-Object System.Collections.Generic.List[object] }
    $groupsBySite[$key].Add($e)
}

$rng = New-Object System.Random($Seed)
$sampleCount = [Math]::Min($SampleSize, $interfaceEdges.Count)
$sampledIndexes = (0..($interfaceEdges.Count - 1) | Sort-Object { $rng.Next() } | Select-Object -First $sampleCount)
$sampledEdges = $sampledIndexes | ForEach-Object { $interfaceEdges[$_] }

$results = @()
foreach ($edge in $sampledEdges) {
    $key = & $groupKey $edge
    $siblingGroup = $groupsBySite[$key]
    $candidateTypes = @($siblingGroup | ForEach-Object { $containingTypeById[$_.to] } | Sort-Object -Unique)
    $boundInGroup = @($candidateTypes | Where-Object { $allBoundImpls.Contains($_) })
    $implType = $containingTypeById[$edge.to]

    $verdict =
        if ($boundInGroup.Count -eq 0) { "unknown" }
        elseif ($boundInGroup -contains $implType) { "match" }
        else { "excess" }

    $results += [PSCustomObject]@{
        From           = $edge.from
        To             = $edge.to
        ImplType       = $implType
        File           = $edge.file
        Line           = $edge.line
        CandidateCount = $candidateTypes.Count
        Verdict        = $verdict
    }
}

$results | Format-Table -Property From, To, CandidateCount, Verdict -AutoSize | Out-String -Width 400 | Write-Host

$matchCount = @($results | Where-Object { $_.Verdict -eq "match" }).Count
$excessCount = @($results | Where-Object { $_.Verdict -eq "excess" }).Count
$unknownCount = @($results | Where-Object { $_.Verdict -eq "unknown" }).Count
$total = $results.Count
$knownTotal = $matchCount + $excessCount

Write-Host ""
Write-Host "=== SUMMARY ==="
Write-Host "Total via:interface edges in index: $($interfaceEdges.Count)"
Write-Host "Sampled: $total edge(s)"
Write-Host "  match (real DI binding):    $matchCount"
Write-Host "  excess (over-inference):    $excessCount"
Write-Host "  unknown (interface not in di.json - not enough data to judge): $unknownCount"
if ($knownTotal -gt 0) {
    $fpRate = [Math]::Round(100 * $excessCount / $knownTotal, 1)
    Write-Host "False positive rate = excess / (match + excess), excluding unknown = $fpRate%"
} else {
    Write-Host "False positive rate: not computable (no sampled edge had known DI binding data)"
}
Write-Host ""
Write-Host "CAVEAT: di.json mixes real DI registrations with a structural 'implements' fallback (see .NOTES"
Write-Host "in this script's help, or docs/BENCHMARK-INTERFACE-EXPANSION.md). A low false-positive rate here"
Write-Host "can mean the mechanism is genuinely fine, OR that di.json's structural fallback is masking real"
Write-Host "excess edges (e.g. decorator pattern: interface bound to Decorator, but the wrapped concrete type"
Write-Host "still shows as structurally 'bound' too). Do not trust a 0% result from this script alone."
