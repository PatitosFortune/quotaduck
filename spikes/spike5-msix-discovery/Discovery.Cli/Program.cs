// SPIKE 5 driver. Usage: discovery-cli <dotnet.exe path (Microsoft-signed scratch source)> <unsigned scratch exe path>
// Executes NOTHING from the real Codex install. Fake packages are built under %TEMP% from harmless files.
using System.Diagnostics;
using QdSpike.Discovery;

string msSigned = args[0], unsigned = args[1];
int fails = 0;
void Show(string name, DiscoveryResult r, DiscoveryStatus expect, string tmp = "")
{
    bool ok = r.Status == expect; if (!ok) fails++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name,-58} -> {r.Status,-20} {r.ValidateMs,7:N0} ms  {(tmp.Length > 0 ? r.Detail.Replace(tmp, "<tmp>") : r.Detail)}");
}

Console.WriteLine("=== REAL discovery (nothing executed) ===");
var real = CodexDiscovery.Resolve(new FamilyPackageLocator(CodexDiscovery.CodexFamilyName), CodexDiscovery.OpenAiPolicy);
Console.WriteLine($"status      : {real.Status}");
Console.WriteLine($"package     : {real.PackageFullName}");
Console.WriteLine($"version     : {real.Version}");
Console.WriteLine($"exe (canon.): {real.ExecutablePath}");
Console.WriteLine($"signer      : {real.SignerSubject}");
Console.WriteLine($"validate ms : {real.ValidateMs:N0}  (full WinVerifyTrust over the whole image)");
var real2 = CodexDiscovery.Resolve(new FamilyPackageLocator(CodexDiscovery.CodexFamilyName), CodexDiscovery.OpenAiPolicy);
Console.WriteLine($"2nd resolve : {real2.Status} in {real2.ValidateMs:N0} ms (OS file cache warm)");

Console.WriteLine("\n=== Failure / change scenarios (fake packages from harmless files) ===");
string tmp = Path.Combine(Path.GetTempPath(), "qd-spike5-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmp);
var ms = new SignerPolicy(".NET", "Microsoft Corporation"); // subject of the scratch source file (dotnet.exe)
PackageCandidate Pkg(string ver, string? exeSource, bool createExe = true)
{
    string root = Path.Combine(tmp, "Fake.Codex_" + ver + "_x64__abcdefghijklmn");
    string dir = Path.Combine(root, "app", "resources"); Directory.CreateDirectory(dir);
    if (createExe && exeSource != null) File.Copy(exeSource, Path.Combine(dir, "codex.exe"), true);
    return new PackageCandidate(Path.GetFileName(root), Version.Parse(ver), root);
}

// not installed
Show("N1 package not installed (bogus family)", CodexDiscovery.Resolve(new FamilyPackageLocator("No.Such.Package_abcdefghijklmn"), CodexDiscovery.OpenAiPolicy), DiscoveryStatus.NotInstalled);
// happy path with a scratch signer policy
var v1 = Pkg("1.0.0.0", msSigned);
Show("V0 valid (scratch policy = Microsoft, v1.0)", CodexDiscovery.Resolve(new StaticPackageLocator(v1), ms), DiscoveryStatus.Ok, tmp);
// version change: v2 appears -> must pick v2, no stale caching
var v2 = Pkg("2.0.0.0", msSigned);
var r = CodexDiscovery.Resolve(new StaticPackageLocator(v1, v2), ms);
Show("V1 version change: v1 + v2 installed -> picks v2", r, DiscoveryStatus.Ok, tmp);
Console.WriteLine($"      resolved => {r.ExecutablePath?.Replace(tmp, "<tmp>")}   (expect ...2.0.0.0...)  {(r.ExecutablePath?.Contains("2.0.0.0") == true ? "PASS" : "FAIL")}");
if (r.ExecutablePath?.Contains("2.0.0.0") != true) fails++;
// previously resolved path vanishes (v2 uninstalled): re-resolve falls to what the OS reports now (v1)
Directory.Delete(v2.InstallRoot, true);
r = CodexDiscovery.Resolve(new StaticPackageLocator(v1), ms);
Show("V2 old path deleted; locator now reports v1 only", r, DiscoveryStatus.Ok, tmp);
// previously resolved path vanishes and locator still lists it
Show("P1 locator lists v2 but its folder is gone", CodexDiscovery.Resolve(new StaticPackageLocator(v1, v2), ms), DiscoveryStatus.PackagePathMissing, tmp);
// newest version present but exe missing -> NO silent fallback to older version
var v3 = Pkg("3.0.0.0", null, createExe: false);
Show("E1 newest package has no exe (no fallback to v1)", CodexDiscovery.Resolve(new StaticPackageLocator(v1, v3), ms), DiscoveryStatus.ExecutableMissing, tmp);
// signer mismatch: valid Microsoft-signed file but policy demands OpenAI
Show("S1 valid signature, wrong signer (policy = OpenAI)", CodexDiscovery.Resolve(new StaticPackageLocator(v1), CodexDiscovery.OpenAiPolicy), DiscoveryStatus.SignerMismatch, tmp);
// unsigned
var vu = Pkg("4.0.0.0", unsigned);
Show("S2 unsigned executable", CodexDiscovery.Resolve(new StaticPackageLocator(vu), ms), DiscoveryStatus.SignatureInvalid, tmp);
// tampered after signing
var vt = Pkg("5.0.0.0", msSigned);
string tp = Path.Combine(vt.InstallRoot, "app", "resources", "codex.exe");
var bytes = File.ReadAllBytes(tp); bytes[bytes.Length / 4] ^= 0xFF; File.WriteAllBytes(tp, bytes);
Show("S3 signed file modified by one byte (tamper)", CodexDiscovery.Resolve(new StaticPackageLocator(vt), ms), DiscoveryStatus.SignatureInvalid, tmp);
// junction escape: app\resources -> outside directory containing a validly signed exe
string outside = Path.Combine(tmp, "outside"); Directory.CreateDirectory(outside); File.Copy(msSigned, Path.Combine(outside, "codex.exe"), true);
string jroot = Path.Combine(tmp, "Fake.Codex_6.0.0.0_x64__abcdefghijklmn"); Directory.CreateDirectory(Path.Combine(jroot, "app"));
var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(jroot, "app", "resources")}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
mk.StandardOutput.ReadToEnd(); mk.WaitForExit();
Show("J1 app\\resources is a junction to an outside dir", CodexDiscovery.Resolve(new StaticPackageLocator(new PackageCandidate("Fake.Codex_6.0.0.0_x64__abcdefghijklmn", new Version(6, 0, 0, 0), jroot)), ms), DiscoveryStatus.PathEscapesPackage, tmp);

try { Directory.Delete(Path.Combine(jroot, "app", "resources")); } catch { } // remove the junction itself first
try { Directory.Delete(tmp, true); } catch { }
Console.WriteLine($"\nscenario failures: {fails}   scratch removed: {!Directory.Exists(tmp)}");
