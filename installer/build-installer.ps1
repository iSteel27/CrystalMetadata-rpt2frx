<#
.SYNOPSIS
  Builds the single-file installer: publishes the app, adds the extractor, verifies the result, compiles the Inno Setup script.

.PARAMETER Version
  Version shown in the installer (default 0.1.0).
.PARAMETER SkipCompile
  Only assemble and verify installer\staging (useful when Inno Setup is not installed).

.NOTES
  Needs the .NET 10 SDK. Compiling needs Inno Setup 6:  winget install JRSoftware.InnoSetup
  Output: installer\output\CrystalMetadataSetup-<Version>.exe
#>
[CmdletBinding()]
param(
    [string]$Version = "0.1.0-beta.1",
    [switch]$SkipCompile
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $scriptDir "..")
$staging = Join-Path $scriptDir "staging"
$app = Join-Path $staging "app"
$server = Join-Path $app "server"

function Step($text) { Write-Host "== $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "BUILD FAILED: $text" -ForegroundColor Red; exit 1 }

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $app, $server | Out-Null

Step "1/4 Publishing the app (self-contained: users do not need to install .NET)"
& dotnet publish (Join-Path $root "src\CrystalMetadata.Console\CrystalMetadata.Console.csproj") -c Release -r win-x64 --self-contained true -o $app -nologo -v q -p:Version=$Version
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed" }

Step "2/4 Building the extractor (.NET Framework 4.8, 64-bit) and copying it into 'server'"
& dotnet build (Join-Path $root "src\CrystalMetadata.PipeServer\CrystalMetadata.PipeServer.csproj") -c Release -nologo -v q -p:Version=$Version
if ($LASTEXITCODE -ne 0) { Fail "building the extractor failed" }
$serverOut = Join-Path $root "src\CrystalMetadata.PipeServer\bin\Release\net48"
Copy-Item (Join-Path $serverOut "*") $server -Recurse -Force -Exclude "*.pdb", "*.xml", "logs"

Step "3/4 Verifying the package"
$required = @(
    (Join-Path $app "rpt2frx.exe"),
    (Join-Path $app "appsettings.json"),
    (Join-Path $server "CrystalMetadata.PipeServer.exe"),
    (Join-Path $server "CrystalMetadata.PipeServer.exe.config"),   # without it the extractor crashes at start-up
    (Join-Path $server "appsettings.json")
)
foreach ($f in $required) { if (-not (Test-Path $f)) { Fail "missing $f" } }
if (Get-ChildItem $server -Filter "CrystalDecisions*" -ErrorAction SilentlyContinue) { Fail "SAP assemblies must never be packaged" }

$size = [math]::Round(((Get-ChildItem $staging -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host ("   staging folder: {0} MB, {1} files" -f $size, (Get-ChildItem $staging -Recurse -File).Count)

# run the packaged program itself: the setup check must start the packaged extractor, not the dev one
Write-Host "   running the packaged program's own setup check..."
& (Join-Path $app "rpt2frx.exe") --version
& (Join-Path $app "rpt2frx.exe") doctor
Write-Host ("   (setup check exit code {0}; 1 only means something on THIS PC is missing, e.g. the SAP runtime)" -f $LASTEXITCODE)

if ($SkipCompile) { Write-Host "Skipping installer compilation (-SkipCompile)." -ForegroundColor Yellow; exit 0 }

Step "4/4 Compiling the installer with Inno Setup"
$iscc = @(
    "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Host "Inno Setup 6 is not installed, so the .exe installer was not compiled." -ForegroundColor Yellow
    Write-Host "Install it (free):  winget install JRSoftware.InnoSetup   then run this script again."
    exit 2
}

& $iscc "/DAppVersion=$Version" (Join-Path $scriptDir "CrystalMetadata.iss")
if ($LASTEXITCODE -ne 0) { Fail "Inno Setup reported an error" }
$out = Get-ChildItem (Join-Path $scriptDir "output") -Filter "rpt2frxSetup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ("DONE: {0} ({1} MB)" -f $out.FullName, [math]::Round($out.Length / 1MB, 1)) -ForegroundColor Green
Write-Host "Reminder: sign the installer with a code-signing certificate before giving it to users, or Windows will warn 'unknown publisher'."
