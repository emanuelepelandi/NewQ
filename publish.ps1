# Builds a self-contained Windows x64 release of NewQ (no .NET installation needed on the target PC).
# Output: .\dist\NewQ\NewQ.exe  (+ a zip ready to copy to the show machine)
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "dist\NewQ"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet test (Join-Path $root "tests\NewQ.Core.Tests") -c $Configuration -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw "Test falliti: pubblicazione annullata." }

dotnet publish (Join-Path $root "src\NewQ.App\NewQ.App.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishReadyToRun=true -p:UseSharedCompilation=false `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "Pubblicazione fallita." }

Copy-Item (Join-Path $root "samples") (Join-Path $out "samples") -Recurse

$zip = Join-Path $root "dist\NewQ-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Pronto: $out\NewQ.exe"
Write-Host "Archivio: $zip"
