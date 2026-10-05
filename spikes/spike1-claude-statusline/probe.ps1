# SPIKE 1 probe (scratch). Runs as a Claude Code status-line command.
# Records ONLY: a UTC timestamp, which surface-identifying env markers exist, the NAMES of the top-level payload keys,
# and whether rate_limits.five_hour / seven_day (+ used_percentage / resets_at) are present.
# It records NO payload values, NO paths, NO session id, NO model name, NO prompts. Output: %TEMP%\qd-spike1\events.jsonl
$ErrorActionPreference = 'SilentlyContinue'
$raw = [Console]::In.ReadToEnd()
$o = $null; try { $o = $raw | ConvertFrom-Json } catch {}
$names = @(); if ($o) { $names = @($o.PSObject.Properties.Name) }
$rl = $null; if ($o -and ($names -contains 'rate_limits')) { $rl = $o.rate_limits }
function Has($obj, $n) { [bool]($obj -and ($obj.PSObject.Properties.Name -contains $n) -and $null -ne $obj.$n) }
$fh = if (Has $rl 'five_hour') { $rl.five_hour } else { $null }
$sd = if (Has $rl 'seven_day') { $rl.seven_day } else { $null }

# parent process chain (names only, up to 6 levels)
$chain = @(); $procId = $PID
try {
  $all = Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name
  for ($i = 0; $i -lt 6; $i++) { $p = $all | Where-Object { $_.ProcessId -eq $procId } | Select-Object -First 1; if (-not $p) { break }; $chain += $p.Name; $procId = $p.ParentProcessId }
} catch {}

$base = $env:ANTHROPIC_BASE_URL
$rec = [ordered]@{
  t                 = (Get-Date).ToUniversalTime().ToString('o')
  entrypoint        = $env:CLAUDE_CODE_ENTRYPOINT          # e.g. cli / claude-vscode / claude-desktop (enumerated label, not sensitive)
  term_program      = $env:TERM_PROGRAM
  has_wt_session    = [bool]$env:WT_SESSION                # Windows Terminal
  has_vscode_pid    = [bool]$env:VSCODE_PID
  has_ide_port      = [bool]$env:CLAUDE_CODE_SSE_PORT
  base_url_set      = [bool]$base
  base_url_loopback = [bool]($base -match '^https?://(127\.0\.0\.1|localhost|\[::1\])')
  parent_chain      = $chain
  payload_keys      = $names
  has_rate_limits   = [bool]$rl
  has_five_hour     = [bool]$fh
  has_seven_day     = [bool]$sd
  five_has_pct      = (Has $fh 'used_percentage')
  five_has_reset    = (Has $fh 'resets_at')
  seven_has_pct     = (Has $sd 'used_percentage')
  seven_has_reset   = (Has $sd 'resets_at')
}
$dir = Join-Path $env:TEMP 'qd-spike1'; New-Item -ItemType Directory -Force -Path $dir | Out-Null
($rec | ConvertTo-Json -Compress -Depth 4) | Add-Content -LiteralPath (Join-Path $dir 'events.jsonl') -Encoding utf8
Write-Output ("QD-probe ok | rate_limits=" + $(if ($rl) { 'yes' } else { 'no' }))
