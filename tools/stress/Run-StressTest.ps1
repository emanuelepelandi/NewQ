<#
.SYNOPSIS
    Standard NewQ stress test: always the same scenario, so runs can be compared. Appends the results to
    PERFORMANCE.md in the project root.

.DESCRIPTION
    Scenario "go-overlap" (the default and, for now, the only one):
      - a generated workspace of Rounds x GosPerRound video cues, alternating the given videos, on the preview window;
      - for each round: GosPerRound GOs IntervalMs apart (videos pile up, one timeline cursor each), then Fade cue on
        the newest running cue, then Panic twice (immediate stop); in round 1 also Pause / Resume with everything playing;
      - 6 s idle at the end.
    Measured: GO command latency, real audio output after each GO, process memory / handles / threads / GDI / USER /
    CPU per round, UI-thread lag, GC, timeline build and render times (NEWQ_DEBUG log), what is left at the end.

    The test runs a separate NewQ instance (never touches the one in use), steals the focus for its duration,
    plays sound, and puts the user's settings back when it ends.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\stress\Run-StressTest.ps1 -Label "after link loops"
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\NewQ.App\bin\Debug\net7.0-windows\NewQ.exe'),
    [string[]]$Videos = @('D:\Download\BearGrylls-contro i castori infernali(PARODIA).mp4',
                          'D:\Download\COSTA RICA IN 4K 60fps HDR (ULTRA HD).mp4'),
    [int]$Rounds = 12,
    [int]$GosPerRound = 6,
    [int]$IntervalMs = 700,
    [string]$Label = '',
    [string]$Report = (Join-Path $PSScriptRoot '..\..\PERFORMANCE.md')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class StressNative {
  [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
}
"@

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Exe = (Resolve-Path $Exe).Path
$work = Join-Path $env:TEMP 'newq-stress'
New-Item -ItemType Directory -Force $work | Out-Null
$kit = Join-Path $PSScriptRoot 'StressKit'
$kitExe = Join-Path $kit 'bin\Release\net7.0-windows\StressKit.exe'
& dotnet build $kit -c Release -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'StressKit build failed' }

# ---- the workspace and the settings: always the same
$workspace = Join-Path $work 'stress.newq'
& $kitExe workspace $workspace ($Rounds * $GosPerRound) @Videos
$settingsPath = Join-Path $env:APPDATA 'NewQ\settings.json'
$settingsBackup = Join-Path $work 'settings.backup.json'
Copy-Item $settingsPath $settingsBackup -Force
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
foreach ($pair in @(@('TimelineVisible', $true), @('RunningPanelVisible', $true), @('InspectorCollapsed', $false), @('TimelineFollow', $true))) {
    $settings | Add-Member -NotePropertyName $pair[0] -NotePropertyValue $pair[1] -Force
}
$settings | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 $settingsPath

$debugLog = Join-Path $env:TEMP 'newq-debug.log'
Remove-Item $debugLog -ErrorAction SilentlyContinue
$env:NEWQ_DEBUG = '1'
$proc = $null
$monitor = $null
try {
    $proc = Start-Process $Exe -ArgumentList "`"$workspace`"" -PassThru
    for ($i = 0; $i -lt 60 -and $proc.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $proc.Refresh() }
    Start-Sleep 8   # libVLC, previews and video lengths

    $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    function Windows { [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $pc) }
    $main = Windows | Where-Object { $_.Current.Name -notlike '*uscita*' } | Select-Object -First 1
    function Find($name) { $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name))) }
    function Press($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    function HidePreview { foreach ($w in (Windows | Where-Object { $_.Current.Name -like '*uscita*' })) { try { $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized) } catch {} } }
    function Sample {
        $proc.Refresh()
        [pscustomobject]@{
            WorkingMB = [math]::Round($proc.WorkingSet64 / 1MB)
            PrivateMB = [math]::Round($proc.PrivateMemorySize64 / 1MB)
            Handles = $proc.HandleCount
            Threads = $proc.Threads.Count
            Gdi = [StressNative]::GetGuiResources($proc.Handle, 0)
            User = [StressNative]::GetGuiResources($proc.Handle, 1)
            CpuMs = $proc.TotalProcessorTime.TotalMilliseconds
            At = [DateTime]::UtcNow
        }
    }

    $go = Find 'GO'; $panic = Find 'Panic'; $pause = Find 'Pausa'; $fade = Find 'Fade cue'
    Press (Find 'Adatta')
    $totalSeconds = 30 + $Rounds * ($GosPerRound * $IntervalMs / 1000 + 4)
    $levels = Join-Path $work 'levels.txt'
    $monitor = Start-Process $kitExe -ArgumentList "level $($proc.Id) $([int]$totalSeconds) 100" -RedirectStandardOutput $levels -PassThru -WindowStyle Hidden

    $start = Sample
    $samples = @($start)
    $latencies = New-Object System.Collections.Generic.List[double]
    $goTimes = New-Object System.Collections.Generic.List[datetime]
    for ($round = 0; $round -lt $Rounds; $round++) {
        for ($k = 0; $k -lt $GosPerRound; $k++) {
            $sw = [Diagnostics.Stopwatch]::StartNew(); Press $go; $latencies.Add($sw.Elapsed.TotalMilliseconds)
            $goTimes.Add([DateTime]::UtcNow)
            if ($round -eq 0 -and $k -eq 0) { Start-Sleep -Milliseconds 300; HidePreview }
            Start-Sleep -Milliseconds $IntervalMs
        }
        if ($round -eq 0) { Press $pause; Start-Sleep 1; Press $pause; Start-Sleep -Milliseconds 300 }
        Press $fade; Start-Sleep 1
        Press $panic; Start-Sleep -Milliseconds 150; Press $panic; Start-Sleep 1
        $samples += Sample
    }
    Start-Sleep 6
    $end = Sample
    $alive = -not $proc.HasExited
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    if ($monitor -and -not $monitor.HasExited) { Stop-Process -Id $monitor.Id -Force }
    $env:NEWQ_DEBUG = $null
    Copy-Item $settingsBackup $settingsPath -Force
}

# ---- results
function Stats($values) {
    $v = @($values | Sort-Object)
    if ($v.Count -eq 0) { return [pscustomobject]@{ Count = 0; Avg = 0; P95 = 0; Max = 0; Min = 0 } }
    [pscustomobject]@{
        Count = $v.Count; Min = $v[0]; Max = $v[-1]
        Avg = ($v | Measure-Object -Average).Average
        P95 = $v[[math]::Min($v.Count - 1, [int][math]::Floor(0.95 * $v.Count))]
    }
}
$inv = [Globalization.CultureInfo]::InvariantCulture
$levelRows = @(Get-Content $levels | Where-Object { $_ -match '^\d+ ' } | ForEach-Object { $p = $_ -split ' '; [pscustomobject]@{ At = [datetime]::new([long]$p[0], 'Utc'); Peak = [double]::Parse($p[1], $inv) } })
$goLevels = foreach ($t in $goTimes) {
    $from = $t.AddMilliseconds(300); $to = $t.AddMilliseconds($IntervalMs)
    ($levelRows | Where-Object { $_.At -gt $from -and $_.At -le $to } | Measure-Object Peak -Maximum).Maximum
}
$silent = @($goLevels | Where-Object { $_ -lt 0.01 }).Count
$level = Stats $goLevels
$lat = Stats $latencies

$log = if (Test-Path $debugLog) { Get-Content $debugLog } else { @() }
function Num($line, $pattern) { if ($line -match $pattern) { [double]::Parse($Matches[1].Replace(',', '.'), $inv) } }
$renders = Stats ($log | ForEach-Object { Num $_ 'timeline render: ([\d.,]+) ms' } | Where-Object { $_ -ne $null })
$builds = Stats ($log | ForEach-Object { Num $_ 'blocks built and shown in ([\d.,]+) ms' } | Where-Object { $_ -ne $null })
$states = @($log | Where-Object { $_ -match 'state:' })
$lagP99 = Stats ($states | ForEach-Object { Num $_ 'p99=([\d.,]+)' })
$lagMax = Stats ($states | ForEach-Object { Num $_ 'max=([\d.,]+)' })
$last = if ($states.Count) { $states[-1] } else { '' }
$gc2 = Num $last 'gc2=(\d+)'; $gc0 = Num $last 'gc0=(\d+)'; $managed = Num $last 'managedMB=(\d+)'
$vlcLeft = Num $last 'vlcPlayers=(\d+)'; $runningLeft = Num $last 'running=(\d+)'

$cpu = @()
for ($i = 1; $i -lt $samples.Count; $i++) {
    $span = ($samples[$i].At - $samples[$i - 1].At).TotalMilliseconds
    $cpu += 100 * ($samples[$i].CpuMs - $samples[$i - 1].CpuMs) / $span / [Environment]::ProcessorCount
}
$cpuStats = Stats $cpu
$peakWorking = ($samples | Measure-Object WorkingMB -Maximum).Maximum

$commit = (& git -C $root rev-parse --short HEAD) + $(if (& git -C $root status --porcelain --untracked-files=no) { '+modifiche' } else { '' })
$machine = '{0} | {1} | {2} GB | {3}' -f (Get-CimInstance Win32_Processor | Select-Object -First 1).Name,
    ((Get-CimInstance Win32_VideoController | ForEach-Object Name) -join ', '),
    [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB),
    ((Get-CimInstance Win32_OperatingSystem).Caption + ' ' + (Get-CimInstance Win32_OperatingSystem).BuildNumber)
function F($x, $fmt = '0.0') { if ($null -eq $x) { 'n/d' } else { ([double]$x).ToString($fmt, $inv) } }

$section = @"

## $((Get-Date).ToString('yyyy-MM-dd HH:mm')) - go-overlap$(if ($Label) { " - $Label" })

- Commit: ``$commit`` (build: ``$(if ($Exe -match '\\(Debug|Release)\\') { $Matches[1] } else { [IO.Path]::GetDirectoryName($Exe) })``) - Macchina: $machine
- Parametri: $Rounds giri x $GosPerRound GO, $IntervalMs ms tra i GO, $($Rounds * $GosPerRound) video ($([string]::Join(', ', ($Videos | ForEach-Object { [IO.Path]::GetFileName($_) })))), anteprima in finestra
- Esito: processo vivo $alive, player libVLC rimasti $(F $vlcLeft '0'), cue in esecuzione rimaste $(F $runningLeft '0')

| Misura | Valore |
|---|---|
| Comando GO (UIA), ms | media $(F $lat.Avg) - p95 $(F $lat.P95) - max $(F $lat.Max) ($($lat.Count) GO) |
| Audio dopo ogni GO (picco) | min $(F $level.Min '0.00') - media $(F $level.Avg '0.00') - GO muti $silent / $($goTimes.Count) |
| Ritardo thread UI, ms (finestre di 5 s) | p99 peggiore $(F $lagP99.Max) - p99 medio $(F $lagP99.Avg) - max assoluto $(F $lagMax.Max) |
| Rendering timeline, ms | media $(F $renders.Avg) - max $(F $renders.Max) ($($renders.Count) campioni) |
| Ricostruzione timeline, ms | media $(F $builds.Avg) - max $(F $builds.Max) ($($builds.Count)) |
| CPU per giro, % (tutti i core) | media $(F $cpuStats.Avg) - max $(F $cpuStats.Max) |
| Working set, MB | inizio $($start.WorkingMB) - picco $peakWorking - fine $($end.WorkingMB) |
| Private bytes, MB | inizio $($start.PrivateMB) - fine $($end.PrivateMB) |
| Memoria gestita, MB (fine) | $(F $managed '0') |
| GC gen0 / gen2 (totale) | $(F $gc0 '0') / $(F $gc2 '0') |
| Handle | inizio $($start.Handles) - fine $($end.Handles) |
| Thread | inizio $($start.Threads) - fine $($end.Threads) |
| Oggetti GDI / USER | inizio $($start.Gdi) / $($start.User) - fine $($end.Gdi) / $($end.User) |
| Working set a fine giro, MB | $(($samples | Select-Object -Skip 1 | ForEach-Object WorkingMB) -join ' ') |
"@

if (-not (Test-Path $Report)) {
    Set-Content -Encoding UTF8 $Report "# NewQ - misure di prestazioni`r`n`r`nScritte da ``tools/stress/Run-StressTest.ps1``."
}
Add-Content -Encoding UTF8 $Report $section
$section
