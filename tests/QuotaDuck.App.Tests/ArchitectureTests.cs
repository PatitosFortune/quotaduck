using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace QuotaDuck.App.Tests;

/// <summary>The shell stays a thin consumer: WPF only here, no provider logic, no network, no process launching, no file writes.</summary>
public class ArchitectureTests
{
    private static readonly Assembly App = typeof(QuotaDuck.App.Shell.ShellController).Assembly;

    internal static string Root()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "QuotaDuck.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src", project), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains("\\obj\\") && !f.Contains("\\bin\\"));

    [Fact]
    public void AppReferences_OnlyBclWpfCoreAndCoordinator_NotCodexNotBridgeNotNetwork()
    {
        string[] refs = App.GetReferencedAssemblies().Select(r => r.Name!).ToArray();
        Assert.Contains("QuotaDuck.Coordinator", refs);
        Assert.Contains("QuotaDuck.Core", refs);
        Assert.Contains("QuotaDuck.Art", refs);
        Assert.DoesNotContain("QuotaDuck.Codex", refs);                                   // no direct Codex interaction from the UI
        Assert.DoesNotContain("QuotaDuck.Bridge", refs);                                  // no Claude state access/writes from the UI
        string[] forbidden = ["System.Net", "System.Diagnostics.Process", "Microsoft.Win32.Registry", "System.Data", "System.Management", "System.Security.Cryptography", "System.IO.Pipes"];
        Assert.DoesNotContain(refs, r => forbidden.Any(f => r.StartsWith(f, StringComparison.Ordinal)));
    }

    [Fact]
    public void WpfAndWinForms_AreReferencedByTheAppProjectOnly()
    {
        string[] ui = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms", "System.Drawing", "System.Xaml"];
        foreach (string dll in new[] { typeof(QuotaDuck.Core.WindowKey), typeof(QuotaDuck.Coordinator.QuotaCoordinator), typeof(QuotaDuck.Codex.CodexQuotaReader), typeof(QuotaDuck.Art.PondScene) }.Select(t => t.Assembly.Location))
        {
            string[] refs = Assembly.LoadFrom(dll).GetReferencedAssemblies().Select(r => r.Name!).ToArray();
            Assert.DoesNotContain(refs, r => ui.Any(u => r.StartsWith(u, StringComparison.Ordinal)));
        }

        foreach (string proj in Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.csproj", SearchOption.AllDirectories).Where(p => !p.EndsWith("QuotaDuck.App.csproj")))
        {
            string text = File.ReadAllText(proj);
            Assert.DoesNotContain("UseWPF", text);
            Assert.DoesNotContain("UseWindowsForms", text);
        }

        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.xaml", SearchOption.AllDirectories), f => !f.Contains("QuotaDuck.App") && !f.Contains("\\obj\\"));
    }

    [Fact]
    public void NoProviderParsing_NoJson_NoProtocol_NoSecrets_InTheUiSources()
    {
        string[] banned =
        [
            "System.Text.Json", "Utf8JsonReader", "JsonDocument", "JsonSerializer", "CodexResponseParser", "ClaudeStatusLineParser", "ClaudeStateCodec",
            "QuotaDuck.Codex", "QuotaDuck.Bridge", "ICodexQuotaSource", "CodexQuotaReader", "CodexReadOutcome", "IClaudeStateSource", "FileClaudeStateSource",
            "app-server", "account/rateLimits", "initialize", "auth.json", "settings.json", ".claude", ".codex", "OPENAI", "ANTHROPIC", "Bearer", "stderr",
        ];
        foreach (string f in Sources("QuotaDuck.App"))
        {
            string text = File.ReadAllText(f);
            foreach (string b in banned)
            {
                // the first-run guide's TEXT names the user's settings file (it never opens it: the coordinator's probe does, read-only)
                if (Path.GetFileName(f) == "SetupGuide.cs" && b is ".claude" or "settings.json")
                {
                    continue;
                }

                Assert.False(text.Contains(b, StringComparison.Ordinal), $"{Path.GetFileName(f)} mentions {b}");
            }
        }
    }

    [Fact]
    public void NoProcessStart_NoNetwork_NoEnvironmentReads_NoFileWrites_InTheApp()
    {
        string[] banned =
        [
            "Process.Start", "ProcessStartInfo", "HttpClient", "WebClient", "Socket", "TcpClient", "System.Net", "WebRequest",
            "GetEnvironmentVariable", "ExpandEnvironmentVariables", "CommandLine", "File.Write", "File.Create", "File.Append", "File.Delete", "File.Move", "File.Copy",
            "StreamWriter", "FileStream", "Directory.Create", "Registry", "DllImport", "LibraryImport", "Console.", "Debug.", "Trace.",
        ];
        foreach (string f in Sources("QuotaDuck.App").Where(f => f.EndsWith(".cs")))
        {
            string text = File.ReadAllText(f);
            foreach (string b in banned)
            {
                if (Path.GetFileName(f) == "NativeWindows.cs" && b is "DllImport" or "LibraryImport")
                {
                    continue;                                                         // the shell's one native file: window placement (documented user32/shcore)
                }

                Assert.False(text.Contains(b, StringComparison.Ordinal), $"{Path.GetFileName(f)} uses {b}");
            }
        }
    }

    [Fact]
    public void ProcessStart_StillExistsInExactlyOneProductionFile()
    {
        string[] sites = new[] { "QuotaDuck.Core", "QuotaDuck.Bridge", "QuotaDuck.Codex", "QuotaDuck.Coordinator", "QuotaDuck.App" }
            .SelectMany(Sources)
            .Where(f => File.ReadAllText(f).Contains("Process.Start", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Root(), f).Replace('\\', '/'))
            .ToArray();
        Assert.Equal(["src/QuotaDuck.Codex/CodexProcess.cs"], sites);
    }

    [Fact]
    public void Xaml_HasExactlyThreeButtons_CodexRefresh_SetUp_Copy_AndNothingNamedLikeAClaudeRefresh()
    {
        string xaml = File.ReadAllText(Path.Combine(Root(), "src", "QuotaDuck.App", "Views", "CompactFlyoutContent.xaml"));
        string window = File.ReadAllText(Path.Combine(Root(), "src", "QuotaDuck.App", "Views", "FlyoutWindow.xaml"));
        Assert.DoesNotContain("<Button", window);
        // exactly three buttons: Codex's Refresh, Claude's first-run "Set up"/"Hide" (shown only while setup is pending) and the guide's Copy
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(xaml, "<Button").Count);
        Assert.Contains("Refresh Codex quota", xaml);
        Assert.Contains("Copy the setup text to the clipboard", xaml);
        Assert.Contains("SetupButtonName", xaml);
        Assert.DoesNotContain("Refresh Claude", xaml);
        Assert.DoesNotContain("Fetch", xaml);
    }

    [Fact]
    public void NativeCode_ExistsInExactlyOneAppFile_AndNothingReadsOrWritesClaudeSettings()
    {
        string[] native = Sources("QuotaDuck.App").Where(f => f.EndsWith(".cs") && File.ReadAllText(f).Contains("LibraryImport", StringComparison.Ordinal)).Select(f => Path.GetFileName(f)!).ToArray();
        Assert.Equal(["NativeWindows.cs"], native);
        string[] settingsText = Sources("QuotaDuck.App").Where(f => File.ReadAllText(f).Contains("settings.json", StringComparison.Ordinal)).Select(f => Path.GetFileName(f)!).ToArray();
        Assert.Equal(["SetupGuide.cs"], settingsText);                                // display text only
        string setup = File.ReadAllText(Path.Combine(Root(), "src", "QuotaDuck.App", "Presentation", "SetupGuide.cs"));
        foreach (string io in new[] { "File.", "FileStream", "StreamReader", "Directory." })
        {
            Assert.DoesNotContain(io, setup);
        }
    }

    [Fact]
    public void TheRuntimePrerequisite_IsDeclared_SoTheApphostShowsTheOfficialInstallDialog()
    {
        string outDir = new[] { "Release", "Debug" }.Select(c => Path.Combine(Root(), "src", "QuotaDuck.App", "bin", c, "net10.0-windows")).Where(Directory.Exists)
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "QuotaDuck.dll"))).First();
        string cfg = Path.Combine(outDir, "QuotaDuck.runtimeconfig.json");
        Assert.True(File.Exists(cfg), "runtimeconfig missing");
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
        System.Text.Json.JsonElement opts = doc.RootElement.GetProperty("runtimeOptions");
        System.Text.Json.JsonElement desktop = opts.GetProperty("frameworks").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "Microsoft.WindowsDesktop.App");
        Assert.StartsWith("10.0", desktop.GetProperty("version").GetString());                    // the Desktop runtime: that is what the missing-runtime dialog names
        Assert.True(File.Exists(Path.Combine(outDir, "QuotaDuck.exe")), "framework-dependent apphost missing");
        Assert.False(opts.TryGetProperty("includedFrameworks", out _));                         // not self-contained: v1 stays framework-dependent
    }

    [Fact]
    public void StringLiterals_OfTheAppAssembly_ContainNoSecretsPathsOrUrls()
    {
        foreach (string lit in UserStrings(App.Location))
        {
            string lower = lit.ToLowerInvariant();
            foreach (string bad in new[] { "http://", "https://", "credential", "password", "bearer", "api_key", "cookie", "c:\\", "\\users\\" })
            {
                Assert.False(lower.Contains(bad, StringComparison.Ordinal), $"literal \"{lit}\" contains \"{bad}\"");
            }
        }
    }

    [Fact]
    public void Denylist_Passes()
    {
        string script = Path.Combine(Root(), "scripts", "check-denylist.ps1");
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000);
        Assert.True(p.ExitCode == 0, output);
        Assert.Contains("denylist OK", output);
    }

    private static IEnumerable<string> UserStrings(string dll)
    {
        using FileStream fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();
        UserStringHandle h = MetadataTokens.UserStringHandle(0);
        while (true)
        {
            h = md.GetNextHandle(h);
            if (h.IsNil)
            {
                yield break;
            }

            yield return md.GetUserString(h);
        }
    }
}
