// SPIKE 2 — .NET Process.Start executable-resolution behaviour (scratch only).
// Usage: launcher.exe <path-to-qdchild.exe>
// Every scenario: copies the harmless child into chosen places, launches it with Process.Start
// (UseShellExecute=false) and reports WHICH copy actually ran.
using System.ComponentModel;
using System.Diagnostics;

string src = args.Length > 0 ? args[0] : throw new ArgumentException("path to qdchild.exe required");
string appDir = Path.GetDirectoryName(Environment.ProcessPath)!;
string origPath = Environment.GetEnvironmentVariable("PATH")!;
string origCwd = Directory.GetCurrentDirectory();
string tmp = Path.Combine(Path.GetTempPath(), "qd-spike2-" + Guid.NewGuid().ToString("N")[..8]);
string cwdDir = Path.Combine(tmp, "cwd"), pathDir = Path.Combine(tmp, "pathdir"), wdDir = Path.Combine(tmp, "workdir"),
       otherCwd = Path.Combine(tmp, "elsewhere");
foreach (var d in new[] { cwdDir, pathDir, wdDir, otherCwd }) Directory.CreateDirectory(d);

Console.WriteLine($".NET {Environment.Version}  OS {Environment.OSVersion.Version}");
Console.WriteLine($"launcher app dir : {Short(appDir)}");
Console.WriteLine();
Console.WriteLine($"{"scenario",-66} -> ran copy from");

void Clean() { foreach (var d in new[] { appDir, cwdDir, pathDir, wdDir }) { var f = Path.Combine(d, "qdchild.exe"); if (File.Exists(f)) File.Delete(f); } }
void Place(params string[] dirs) { foreach (var d in dirs) File.Copy(src, Path.Combine(d, "qdchild.exe"), true); }

string Run(string fileName, string? workingDir = null, string? parentCwd = null, bool pathHasDir = false, string? arguments = null)
{
    Directory.SetCurrentDirectory(parentCwd ?? otherCwd);
    Environment.SetEnvironmentVariable("PATH", pathHasDir ? pathDir + ";" + origPath : origPath); // process-local only
    var psi = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
    if (arguments != null) psi.Arguments = arguments;
    if (workingDir != null) psi.WorkingDirectory = workingDir;
    try
    {
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        var line = o.Split('\n').FirstOrDefault(l => l.StartsWith("CHILD_PATH="))?.Trim() ?? "(no CHILD_PATH; output: " + o.Trim().Replace("\n", " / ") + ")";
        return Short(line.Replace("CHILD_PATH=", ""));
    }
    catch (Win32Exception e) { return $"NOT FOUND/LAUNCH ERROR (0x{e.NativeErrorCode:X})"; }
}
string Short(string s) => s.Replace(tmp, "<tmp>").Replace(appDir, "<APPDIR>");
void Row(string name, string result) => Console.WriteLine($"{name,-66} -> {result}");

// ---------- BARE NAME ----------
Console.WriteLine("-- bare name \"qdchild.exe\" (what NOT to do) --");
Clean(); Place(cwdDir);              Row("B1 copy only in PARENT CWD", Run("qdchild.exe", parentCwd: cwdDir));
Clean(); Place(pathDir);             Row("B2 copy only in a PATH dir", Run("qdchild.exe", pathHasDir: true));
Clean(); Place(cwdDir, pathDir);     Row("B3 copies in PARENT CWD + PATH dir", Run("qdchild.exe", parentCwd: cwdDir, pathHasDir: true));
Clean(); Place(appDir);              Row("B4 copy only in APPLICATION dir (exe's own folder)", Run("qdchild.exe"));
Clean(); Place(appDir, pathDir);     Row("B5 copies in APPLICATION dir + PATH dir", Run("qdchild.exe", pathHasDir: true));
Clean(); Place(appDir, cwdDir, pathDir); Row("B6 copies in APPDIR + CWD + PATH", Run("qdchild.exe", parentCwd: cwdDir, pathHasDir: true));
Clean(); Place(wdDir);               Row("B7 copy only in psi.WorkingDirectory (parent CWD elsewhere)", Run("qdchild.exe", workingDir: wdDir));
Clean(); Place(pathDir);             Row("B8 no extension \"qdchild\" + copy in PATH dir", Run("qdchild", pathHasDir: true));

// ---------- RELATIVE ----------
Console.WriteLine("-- relative path (depends on parent CWD) --");
Clean(); Place(cwdDir);              Row("R1 \".\\qdchild.exe\", parent CWD = cwd dir", Run(".\\qdchild.exe", parentCwd: cwdDir));
Clean(); Place(cwdDir);              Row("R2 \".\\qdchild.exe\", parent CWD = elsewhere", Run(".\\qdchild.exe", parentCwd: otherCwd));
Clean(); Place(cwdDir);              Row("R3 \".\\qdchild.exe\", psi.WorkingDirectory = cwd dir only", Run(".\\qdchild.exe", workingDir: cwdDir, parentCwd: otherCwd));

// ---------- ABSOLUTE ----------
Console.WriteLine("-- absolute path (the planned rule) --");
string good = Path.Combine(tmp, "good"); Directory.CreateDirectory(good); File.Copy(src, Path.Combine(good, "qdchild.exe"), true);
Clean(); Place(appDir, cwdDir, pathDir, wdDir);
Row("A1 absolute path; decoys in APPDIR, CWD, PATH, WorkingDir", Run(Path.Combine(good, "qdchild.exe"), workingDir: wdDir, parentCwd: cwdDir, pathHasDir: true));
string spaced = Path.Combine(tmp, "sp ace"); Directory.CreateDirectory(spaced); File.Copy(src, Path.Combine(spaced, "qdchild.exe"), true);
File.Copy(src, Path.Combine(tmp, "sp.exe"), true); // classic unquoted-path decoy: "<tmp>\sp.exe"
Row("A2 absolute path containing a space; decoy <tmp>\\sp.exe exists", Run(Path.Combine(spaced, "qdchild.exe")));
string longp = Path.Combine(tmp, "trailing dot"); Directory.CreateDirectory(longp); File.Copy(src, Path.Combine(longp, "qdchild.exe"), true);
Row("A3 absolute path, args with spaces/quotes preserved", Run(Path.Combine(good, "qdchild.exe"), arguments: "\"a b\" c"));

// ---------- SHIMS ----------
Console.WriteLine("-- .cmd shim via absolute path (why only .exe is allowed) --");
string cmd = Path.Combine(tmp, "shim.cmd"); File.WriteAllText(cmd, "@echo off\r\necho CHILD_PATH=shim.cmd ran with args: %*\r\n");
Row("S1 absolute x.cmd, safe arg \"hello\"", Run(cmd, arguments: "hello"));
string marker = Path.Combine(tmp, "INJECTED.txt");
Run(cmd, arguments: "x&echo.>\"" + marker + "\"");
Row("S2 absolute x.cmd, arg  x&echo.>marker  (cmd metachar)", File.Exists(marker) ? "INJECTION OCCURRED (marker file created) — .cmd/.bat must never be launched" : "no injection");

Directory.SetCurrentDirectory(origCwd); Environment.SetEnvironmentVariable("PATH", origPath);
Clean(); try { Directory.Delete(tmp, true); } catch { }
Console.WriteLine("\ncleanup done; scratch dir removed: " + !Directory.Exists(tmp));
