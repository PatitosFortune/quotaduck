# Publishes the Claude bridge as Native AOT and verifies it.
#   PRODUCTION exe (src/QuotaDuck.Bridge)  : single-file + size gate, binary scan proving it has no path-override/test hook strings,
#                                            and the production-safe process tests (state path is fixed; only no-write inputs are used).
#   TEST-SEAM exe  (tests/QuotaDuck.Bridge.TestSeam, same sources minus Program.cs): the full end-to-end process tests and the timings,
#                                            with state redirected to a temp directory. The real QuotaDuck state is NEVER touched.
# Exit code 1 if any gate fails (gates are parameters; they are reported, never silently relaxed).
param(
  [double]$MaxSizeMB = 2.0,
  [double]$MaxMedianMs = 40,
  [int]$Runs = 40,
  [switch]$SkipTimings
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
$dn = Join-Path $PSScriptRoot 'dotnet.ps1'
$failed = $false

function Publish([string]$project, [string]$out) {
  if (Test-Path $out) { [IO.Directory]::Delete($out, $true) }
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $dn publish $project -c Release -r win-x64 -o $out --nologo -v q 2>&1 | Where-Object { $_ -match 'error|warn' }
  if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}
$prodOut = Join-Path $repo 'artifacts\bridge-aot'
$seamOut = Join-Path $repo 'artifacts\bridge-aot-testseam'
Publish 'src\QuotaDuck.Bridge\QuotaDuck.Bridge.csproj' $prodOut
Publish 'tests\QuotaDuck.Bridge.TestSeam\QuotaDuck.Bridge.TestSeam.csproj' $seamOut
$prod = Join-Path $prodOut 'quotaduck-statusline.exe'
$seam = Join-Path $seamOut 'quotaduck-statusline-testseam.exe'

"== 1. production executable =="
$files = @(Get-ChildItem $prodOut -File)
$files | ForEach-Object { "  {0}  {1:N0} bytes ({2:N3} MB)" -f $_.Name, $_.Length, ($_.Length / 1MB) }
if ($files.Count -ne 1 -or -not (Test-Path $prod)) { "  FAIL: expected exactly one file, quotaduck-statusline.exe"; $failed = $true } else { "  single executable: yes" }
$sizeMB = (Get-Item $prod).Length / 1MB
if ($sizeMB -le $MaxSizeMB) { "  size gate  <= $MaxSizeMB MB : PASS ({0:N2} MB)" -f $sizeMB } else { "  size gate  <= $MaxSizeMB MB : FAIL ({0:N2} MB)" -f $sizeMB; $failed = $true }
"  signed: {0} (unsigned is expected for a local build)" -f (Get-AuthenticodeSignature $prod).Status

"== 2. production binary contains no path-override / test-hook strings =="
$bytes = [IO.File]::ReadAllBytes($prod)
$latin = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
foreach ($s in 'QD_TEST_STATE_DIR', 'QD_TEST', 'TestSeam', 'QD_BRIDGE_EXE', 'LOCALAPPDATA') {
  $ascii = $latin.IndexOf($s, [StringComparison]::Ordinal) -ge 0
  $utf16 = $latin.IndexOf([Text.Encoding]::GetEncoding(28591).GetString([Text.Encoding]::Unicode.GetBytes($s)), [StringComparison]::Ordinal) -ge 0
  if ($ascii -or $utf16) { "  FAIL: production binary contains '$s'"; $failed = $true } else { "  absent: $s" }
}

"== 3. process tests =="
$env:QD_PRODUCTION_EXE = $prod
$env:QD_BRIDGE_EXE = $seam
$t = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $dn test tests\QuotaDuck.Bridge.Tests -c Release --nologo --filter "FullyQualifiedName~BridgeProcessTests|FullyQualifiedName~StatePathHardeningTests" 2>&1
$t | Select-String 'Passed!|Failed!|^\s+Failed ' | ForEach-Object { "  " + $_.Line.Trim() }
if (($t | Select-String 'Failed!') -or -not ($t | Select-String 'Passed!')) { $failed = $true }
Remove-Item Env:\QD_PRODUCTION_EXE, Env:\QD_BRIDGE_EXE

if (-not $SkipTimings) {
  "== 4. timing of the Native AOT build ($Runs runs each; spawn -> exit, piped stdin; temp state dir via the seam entry point) =="
  $env:DOTNET_ROOT = Join-Path $repo '.tools\dotnet'
  function New-Payload([double]$pct) {
    $now = [DateTimeOffset]::UtcNow
    $r5 = $now.AddHours(3).ToUnixTimeSeconds(); $r7 = $now.AddDays(6).ToUnixTimeSeconds()
    $pctS = $pct.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
    $pad = ('"x' + 'y' * 40 + '"')
    return '{"session_id":"synthetic","transcript_path":"C:\\synthetic\\t.jsonl","cwd":"C:\\synthetic","model":{"id":"m","display_name":"M"},"workspace":{"current_dir":"C:\\synthetic","project_dir":"C:\\synthetic"},"cost":{"total_cost_usd":0.12,"total_duration_ms":1234},"context_window":{"used_percentage":8,"total_input_tokens":1234,"current_usage":{"input_tokens":1,"output_tokens":2}},"prompt_cache":{"hit_ratio":0.9,"warm":true},"notes":[' + (($pad + ',') * 20) + $pad + '],"rate_limits":{"five_hour":{"used_percentage":' + $pctS + ',"resets_at":' + $r5 + '},"seven_day":{"used_percentage":41.2,"resets_at":' + $r7 + '}}}'
  }
  function Measure-Runs([string]$name, [scriptblock]$payloadFor, [string[]]$arguments) {
    $tmp = Join-Path $env:TEMP ('qd-aot-timing-' + [guid]::NewGuid().ToString('N').Substring(0, 8)); [IO.Directory]::CreateDirectory($tmp) | Out-Null
    $times = New-Object System.Collections.Generic.List[double]
    for ($i = 0; $i -lt $Runs; $i++) {
      $psi = New-Object Diagnostics.ProcessStartInfo $seam
      $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
      foreach ($a in $arguments) { $psi.Arguments += $a + ' ' }
      $psi.EnvironmentVariables['QD_TEST_STATE_DIR'] = Join-Path $tmp 'QuotaDuck\bridge'
      $payload = & $payloadFor $i
      $sw = [Diagnostics.Stopwatch]::StartNew()
      $p = [Diagnostics.Process]::Start($psi)
      $p.StandardInput.Write($payload); $p.StandardInput.Close()
      $null = $p.StandardOutput.ReadToEnd(); $p.WaitForExit()
      $times.Add($sw.Elapsed.TotalMilliseconds)
    }
    [IO.Directory]::Delete($tmp, $true)
    $sorted = $times | Sort-Object
    $median = $sorted[[int]($sorted.Count / 2)]; $p90 = $sorted[[int]([Math]::Ceiling($sorted.Count * 0.9)) - 1]
    Write-Host ("  {0,-52} first={1,5:N0}  median={2,5:N1}  p90={3,5:N1}  min={4,5:N1}  max={5,5:N1} ms" -f $name, $times[0], $median, $p90, $sorted[0], $sorted[-1])
    return [double]$median
  }
  $mA = Measure-Runs 'normal mode, unchanged data (no disk write)' { param($i) New-Payload 23.5 } @()
  $null = Measure-Runs 'normal mode, changing data (merge + atomic write)' { param($i) New-Payload (10 + $i * 0.5) } @()
  $null = Measure-Runs '--tap, changing data (merge + atomic write)' { param($i) New-Payload (10 + $i * 0.5) } @('--tap')
  $null = Measure-Runs '--tap, nothing usable (early exit, no disk)' { param($i) '{"model":"x"}' } @('--tap')
  "  (PowerShell Process-start overhead is included in every figure above; the production exe adds only the token-based folder lookup)"
  # PRODUCTION exe timing, only on paths that CANNOT write state (its state path is the user's real one):
  #   --tap + nothing usable  : no state needed at all (lazy folder resolution => never resolves)
  #   normal + nothing usable : resolves the token-based folder and READS (never writes) whatever state exists
  function Measure-Prod([string]$name, [string[]]$arguments) {
    $times = New-Object System.Collections.Generic.List[double]
    for ($i = 0; $i -lt $Runs; $i++) {
      $psi = New-Object Diagnostics.ProcessStartInfo $prod
      $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
      foreach ($a in $arguments) { $psi.Arguments += $a + ' ' }
      $sw = [Diagnostics.Stopwatch]::StartNew(); $p = [Diagnostics.Process]::Start($psi)
      $p.StandardInput.Write('{"model":"x"}'); $p.StandardInput.Close(); $null = $p.StandardOutput.ReadToEnd(); $p.WaitForExit(); $times.Add($sw.Elapsed.TotalMilliseconds)
    }
    $s = $times | Sort-Object
    $med = $s[[int]($s.Count / 2)]
    Write-Host ("  PRODUCTION {0,-42} first={1,5:N0}  median={2,5:N1}  p90={3,5:N1} ms" -f $name, $times[0], $med, $s[[int]([Math]::Ceiling($s.Count * 0.9)) - 1])
    return [double]$med
  }
  $null = Measure-Prod '--tap, nothing usable (no state needed)' @('--tap')
  $mProd = Measure-Prod 'normal, nothing usable (resolve + read)' @()
  $bench = Join-Path $repo 'spikes\spike4-footprint\out\bench\bench.exe'
  if (Test-Path $bench) {
    $tmp = Join-Path $env:TEMP ('qd-aot-bench-' + [guid]::NewGuid().ToString('N').Substring(0, 8)); [IO.Directory]::CreateDirectory($tmp) | Out-Null
    $env:QD_TEST_STATE_DIR = Join-Path $tmp 'QuotaDuck\bridge'
    $pl = Join-Path $tmp 'payload.json'; [IO.File]::WriteAllText($pl, (New-Payload 23.5), (New-Object Text.UTF8Encoding($false)))
    "  cross-check with the Step-0 C# harness (same method as the 29 ms prototype figure; seam exe, unchanged data):"
    & $bench bridge $seam $pl $Runs | ForEach-Object { "    " + ($_ -replace '\s+wrote state file:.*$', '') }
    Remove-Item Env:\QD_TEST_STATE_DIR
    [IO.Directory]::Delete($tmp, $true)
  }
  if ($mA -le $MaxMedianMs) { "  timing gate (seam, unchanged data, no write) median <= $MaxMedianMs ms: PASS ({0:N1})" -f $mA } else { "  timing gate (seam) median <= $MaxMedianMs ms: FAIL ({0:N1})" -f $mA; $failed = $true }
  if ($mProd -le $MaxMedianMs) { "  timing gate (PRODUCTION, resolve + read) median <= $MaxMedianMs ms: PASS ({0:N1})" -f $mProd } else { "  timing gate (PRODUCTION, resolve + read) median <= $MaxMedianMs ms: FAIL ({0:N1})" -f $mProd; $failed = $true }
}

if ($failed) { "VERIFY FAILED"; exit 1 }
"VERIFY OK"
