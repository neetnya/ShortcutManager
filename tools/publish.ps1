# Build the portable package into dist\ShortcutManager\.
# Targets the installed .NET 9 desktop runtime (small output, fast startup).
param(
    [string]$Configuration = 'Release',
    [string]$OutDir = '.\dist\ShortcutManager'
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$proj = Join-Path $PSScriptRoot '..\src\ShortcutManager.csproj'
$proj = (Resolve-Path $proj).Path

Write-Host "publishing $proj ($Configuration) -> $OutDir"

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }

dotnet publish $proj -c $Configuration -o $OutDir --nologo -v minimal /p:DebugType=none /p:DebugSymbols=false /p:SatelliteResourceLanguages=zh-Hans

if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

# The portable config.json is created by the app at first run.
# Only remove development-time logs here.
foreach ($junk in 'logic-test.log', 'selftest.log', 'diag.log', 'shot.log', 'config.json.tmp') {
    $f = Join-Path $OutDir $junk
    if (Test-Path $f) { Remove-Item $f -Force }
}

$exe = Join-Path $OutDir 'ShortcutManager.exe'
$files = Get-ChildItem $OutDir -Recurse -File
$size = ($files | Measure-Object -Property Length -Sum).Sum

Write-Host ''
Write-Host ("exe  : {0}" -f $exe)
Write-Host ("files: {0}" -f $files.Count)
Write-Host ("size : {0:N0} bytes ({1:N2} MB)" -f $size, ($size / 1MB))
Write-Host ''
$files | Select-Object @{n='Size';e={$_.Length}}, Name | Format-Table -AutoSize | Out-String | Write-Host
