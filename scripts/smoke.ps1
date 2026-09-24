<#
.SYNOPSIS
  End-to-end smoke test: builds the solution, runs the console batch over the sample reports through the named pipe,
  validates every output against the generated JSON Schema and prints a summary.

.PARAMETER SampleDir
  Directory with .rpt files (default: ..\samples).
.PARAMETER Configuration
  Build configuration (default: Release).
.PARAMETER Workers
  Extractor processes to run in parallel (default: 2).
#>
[CmdletBinding()]
param(
    [string]$SampleDir = "",
    [string]$Configuration = "Release",
    [int]$Workers = 2,
    [switch]$SkipBuild
)

# Native executables write logs to stderr; Windows PowerShell 5.1 would turn that into a terminating error under "Stop",
# so errors are checked explicitly through exit codes instead.
$ErrorActionPreference = "Continue"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $scriptDir "..")
if ([string]::IsNullOrEmpty($SampleDir)) { $SampleDir = Join-Path $root "samples" }
$outDir = Join-Path $root "out\smoke"
$console = Join-Path $root "src\CrystalMetadata.Console\bin\$Configuration\net10.0\rpt2frx.dll"
$server = Join-Path $root "src\CrystalMetadata.PipeServer\bin\$Configuration\net48\CrystalMetadata.PipeServer.exe"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Fail($message) { Write-Host "SMOKE FAILED: $message" -ForegroundColor Red; exit 1 }

if (-not $SkipBuild) {
    Write-Host "== Building CrystalMetadata.sln ($Configuration)" -ForegroundColor Cyan
    & dotnet build (Join-Path $root "CrystalMetadata.sln") -c $Configuration -nologo -v q 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail "build failed" }
}

if (-not (Test-Path $server)) { Fail "server not found: $server" }
if (-not (Test-Path $console)) { Fail "console not found: $console" }

Write-Host "== Crystal runtime probe" -ForegroundColor Cyan
$probe = Start-Process -FilePath $server -ArgumentList @("--extract", "`"$(Join-Path $root 'nonexistent.rpt')`"") -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput (Join-Path $outDir "probe.out.txt") -RedirectStandardError (Join-Path $outDir "probe.err.txt")
if ($probe.ExitCode -eq 3) { Fail "SAP Crystal Reports runtime (x64) is not installed on this machine." }
Write-Host ("   runtime ok (probe exit code {0} = file-not-found as expected)" -f $probe.ExitCode)

Write-Host "== JSON Schema" -ForegroundColor Cyan
$schemaPath = Join-Path $outDir "metadata.schema.json"
& dotnet $console schema --out $schemaPath 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { Fail "schema generation failed" }

Write-Host "== Batch over $SampleDir with $Workers workers" -ForegroundColor Cyan
$manifestPath = Join-Path $outDir "_manifest.json"
if (Test-Path $manifestPath) { Remove-Item $manifestPath }
& dotnet $console batch --in $SampleDir --out $outDir --workers $Workers --hints --server-exe $server --force 2>&1 | Out-Host
$batchExit = $LASTEXITCODE
if (-not (Test-Path $manifestPath)) { Fail "no manifest written (exit code $batchExit)" }

Write-Host "== Validating outputs" -ForegroundColor Cyan
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$schemaJson = Get-Content $schemaPath -Raw
$canTestJson = $null -ne (Get-Command Test-Json -ErrorAction SilentlyContinue)
$bad = 0
foreach ($e in $manifest.entries) {
    if (-not $e.success) { Write-Warning ("FAILED {0}: {1} {2}" -f $e.relativePath, $e.errorCode, $e.errorMessage); $bad++; continue }
    $json = Get-Content $e.outputFile -Raw
    if ($canTestJson) {
        # PowerShell 7 only; Windows PowerShell 5.1 falls back to a parse check
        $ok = Test-Json -Json $json -Schema $schemaJson -ErrorAction SilentlyContinue
        if (-not $ok) { Write-Warning ("schema violation: {0}" -f $e.outputFile); $bad++ }
    }
    $m = $json | ConvertFrom-Json
    if ($null -eq $m.sections) { Write-Warning ("unparseable output: {0}" -f $e.outputFile); $bad++; continue }
    "{0,-50} sections={1,3} objects={2,4} tables={3,2} params={4,2} formulas={5,2} subreports={6} warnings={7}" -f $e.relativePath, $m.stats.sectionCount, $m.stats.objectCount, $m.stats.tableCount, $m.stats.parameterCount, $m.stats.formulaCount, $m.stats.subreportCount, $m.stats.warningCount
}

Write-Host ("== {0} ok, {1} failed, {2} invalid (batch exit code {3})" -f $manifest.succeeded, $manifest.failed, $bad, $batchExit) -ForegroundColor Cyan
if ($batchExit -ne 0 -or $bad -ne 0) { exit 1 }
exit 0
