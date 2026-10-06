# Fails (exit 1) if production source contains constructs forbidden by CLAUDE.md.
# Scans src/ only (spikes/ and tests/ are exempt). Allowances are per exact file AND per category and must stay narrow;
# every one is documented next to it. Adding an allowance is a reviewed, owner-visible act.
param([string]$Root = (Split-Path $PSScriptRoot -Parent))

$deny = [ordered]@{
  'network (HttpClient/WebClient/WebRequest/Socket/TcpClient/UdpClient/HttpListener/WebSocket)' = '\b(HttpClient|HttpClientHandler|WebClient|WebRequest|HttpWebRequest|Socket|TcpClient|TcpListener|UdpClient|HttpListener|ClientWebSocket|WebSocket)\b|System\.Net\.(Http|Sockets|WebSockets|NetworkInformation)'
  'registry / startup persistence'                                                             = 'Microsoft\.Win32\.Registry|RegistryKey|\bRegistry\.|CurrentVersion\\\\Run|StartupTask|schtasks|ScheduledTask'
  'process launch (only the Codex runner may, by allowance)'                                   = '\bProcess\.Start\b|\bProcessStartInfo\b|\bShellExecute\b|\bcmd\.exe\b'
  'raw native interop (only named files may, by allowance)'                                    = '\bDllImport\b|\bLibraryImport\b'
  'credential / auth-file / browser-storage access'                                            = 'auth\.json|\.credentials|Login Data|Local State|cookies?\.sqlite|\bCookies\b|OPENAI_API_KEY|ANTHROPIC_API_KEY|ANTHROPIC_AUTH_TOKEN'
  'Claude/Codex config or transcript paths'                                                    = '["''\\/]\.(claude|codex)\b|settings(\.local)?\.json|transcripts?\b'
  'status-line payload fields that must only ever be SKIPPED (never named)'                    = 'session_id|transcript_path|\bcwd\b|workspace|total_cost|display_name|prompt_id'
  'environment-variable reads / command-line path sources (state and executable locations are never environment-derived)' = '\bEnvironment\.(GetEnvironmentVariable|GetEnvironmentVariables|ExpandEnvironmentVariables|CommandLine|GetCommandLineArgs)\b'
  'telemetry / analytics SDKs'                                                               = 'ApplicationInsights|OpenTelemetry|Sentry|Segment\.Analytics|Amplitude|Mixpanel'
  'updater / downloader'                                                                       = 'Squirrel|Velopack|ClickOnce|AutoUpdater|BackgroundDownloader|\bDownloadFile\b'
  'logging / diagnostic output (nothing is ever logged)'                                       = '\bConsole\.(Error|Write|WriteLine)\b|\bDebug\.(Write|Print|Assert)|\bTrace\.|\bEventLog\b|\bILogger\b|\bStackTrace\b'
  'directory scanning / enumeration (only the bridge may list ITS OWN state dir, by allowance)' = '\bDirectory\.(EnumerateFiles|EnumerateDirectories|GetFiles|GetDirectories|EnumerateFileSystemEntries|GetFileSystemEntries)\b|\bSearchOption\b|\bGetLogicalDrives\b|\bDriveInfo\b'
}

# file (relative to repo root, forward slashes) => categories it is allowed to violate.
$Allow = @{
  # The single P/Invoke surface of the bridge: create ONE directory with a protected current-user-only DACL (SID strictly validated).
  'src/QuotaDuck.Bridge/Native.cs'                 = @('raw native interop (only named files may, by allowance)')
  # Removes leftover temp files (claude-v1.json.*.tmp) from the bridge's OWN fixed state directory, under its lock. No other scanning.
  'src/QuotaDuck.Bridge/FileClaudeStateStore.cs'   = @('directory scanning / enumeration (only the bridge may list ITS OWN state dir, by allowance)')
  # The Codex runner's P/Invoke surface: package-family lookup, final-path canonicalisation, WinVerifyTrust. Reads no file contents.
  'src/QuotaDuck.Codex/Native.cs'                  = @('raw native interop (only named files may, by allowance)')
  # The ONE place a process is launched: a validated absolute codex.exe, UseShellExecute=false, constant ArgumentList, cleared environment.
  'src/QuotaDuck.Codex/CodexProcess.cs'            = @('process launch (only the Codex runner may, by allowance)')
}

$files = Get-ChildItem (Join-Path $Root 'src') -Recurse -File -Include *.cs, *.csproj, *.props, *.targets, *.xaml -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$bad = @()
foreach ($f in $files) {
  $rel = $f.FullName.Substring($Root.Length).TrimStart('\').Replace('\', '/')
  $text = Get-Content -LiteralPath $f.FullName -Raw
  foreach ($k in $deny.Keys) {
    if ($Allow.ContainsKey($rel) -and $Allow[$rel] -contains $k) { continue }
    # case-SENSITIVE for field names / API names so e.g. the namespace QuotaDuck.Core.Claude is not mistaken for a ".claude" path
    if ($text -cmatch $deny[$k]) { $bad += "$rel : $k" }
  }
  # no third-party runtime packages in production projects
  if ($f.Extension -eq '.csproj' -and $text -match '<PackageReference') { $bad += "$rel : PackageReference in production project (runtime NuGet packages are forbidden)" }
}
# every allowance must point at a real file (stale allowances silently widen the rule)
foreach ($k in $Allow.Keys) { if (-not (Test-Path (Join-Path $Root $k))) { $bad += "$k : allowance refers to a file that does not exist" } }

if ($bad.Count) { "DENYLIST VIOLATIONS:"; $bad | ForEach-Object { "  $_" }; exit 1 }
"denylist OK: $($files.Count) production files scanned, $($Allow.Count) narrow allowance(s), no violations"
