# Fails (exit 1) if production source contains constructs forbidden by CLAUDE.md.
# Scans src/ only (spikes/ and tests/ are exempt). Add narrowly-scoped allowances to $Allow when a later step legitimately needs one
# (e.g. the Codex runner may use Process; the bridge/WPF shell may P/Invoke) — each allowance must name the exact file.
param([string]$Root = (Split-Path $PSScriptRoot -Parent))

$deny = [ordered]@{
  'network (HttpClient/WebClient/WebRequest/Socket/TcpClient/UdpClient/HttpListener/WebSocket)' = '\b(HttpClient|HttpClientHandler|WebClient|WebRequest|HttpWebRequest|Socket|TcpClient|TcpListener|UdpClient|HttpListener|ClientWebSocket|WebSocket)\b|System\.Net\.(Http|Sockets|WebSockets|NetworkInformation)'
  'registry / startup persistence'                                                             = 'Microsoft\.Win32|RegistryKey|\bRegistry\.|CurrentVersion\\\\Run|StartupTask|schtasks|ScheduledTask'
  'process launch (only the Codex runner may, by allowance)'                                   = '\bProcess\.Start\b|\bProcessStartInfo\b|\bShellExecute\b|\bcmd\.exe\b'
  'raw native interop (only named files may, by allowance)'                                    = '\bDllImport\b|\bLibraryImport\b'
  'credential / auth-file / browser-storage access'                                            = 'auth\.json|\.credentials|Login Data|Local State|cookies?\.sqlite|\bCookies\b|OPENAI_API_KEY|ANTHROPIC_API_KEY|ANTHROPIC_AUTH_TOKEN'
  'telemetry / analytics SDKs'                                                                 = 'ApplicationInsights|OpenTelemetry|Sentry|Segment\.Analytics|Amplitude|Mixpanel'
  'updater / downloader'                                                                       = 'Squirrel|Velopack|ClickOnce|AutoUpdater|BackgroundDownloader|\bDownloadFile\b'
}
# file (relative to repo root, forward slashes) => categories it is allowed to violate. Empty until a gated step adds one.
$Allow = @{ }

$files = Get-ChildItem (Join-Path $Root 'src') -Recurse -File -Include *.cs, *.csproj, *.props, *.targets, *.xaml -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$bad = @()
foreach ($f in $files) {
  $rel = $f.FullName.Substring($Root.Length).TrimStart('\').Replace('\', '/')
  $text = Get-Content -LiteralPath $f.FullName -Raw
  foreach ($k in $deny.Keys) {
    if ($Allow.ContainsKey($rel) -and $Allow[$rel] -contains $k) { continue }
    if ($text -match $deny[$k]) { $bad += "$rel : $k" }
  }
  # no third-party runtime packages in production projects
  if ($f.Extension -eq '.csproj' -and $text -match '<PackageReference') { $bad += "$rel : PackageReference in production project (runtime NuGet packages are forbidden)" }
}
if ($bad.Count) { "DENYLIST VIOLATIONS:"; $bad | ForEach-Object { "  $_" }; exit 1 }
"denylist OK: $($files.Count) production files scanned, no violations"
