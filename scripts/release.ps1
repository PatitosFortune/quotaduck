# Builds the QuotaDuck release package for win-x64: framework-dependent app + Native AOT Claude bridge -> QuotaDuck-<version>-win-x64.zip
#
#   powershell -File scripts\release.ps1                      # clean tree required
#   powershell -File scripts\release.ps1 -AllowDirty          # development run (the revision is stamped "<hash>-dirty")
#   powershell -File scripts\release.ps1 -CheckReproducible   # also builds a second time from scratch and compares every byte
#
# It FAILS (non-zero exit, nothing half-published) if:
#   - the publish outputs or the assembled package differ from release\MANIFEST.txt (the approved file list),
#   - any binary lacks the version / product / company metadata,
#   - NOTICE does not embed the third-party notices of the Native AOT compiler package that actually built the bridge,
#   - the README names a different package version,
#   - the extracted ZIP (from a folder whose name has spaces, an apostrophe, $, a semicolon and non-ASCII characters) differs from what was built,
#   - the packaged bridge, found beside the app and launched with the REAL generated statusLine.command through Git Bash, does not run.
# Outputs (artifacts\release, git-ignored): the ZIP, SHA256SUMS.txt (ZIP + hash list), <name>.files.sha256 (every file inside the ZIP).
# Nothing is signed, tagged or published by this script.
param(
  [switch]$AllowDirty,
  [switch]$CheckReproducible,
  [string]$OutDir
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
$dn = Join-Path $PSScriptRoot 'dotnet.ps1'
if (-not $OutDir) { $OutDir = Join-Path $repo 'artifacts\release' }
$OutDir = [IO.Path]::GetFullPath($OutDir)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$utf8 = New-Object Text.UTF8Encoding($false)

function Fail([string]$m) { throw "RELEASE FAILED: $m" }
function Remove-Dir([string]$p) {
  $full = [IO.Path]::GetFullPath($p)
  if (-not $full.StartsWith((Join-Path $repo 'artifacts') + '\', [StringComparison]::OrdinalIgnoreCase)) { Fail "refusing to delete outside <repo>\artifacts: $full" }
  if (Test-Path -LiteralPath $full) { [IO.Directory]::Delete($full, $true) }
}
function Run-Dotnet([string[]]$arguments) {
  $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $dn @arguments 2>&1
  if ($LASTEXITCODE -ne 0) { $out | ForEach-Object { Write-Host $_ }; Fail "dotnet $($arguments -join ' ')" }
  $out | Where-Object { $_ -match 'warning|error' } | ForEach-Object { Write-Host "  $_" }
}
function Ord([string[]]$a) { $l = New-Object 'System.Collections.Generic.List[string]'; $l.AddRange($a); $l.Sort([StringComparer]::Ordinal); return ,$l.ToArray() }
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

# ---------------- version, revision, manifest ----------------
$props = [IO.File]::ReadAllText((Join-Path $repo 'Directory.Build.props'))
$m = [regex]::Match($props, '<Version>([^<]+)</Version>')
if (-not $m.Success) { Fail 'no <Version> in Directory.Build.props' }
$version = $m.Groups[1].Value
$manifest = @(Get-Content (Join-Path $repo 'release\MANIFEST.txt') | Where-Object { $_ -and -not $_.StartsWith('#') })
if ((Ord $manifest) -join "`n" -ne ($manifest -join "`n")) { Fail 'release\MANIFEST.txt is not in ordinal order' }
$head = (& git rev-parse --short=9 HEAD).Trim()
$dirty = @(& git status --porcelain).Count -gt 0
if ($dirty -and -not $AllowDirty) { Fail 'the working tree is not clean (commit first, or use -AllowDirty for a development run)' }
$rev = if ($dirty) { "$head-dirty" } else { $head }
$zipName = "QuotaDuck-$version-win-x64.zip"
"== QuotaDuck release $version (revision $rev) =="

function Build-Package([string]$work, [string]$zipPath, [string]$filesListPath) {
  Remove-Dir $work
  # a from-scratch build every time: incremental compiler / Native AOT caches must not be able to hide non-determinism
  foreach ($proj in Get-ChildItem (Join-Path $repo 'src') -Directory) {
    foreach ($sub in 'bin', 'obj') {
      $d = Join-Path $proj.FullName $sub
      if ((Test-Path -LiteralPath $d) -and $d.StartsWith((Join-Path $repo 'src') + '\', [StringComparison]::OrdinalIgnoreCase)) { [IO.Directory]::Delete($d, $true) }
    }
  }
  $appOut = Join-Path $work 'publish-app'; $brOut = Join-Path $work 'publish-bridge'; $pkg = Join-Path $work 'pkg\QuotaDuck'
  [IO.Directory]::CreateDirectory($pkg) | Out-Null

  Write-Host "-- publishing the app (win-x64, framework-dependent)"
  Run-Dotnet @('publish', 'src\QuotaDuck.App\QuotaDuck.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', $appOut, '-p:ContinuousIntegrationBuild=true', "-p:SourceRevisionId=$rev", '--nologo', '-v', 'q')
  Write-Host "-- publishing the bridge (win-x64, Native AOT)"
  Run-Dotnet @('publish', 'src\QuotaDuck.Bridge\QuotaDuck.Bridge.csproj', '-c', 'Release', '-r', 'win-x64', '-o', $brOut, '-p:ContinuousIntegrationBuild=true', "-p:SourceRevisionId=$rev", '--nologo', '-v', 'q')

  # 1. the publish outputs contain exactly the approved files (nothing extra such as .pdb, satellite folders, runtime files)
  $appExpected = Ord @($manifest | Where-Object { $_ -clike 'QuotaDuck/QuotaDuck*' } | ForEach-Object { $_.Substring('QuotaDuck/'.Length) })
  $appActual = Ord @(Get-ChildItem $appOut -Recurse -File | ForEach-Object { $_.FullName.Substring($appOut.Length + 1).Replace('\', '/') })
  if (($appActual -join '|') -ne ($appExpected -join '|')) { Fail ("app publish output differs from the manifest.`n  expected: {0}`n  actual:   {1}" -f ($appExpected -join ', '), ($appActual -join ', ')) }
  $brActual = @(Get-ChildItem $brOut -Recurse -File | ForEach-Object { $_.FullName.Substring($brOut.Length + 1).Replace('\', '/') })
  if (($brActual -join '|') -ne 'quotaduck-statusline.exe') { Fail ("bridge publish output is not exactly quotaduck-statusline.exe: " + ($brActual -join ', ')) }

  # 2. version / product / company metadata on every binary
  foreach ($f in @($appActual | ForEach-Object { Join-Path $appOut $_ } | Where-Object { $_ -match '\.(exe|dll)$' }) + (Join-Path $brOut 'quotaduck-statusline.exe')) {
    $vi = [Diagnostics.FileVersionInfo]::GetVersionInfo($f)
    $pv = "$($vi.ProductVersion)"
    if (-not $pv.StartsWith("$version+$rev") -and $pv -ne $version) { Fail "$(Split-Path $f -Leaf): ProductVersion '$pv' is not '$version+$rev'" }
    if ($vi.ProductName -ne 'QuotaDuck') { Fail "$(Split-Path $f -Leaf): ProductName '$($vi.ProductName)'" }
    if ($vi.CompanyName -ne 'Patitos Fortune') { Fail "$(Split-Path $f -Leaf): CompanyName '$($vi.CompanyName)'" }
    if (-not $vi.LegalCopyright -or $vi.LegalCopyright -notmatch 'MIT') { Fail "$(Split-Path $f -Leaf): LegalCopyright '$($vi.LegalCopyright)'" }
  }

  # 3. NOTICE embeds the third-party notices of the Native AOT compiler package that built the bridge
  $assets = [IO.File]::ReadAllText((Join-Path $repo 'src\QuotaDuck.Bridge\obj\project.assets.json'))
  $am = [regex]::Match($assets, 'runtime\.win-x64\.Microsoft\.DotNet\.ILCompiler/([0-9][0-9A-Za-z.\-]*)')
  if (-not $am.Success) { Fail 'cannot determine the Native AOT compiler package version from project.assets.json' }
  $ilv = $am.Groups[1].Value
  $tpn = Join-Path $repo ".tools\nuget\microsoft.dotnet.ilcompiler\$ilv\THIRD-PARTY-NOTICES.TXT"
  if (-not (Test-Path $tpn)) { Fail "missing $tpn" }
  $notice = ([IO.File]::ReadAllText((Join-Path $repo 'NOTICE'))) -replace "`r`n", "`n"
  $b = "----- BEGIN .NET THIRD-PARTY-NOTICES (Microsoft.DotNet.ILCompiler $ilv) -----`n"; $e = "`n----- END .NET THIRD-PARTY-NOTICES (Microsoft.DotNet.ILCompiler $ilv) -----"
  $bi = $notice.IndexOf($b, [StringComparison]::Ordinal); $ei = $notice.IndexOf($e, [StringComparison]::Ordinal)
  if ($bi -lt 0 -or $ei -lt $bi) { Fail "NOTICE has no third-party section for Microsoft.DotNet.ILCompiler $ilv (regenerate it from $tpn)" }
  $embedded = $notice.Substring($bi + $b.Length, $ei - $bi - $b.Length).TrimEnd("`n")
  $source = (([IO.File]::ReadAllText($tpn)) -replace "`r`n", "`n").TrimEnd("`n")
  if ($embedded -ne $source) { Fail "NOTICE's embedded third-party notices differ from $tpn" }
  Write-Host "-- NOTICE embeds the Microsoft.DotNet.ILCompiler $ilv third-party notices: identical"

  # 4. README names this version only
  $readme = [IO.File]::ReadAllText((Join-Path $repo 'README.md'))
  foreach ($rm in [regex]::Matches($readme, 'QuotaDuck-(\d[0-9A-Za-z.\-+]*)-win-x64\.zip')) { if ($rm.Groups[1].Value -ne $version) { Fail "README.md mentions version $($rm.Groups[1].Value), not $version" } }

  # 5. assemble the package from the approved sources and compare it with the manifest
  foreach ($f in $appActual) { Copy-Item -LiteralPath (Join-Path $appOut $f) -Destination (Join-Path $pkg $f) }
  Copy-Item -LiteralPath (Join-Path $brOut 'quotaduck-statusline.exe') -Destination (Join-Path $pkg 'quotaduck-statusline.exe')
  foreach ($f in 'README.md', 'LICENSE', 'NOTICE') { Copy-Item -LiteralPath (Join-Path $repo $f) -Destination (Join-Path $pkg $f) }
  $pkgRoot = Split-Path $pkg -Parent
  $pkgActual = Ord @(Get-ChildItem $pkg -Recurse -File | ForEach-Object { $_.FullName.Substring($pkgRoot.Length + 1).Replace('\', '/') })
  if (($pkgActual -join '|') -ne ($manifest -join '|')) { Fail ("package differs from release\MANIFEST.txt.`n  expected: {0}`n  actual:   {1}" -f ($manifest -join ', '), ($pkgActual -join ', ')) }
  Write-Host "-- package file list equals release\MANIFEST.txt ($($manifest.Count) files)"

  # 6. internal hashes, then a deterministic ZIP (manifest order, fixed timestamps, no extra attributes)
  $lines = foreach ($f in $manifest) { '{0} *{1}' -f (Hash (Join-Path $pkgRoot $f.Replace('/', '\'))), $f }
  [IO.File]::WriteAllText($filesListPath, (($lines -join "`n") + "`n"), $utf8)
  if (Test-Path -LiteralPath $zipPath) { [IO.File]::Delete($zipPath) }
  $stamp = New-Object DateTimeOffset(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
  $fs = [IO.File]::Create($zipPath)
  try {
    $za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    try {
      foreach ($f in $manifest) {
        $entry = $za.CreateEntry($f, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $stamp
        $in = [IO.File]::OpenRead((Join-Path $pkgRoot $f.Replace('/', '\')))
        try { $out = $entry.Open(); try { $in.CopyTo($out) } finally { $out.Dispose() } } finally { $in.Dispose() }
      }
    } finally { $za.Dispose() }
  } finally { $fs.Dispose() }
}

[IO.Directory]::CreateDirectory($OutDir) | Out-Null
$zipPath = Join-Path $OutDir $zipName
$filesList = Join-Path $OutDir "QuotaDuck-$version-win-x64.files.sha256"
$work1 = Join-Path $OutDir 'work1'
$null = Build-Package $work1 $zipPath $filesList
$zipHash = Hash $zipPath
$zipSize = (Get-Item $zipPath).Length

if ($CheckReproducible) {
  "== reproducibility: second build from scratch =="
  $zip2 = Join-Path $OutDir 'work2.zip'; $list2 = Join-Path $OutDir 'work2.files.sha256'
  $null = Build-Package (Join-Path $OutDir 'work2') $zip2 $list2
  $h2 = Hash $zip2
  $l1 = [IO.File]::ReadAllText($filesList); $l2 = [IO.File]::ReadAllText($list2)
  if ($h2 -ne $zipHash -or $l1 -ne $l2) {
    "  per-file hashes, build 1 vs build 2:"
    $a1 = $l1 -split "`n"; $a2 = $l2 -split "`n"
    for ($i = 0; $i -lt $a1.Count; $i++) { if ($a1[$i] -ne $a2[$i]) { "    DIFFERS: " + $a1[$i].Split('*')[1] } }
    Fail "the two builds are not byte-identical (zip $zipHash vs $h2)"
  }
  Write-Host "-- byte-identical: ZIP and all $($manifest.Count) internal files"
  [IO.File]::Delete($zip2); [IO.File]::Delete($list2); Remove-Dir (Join-Path $OutDir 'work2')
}

# ---------------- verify the ZIP itself, extracted into a path with spaces and awkward characters ----------------
"== verifying the ZIP from an extracted path with spaces =="
$awkward = 'QuotaDuck test; O''Brien $5 ' + [char]0x00FC + [char]0x65E5 + ' (x86)'
$verifyRoot = Join-Path (Join-Path $OutDir 'verify') $awkward
Remove-Dir (Join-Path $OutDir 'verify')
[IO.Directory]::CreateDirectory($verifyRoot) | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $verifyRoot)
$extracted = Join-Path $verifyRoot 'QuotaDuck'
$names = Ord @(Get-ChildItem -LiteralPath $verifyRoot -Recurse -File | ForEach-Object { $_.FullName.Substring($verifyRoot.Length + 1).Replace('\', '/') })
if (($names -join '|') -ne ($manifest -join '|')) { Fail 'the extracted ZIP differs from the manifest' }
foreach ($line in ([IO.File]::ReadAllText($filesList) -split "`n" | Where-Object { $_ })) {
  $h, $f = $line -split ' \*', 2
  if ((Hash (Join-Path $verifyRoot $f.Replace('/', '\'))) -ne $h) { Fail "hash mismatch after extraction: $f" }
}
"-- extracted to: $verifyRoot"
"-- all $($manifest.Count) extracted files match their recorded SHA-256"
$bridge = Join-Path $extracted 'quotaduck-statusline.exe'
$p = Start-Process -FilePath $bridge -ArgumentList '--bogus' -Wait -PassThru -NoNewWindow
if ($p.ExitCode -ne 2) { Fail "packaged bridge, started directly with an unsafe argument, exited $($p.ExitCode) (expected its documented 2)" }
"-- packaged bridge starts directly from that path (bad-argument exit 2, nothing read or written)"

$result = Join-Path $OutDir 'packaged-bridge-result.txt'
if (Test-Path $result) { [IO.File]::Delete($result) }
$env:QUOTADUCK_PACKAGE_DIR = $extracted; $env:QUOTADUCK_PACKAGE_RESULT = $result
try { Run-Dotnet @('test', 'tests\QuotaDuck.App.Tests\QuotaDuck.App.Tests.csproj', '-c', 'Release', '--nologo', '--filter', 'FullyQualifiedName~PackagedBridgeTests') }
finally { Remove-Item Env:\QUOTADUCK_PACKAGE_DIR, Env:\QUOTADUCK_PACKAGE_RESULT -ErrorAction SilentlyContinue }
if (-not (Test-Path $result)) { Fail 'the packaged-bridge test did not run (no result file)' }
$res = [IO.File]::ReadAllText($result)
if ($res -notmatch 'exit=2') { Fail "packaged bridge via the generated statusLine.command: $res" }
"-- the app finds the packaged bridge beside itself, and the REAL generated statusLine.command launches it through Git Bash:"
$res -split "`n" | Where-Object { $_ -match '^(exit|command)=' } | ForEach-Object { "     $_" }
Remove-Dir (Join-Path $OutDir 'verify'); Remove-Dir $work1; [IO.File]::Delete($result)

# ---------------- checksums file ----------------
$sums = "{0} *{1}`n{2} *{3}`n" -f $zipHash, $zipName, (Hash $filesList), (Split-Path $filesList -Leaf)
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS.txt'), $sums, $utf8)

"== done =="
"  $zipPath"
"  size   : {0:N0} bytes ({1:N2} MB)" -f $zipSize, ($zipSize / 1MB)
"  sha256 : $zipHash"
"  signed : no (unsigned release candidate)"
