// SPIKE 3 — minimal child environment for `codex app-server` quota-only handshake (prototype; not production).
// Sends ONLY: initialize, initialized, account/rateLimits/read. Prints booleans/counts only — never values.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using QdSpike.Discovery;

var loc = CodexDiscovery.Resolve(new FamilyPackageLocator(CodexDiscovery.CodexFamilyName), CodexDiscovery.OpenAiPolicy);
if (loc.Status != DiscoveryStatus.Ok) { Console.WriteLine("discovery failed: " + loc.Status); return 2; }
string codex = loc.ExecutablePath!;
string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string work = Path.Combine(Path.GetTempPath(), "qd-spike3-run"); Directory.CreateDirectory(work);

string Scrub(string s) => s.Replace(home, "<USERPROFILE>").Replace(Environment.UserName, "<user>");
string? P(string n) => Environment.GetEnvironmentVariable(n);

// ---- which env categories exist in the parent (NAMES only) ----
var interesting = new[] { "SystemRoot", "SystemDrive", "windir", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "HOME", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP",
    "PATH", "PATHEXT", "COMSPEC", "ProgramData", "ProgramFiles", "CODEX_HOME", "USERNAME", "COMPUTERNAME" };
Console.WriteLine("parent has (names only): " + string.Join(", ", interesting.Where(n => P(n) != null)));
Console.WriteLine("parent proxy/cert-related names: " + (string.Join(", ", Environment.GetEnvironmentVariables().Keys.Cast<string>()
    .Where(k => k.Contains("PROXY", StringComparison.OrdinalIgnoreCase) || k.Contains("CERT", StringComparison.OrdinalIgnoreCase) || k.Contains("SSL", StringComparison.OrdinalIgnoreCase))) is { Length: > 0 } x ? x : "(none)"));
Console.WriteLine("parent OPENAI*/CODEX*/ANTHROPIC* names: " + (string.Join(", ", Environment.GetEnvironmentVariables().Keys.Cast<string>()
    .Where(k => k.StartsWith("OPENAI", StringComparison.OrdinalIgnoreCase) || k.StartsWith("CODEX", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase))) is { Length: > 0 } y ? y : "(none)"));

// metadata-only integrity oracle (no contents read)
string codexHome = Path.Combine(home, ".codex");
(long, DateTime)? Meta(string f) { var fi = new FileInfo(Path.Combine(codexHome, f)); return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : null; }
var cfgBefore = Meta("config.toml"); var authBefore = Meta("auth.json");

string SysPath() => $"{P("SystemRoot")}\\System32;{P("SystemRoot")}";
// name -> value factory; PATH is replaced by a system-only value (never the parent's dev PATH)
Dictionary<string, string> Build(params string[] names)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var n in names) { var v = n == "PATH" ? SysPath() : P(n); if (v != null) d[n] = v; }
    return d;
}

(bool ok, string why, double initMs, double readMs, string windows) Run(string label, Dictionary<string, string>? env, string[] extraArgs)
{
    var psi = new ProcessStartInfo { FileName = codex, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
        RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = work,
        StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
    psi.ArgumentList.Add("app-server"); psi.ArgumentList.Add("--stdio"); foreach (var a in extraArgs) psi.ArgumentList.Add(a);
    if (env != null) { psi.Environment.Clear(); foreach (var kv in env) psi.Environment[kv.Key] = kv.Value; }
    var sw = Stopwatch.StartNew();
    Process p;
    try { p = Process.Start(psi)!; } catch (Exception e) { return (false, "launch: " + e.GetType().Name, 0, 0, "-"); }
    var err = new StringBuilder();
    p.ErrorDataReceived += (_, e) => { if (e.Data != null && err.Length < 400) err.AppendLine(e.Data); }; p.BeginErrorReadLine();
    p.StandardInput.NewLine = "\n"; p.StandardInput.AutoFlush = true;
    var reader = p.StandardOutput;
    async Task<JsonDocument?> WaitId(int id, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            var t = reader.ReadLineAsync(); var left = until - DateTime.UtcNow; if (left < TimeSpan.Zero) break;
            if (await Task.WhenAny(t, Task.Delay(left)) != t) return null;
            var line = t.Result; if (line == null) return null;
            try { var d = JsonDocument.Parse(line); if (d.RootElement.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id && !d.RootElement.TryGetProperty("method", out _)) return d; d.Dispose(); } catch { }
        }
        return null;
    }
    bool ok = false; string why = ""; double initMs = 0, readMs = 0; string wins = "-";
    try
    {
        p.StandardInput.WriteLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"quotaduck-spike3\",\"title\":\"QuotaDuck spike 3\",\"version\":\"0.0.0\"}}}");
        using var r1 = WaitId(1, 10000).GetAwaiter().GetResult();
        initMs = sw.Elapsed.TotalMilliseconds;
        if (r1 == null) { why = p.HasExited ? $"child exited early (code {p.ExitCode})" : "no initialize response in 10 s"; }
        else if (r1.RootElement.TryGetProperty("error", out _)) why = "initialize returned error";
        else
        {
            p.StandardInput.WriteLine("{\"method\":\"initialized\"}");
            var t2 = sw.Elapsed.TotalMilliseconds;
            p.StandardInput.WriteLine("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
            using var r2 = WaitId(2, 10000).GetAwaiter().GetResult();
            readMs = sw.Elapsed.TotalMilliseconds - t2;
            if (r2 == null) why = "no rateLimits response in 10 s";
            else if (r2.RootElement.TryGetProperty("error", out var e)) why = "rateLimits error code " + (e.TryGetProperty("code", out var c) ? c.ToString() : "?");
            else if (r2.RootElement.TryGetProperty("result", out var res) && res.TryGetProperty("rateLimits", out var rl))
            {
                int n = 0; foreach (var w in new[] { "primary", "secondary" }) if (rl.TryGetProperty(w, out var wv) && wv.ValueKind == JsonValueKind.Object && wv.TryGetProperty("usedPercent", out _)) n++;
                ok = true; wins = $"{n} window(s) with usedPercent"; why = "ok";
            }
            else why = "unexpected result shape";
        }
    }
    catch (Exception e) { why = "harness: " + e.GetType().Name; }
    try { p.StandardInput.Close(); } catch { }
    if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } }
    string e2 = ok ? "" : Scrub(err.ToString().Trim().Replace("\r", "").Replace("\n", " | "));
    return (ok, ok ? why : why + (e2.Length > 0 ? "  stderr: " + (e2.Length > 220 ? e2[..220] + "…" : e2) : ""), initMs, readMs, wins);
}

void Report(string label, Dictionary<string, string>? env, params string[] extra)
{
    var r = Run(label, env, extra);
    Console.WriteLine($"{(r.ok ? "OK  " : "FAIL")} {label,-52} vars={(env == null ? "ALL(inherit)" : env.Count.ToString()),-12} init={r.initMs,6:N0}ms read={r.readMs,6:N0}ms  {r.windows}  {(r.ok ? "" : r.why)}");
}

var minimalNames = new[] { "SystemRoot", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP", "PATH" };
Console.WriteLine("\n--- control ---");
Report("C0 inherit full parent environment", null);
Console.WriteLine("--- bottom-up (what is the smallest set that works?) ---");
Report("U0 empty environment", new());
Report("U1 SystemRoot", Build("SystemRoot"));
Report("U2 + USERPROFILE", Build("SystemRoot", "USERPROFILE"));
Report("U3 + LOCALAPPDATA", Build("SystemRoot", "USERPROFILE", "LOCALAPPDATA"));
Report("U4 + APPDATA", Build("SystemRoot", "USERPROFILE", "LOCALAPPDATA", "APPDATA"));
Report("U5 + TEMP, TMP", Build("SystemRoot", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP"));
Report("U6 + PATH (System32;Windows only)", Build(minimalNames));
Console.WriteLine("--- leave-one-out from the 7-variable set (is each member necessary?) ---");
foreach (var drop in minimalNames) Report($"L- without {drop}", Build(minimalNames.Where(n => n != drop).ToArray()));

Console.WriteLine("--- analytics override (per-invocation; config.toml untouched) ---");
var min = Build(minimalNames);
Report("A1 minimal + -c analytics.enabled=false", min, "-c", "analytics.enabled=false");
Report("A2 same + --strict-config (valid key)", min, "-c", "analytics.enabled=false", "--strict-config");
Report("A3 calibration: --strict-config + bogus key", min, "-c", "analytics.qd_bogus_key=1", "--strict-config");

var cfgAfter = Meta("config.toml"); var authAfter = Meta("auth.json");
Console.WriteLine($"\nconfig.toml unchanged (size+mtime): {cfgBefore == cfgAfter}    auth.json unchanged (size+mtime): {authBefore == authAfter}");
try { Directory.Delete(work, true); } catch { }
return 0;
