# Measures the tray app: startup (process start -> message loop idle), idle memory, CPU while the flyout is hidden and while it is
# visible but idle. The visible phase makes the app do its normal first read (ONE real Codex read, by design: the flyout opening is the
# trigger); it is skipped with -SkipVisible. Hidden-phase launches never touch Codex.
# The app is a tray-only process with no automation hook, so it is ended with Kill at the end (Windows drops the ghost icon on hover).
param(
  [string]$Exe = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\QuotaDuck.App\bin\Release\net10.0-windows\QuotaDuck.exe'),
  [int]$StartupRuns = 5,
  [int]$IdleSeconds = 10,
  [switch]$SkipVisible
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $repo '.tools\dotnet'          # framework-dependent apphost: use the repo-local runtime
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
if (-not (Test-Path $Exe)) { throw "not built: $Exe" }

function Stop-App { Get-Process QuotaDuck -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null } }
function Cpu-Percent($p, [int]$seconds) {
  $p.Refresh(); $t0 = $p.TotalProcessorTime; $w = [Diagnostics.Stopwatch]::StartNew()
  Start-Sleep -Seconds $seconds
  $p.Refresh(); $t1 = $p.TotalProcessorTime
  return [math]::Round(($t1 - $t0).TotalMilliseconds / $w.Elapsed.TotalMilliseconds / [Environment]::ProcessorCount * 100, 3)
}
function Mem($p) { $p.Refresh(); [pscustomobject]@{ WorkingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 1); PrivateMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1) } }

Stop-App
"== startup: process start -> message loop idle ($StartupRuns launches; the first is the cold one) =="
$times = @()
for ($i = 0; $i -lt $StartupRuns; $i++) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process $Exe -PassThru
  $null = $p.WaitForInputIdle(15000)
  $ms = $sw.Elapsed.TotalMilliseconds; $times += $ms
  "  run {0}: {1,6:N0} ms" -f ($i + 1), $ms
  Start-Sleep -Milliseconds 800
  Stop-App
  Start-Sleep -Milliseconds 300
}
$sorted = $times | Sort-Object
"  cold (first) = {0:N0} ms; median of all = {1:N0} ms; min = {2:N0} ms" -f $times[0], $sorted[[int]($sorted.Count / 2)], $sorted[0]

"== idle, flyout hidden =="
$p = Start-Process $Exe -PassThru
$null = $p.WaitForInputIdle(15000)
Start-Sleep -Seconds 4
$m = Mem $p
"  working set {0} MB, private bytes {1} MB (after 4 s)" -f $m.WorkingSetMB, $m.PrivateMB
"  threads: {0}, handles: {1}" -f $p.Threads.Count, $p.HandleCount
"  CPU over {0} s hidden: {1} % of one-machine CPU" -f $IdleSeconds, (Cpu-Percent $p $IdleSeconds)
$m = Mem $p
"  after the hidden window: working set {0} MB, private {1} MB" -f $m.WorkingSetMB, $m.PrivateMB

if (-not $SkipVisible) {
  "== flyout visible, idle (a second launch asks the running instance to open; this triggers the normal first Codex read) =="
  $null = Start-Process $Exe -PassThru
  Start-Sleep -Seconds 6                                    # the read takes ~1.5 s; let it finish and settle
  $p.Refresh()
  $m = Mem $p
  "  working set {0} MB, private bytes {1} MB (flyout open, after the first read)" -f $m.WorkingSetMB, $m.PrivateMB
  "  threads: {0}, handles: {1}" -f $p.Threads.Count, $p.HandleCount
  "  CPU over {0} s visible-idle (1 Hz countdown tick): {1} %" -f $IdleSeconds, (Cpu-Percent $p $IdleSeconds)
  "  codex.exe children of the app: " + @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id)").Count
}
Stop-App
"done"
