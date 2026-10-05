# Runs the repo-local .NET SDK (.tools\dotnet) with telemetry off and a repo-local NuGet cache.
# Usage: pwsh/powershell -File scripts\dotnet.ps1 build spikes\...\x.csproj -c Release
$root = Split-Path $PSScriptRoot -Parent
$dn = Join-Path $root '.tools\dotnet'
$env:DOTNET_ROOT = $dn
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
$env:PATH = "$dn;$env:PATH"
# Native AOT needs vswhere on PATH so the ILCompiler targets can locate the MSVC linker (process-local only)
$vsInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (Test-Path $vsInstaller) { $env:PATH = "$env:PATH;$vsInstaller" }
& (Join-Path $dn 'dotnet.exe') @args
exit $LASTEXITCODE
