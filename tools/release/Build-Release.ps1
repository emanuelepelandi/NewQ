<#
.SYNOPSIS
    Builds the NewQ installer and update packages (Velopack) and, with -Publish, uploads them as a GitHub release.

.DESCRIPTION
    1. Gets the Velopack packager (vpk) from nuget.org the first time (tools\release\.vpk, not in git).
    2. Runs the tests.
    3. Publishes NewQ self-contained for Windows x64 (no .NET needed on the show PC), with the given version.
    4. Downloads the previous release from GitHub (if any), so the update can be a small "delta".
    5. Packs: artifacts\releases\NewQ-win-Setup.exe (installer), NewQ-win-Portable.zip, update packages.
    6. With -Publish: creates the GitHub release vX.Y.Z with those files. Installed copies of NewQ see it at
       their next start and ask whether to install it.

    Needs a .NET runtime 8 or newer installed (for vpk; NewQ itself still builds with the .NET 7 SDK).
    For -Publish: a GitHub token with "Contents: read and write" on the repository, in -Token or in the
    GITHUB_TOKEN environment variable.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\release\Build-Release.ps1 -Version 0.2.0
.EXAMPLE
    $env:GITHUB_TOKEN = "github_pat_..."; powershell -ExecutionPolicy Bypass -File tools\release\Build-Release.ps1 -Version 0.2.0 -Notes notes.md -Publish
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [switch]$Publish,
    [string]$Token = $env:GITHUB_TOKEN,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$VpkVersion = '1.2.158'   # keep equal to the Velopack package in src\NewQ.App\NewQ.App.csproj
$RepoUrl = 'https://github.com/emanuelepelandi/NewQ'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versione non valida: '$Version' (es. 0.2.0)" }
if ($Publish -and -not $Token) { throw 'Per -Publish serve un token GitHub (-Token o variabile GITHUB_TOKEN).' }
if ($Notes -and -not (Test-Path $Notes)) { throw "File delle note non trovato: $Notes" }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$publishDir = Join-Path $root 'artifacts\publish'
$releaseDir = Join-Path $root 'artifacts\releases'
function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Comando fallito ($LASTEXITCODE): $exe $($arguments -join ' ')" }
}

# ---- 1. vpk
Step "Velopack vpk $VpkVersion"
$runtimes = & dotnet --list-runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App (\d+)\.' } | ForEach-Object { [int]$Matches[1] }
$major = $runtimes | Where-Object { $_ -ge 8 } | Sort-Object | Select-Object -First 1
if (-not $major) { throw 'Serve il runtime .NET 8 o successivo per vpk: https://dotnet.microsoft.com/download' }
$tfm = "net$major.0"
$vpkRoot = Join-Path $PSScriptRoot ".vpk\$VpkVersion"
if (-not (Test-Path (Join-Path $vpkRoot 'tools'))) {
    New-Item -ItemType Directory -Force $vpkRoot | Out-Null
    $package = Join-Path $vpkRoot 'vpk.zip'
    Invoke-WebRequest -UseBasicParsing "https://api.nuget.org/v3-flatcontainer/vpk/$VpkVersion/vpk.$VpkVersion.nupkg" -OutFile $package
    Expand-Archive $package -DestinationPath $vpkRoot -Force
    Remove-Item $package
}
$vpk = Join-Path $vpkRoot "tools\$tfm\any\vpk.dll"
if (-not (Test-Path $vpk)) { $vpk = (Get-ChildItem (Join-Path $vpkRoot 'tools') -Recurse -Filter vpk.dll | Select-Object -First 1).FullName }
function Vpk([string[]]$arguments) { Run 'dotnet' (@($vpk) + $arguments) }

# ---- 2. tests
if (-not $SkipTests) {
    Step 'Test'
    Run 'dotnet' @('test', (Join-Path $root 'tests\NewQ.Core.Tests'), '-c', 'Release', '-v', 'q', '-p:UseSharedCompilation=false')
}

# ---- 3. publish
Step "Pubblicazione NewQ $Version (self-contained win-x64)"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Run 'dotnet' @('publish', (Join-Path $root 'src\NewQ.App\NewQ.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    "-p:Version=$Version", '-p:PublishReadyToRun=true', '-p:UseSharedCompilation=false', '-o', $publishDir, '-v', 'q')
Copy-Item (Join-Path $root 'samples') (Join-Path $publishDir 'samples') -Recurse

# ---- 4. previous release (for the delta update)
Step 'Release precedente da GitHub'
New-Item -ItemType Directory -Force $releaseDir | Out-Null
$download = @('download', 'github', '--repoUrl', $RepoUrl, '--outputDir', $releaseDir)
if ($Token) { $download += @('--token', $Token) }
try { Vpk $download } catch { Write-Host "Nessuna release precedente scaricata ($($_.Exception.Message)): solo pacchetto completo." }

# ---- 5. pack
Step 'Installer e pacchetti di aggiornamento'
$pack = @('pack', '--packId', 'NewQ', '--packVersion', $Version, '--packDir', $publishDir, '--mainExe', 'NewQ.exe', '--runtime', 'win-x64',
    '--packTitle', 'NewQ', '--packAuthors', 'Emanuele Pelandi', '--icon', (Join-Path $root 'src\NewQ.App\Assets\NewQ.ico'),
    '--outputDir', $releaseDir)
if ($Notes) { $pack += @('--releaseNotes', (Resolve-Path $Notes).Path) }
Vpk $pack

# ---- 6. publish on GitHub
if ($Publish) {
    Step "Release v$Version su GitHub"
    Vpk @('upload', 'github', '--repoUrl', $RepoUrl, '--token', $Token, '--outputDir', $releaseDir,
        '--publish', '--releaseName', "NewQ $Version", '--tag', "v$Version")
}

Step 'Fatto'
Get-ChildItem $releaseDir -File | Where-Object { $_.Name -like "*$Version*" -or $_.Name -like 'NewQ-win-*' -or $_.Name -like 'releases.*' } |
    ForEach-Object { '{0,-40} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
Write-Host "Installer: $(Join-Path $releaseDir 'NewQ-win-Setup.exe')"
if (-not $Publish) { Write-Host 'Non pubblicata: aggiungi -Publish per caricarla su GitHub.' }
