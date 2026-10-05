// SPIKE 4 benchmark harness (scratch).
//   bench bridge <exe> <payload.json> <runs>            wall time of a full invocation (spawn -> exit) with piped stdin
//   bench wpf <exe> <window|tray> <runs> <idleSeconds>   ready-time + idle memory of the WPF prototype
using System.Diagnostics;
using System.Runtime.InteropServices;

static double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }
static double Pct(List<double> v, double p) { var s = v.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, (int)Math.Ceiling(p * s.Count) - 1)]; }

if (args[0] == "bridge")
{
    string exe = args[1], payload = File.ReadAllText(args[2]); int runs = int.Parse(args[3]);
    string outDir = Path.Combine(Path.GetTempPath(), "qd-spike4-out-" + Guid.NewGuid().ToString("N")[..6]);
    var times = new List<double>();
    for (int i = 0; i < runs; i++)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        psi.Environment["QD_SPIKE_OUT"] = outDir;
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(payload); p.StandardInput.Close();
        p.StandardOutput.ReadToEnd(); p.WaitForExit();
        times.Add(sw.Elapsed.TotalMilliseconds);
        if (p.ExitCode != 0) { Console.WriteLine("exit code " + p.ExitCode); return 1; }
    }
    bool wrote = File.Exists(Path.Combine(outDir, "state.json"));
    Console.WriteLine($"runs={runs}  first={times[0]:F0} ms  median(rest)={Median(times.Skip(1).ToList()):F0} ms  p90={Pct(times, .9):F0} ms  min={times.Min():F0}  max={times.Max():F0}   wrote state file: {wrote}");
    try { Directory.Delete(outDir, true); } catch { }
    return 0;
}

if (args[0] == "wpf")
{
    string exe = args[1], mode = args[2]; int runs = int.Parse(args[3]), idle = int.Parse(args[4]);
    var fromPs = new List<double>(); var fromMain = new List<double>(); var ws = new List<double>(); var pws = new List<double>(); var priv = new List<double>(); var wall = new List<double>();
    for (int i = 0; i < runs; i++)
    {
        string flag = Path.Combine(Path.GetTempPath(), "qd-wpf-ready-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(flag); if (mode == "tray") psi.ArgumentList.Add("--tray");
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        while (!File.Exists(flag) && sw.Elapsed.TotalSeconds < 30) Thread.Sleep(5);
        if (!File.Exists(flag)) { Console.WriteLine("never became ready"); try { p.Kill(true); } catch { } return 1; }
        wall.Add(sw.Elapsed.TotalMilliseconds);
        Thread.Sleep(50); var parts = File.ReadAllText(flag).Split(' '); fromPs.Add(double.Parse(parts[1])); fromMain.Add(double.Parse(parts[2]));
        if (i >= runs - 3 || runs <= 3)   // memory sampled on the last 3 runs only (idle wait is slow)
        {
            Thread.Sleep(idle * 1000); p.Refresh();
            var c = new PMC { cb = (uint)Marshal.SizeOf<PMC>() };
            if (GetProcessMemoryInfo(p.Handle, ref c, c.cb)) { pws.Add(c.PrivateWorkingSetSize / 1048576.0); }
            ws.Add(p.WorkingSet64 / 1048576.0); priv.Add(p.PrivateMemorySize64 / 1048576.0);
        }
        try { p.Kill(true); } catch { } p.WaitForExit(); File.Delete(flag);
    }
    Console.WriteLine($"{mode,-6} runs={runs}  ready(since process start): first={fromPs[0]:F0} ms, median={Median(fromPs):F0} ms, min={fromPs.Min():F0}, max={fromPs.Max():F0}   | since Main: median={Median(fromMain):F0} ms");
    Console.WriteLine($"       idle {idle}s: working set={Median(ws):F1} MB   PRIVATE working set={Median(pws):F1} MB   private bytes (commit)={Median(priv):F1} MB");
    return 0;
}
return 2;

[DllImport("psapi.dll", SetLastError = true)] static extern bool GetProcessMemoryInfo(IntPtr h, ref PMC c, uint cb);
[StructLayout(LayoutKind.Sequential)]
struct PMC
{
    public uint cb, PageFaultCount; public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
        QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize;
    public ulong SharedCommitUsage;
}
