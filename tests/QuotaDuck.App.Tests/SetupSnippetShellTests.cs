using System.Diagnostics;
using System.Text.Json;
using QuotaDuck.App.Presentation;

namespace QuotaDuck.App.Tests;

/// <summary>
/// The generated Claude setup snippet is a shell command: Claude Code evaluates <c>statusLine.command</c> through Git Bash on Windows (Step 0).
/// These tests run the REAL generated command through that same shell, for install paths that break naive quoting. A small stand-in executable
/// is copied to <c>quotaduck-statusline.exe</c> in each folder (the real bridge is exercised by scripts/release.ps1 against the packaged ZIP).
/// </summary>
public class SetupSnippetShellTests
{
    private static readonly string[] BashLocations =
    [
        @"C:\Program Files\Git\bin\bash.exe",
        @"C:\Program Files\Git\usr\bin\bash.exe",
        @"C:\Program Files (x86)\Git\bin\bash.exe",
    ];

    private static string Bash() =>
        BashLocations.FirstOrDefault(File.Exists)
        ?? throw new InvalidOperationException("Git Bash (bash.exe) is required by this test: it is the shell Claude Code uses for status lines on Windows.");

    private static string StandIn() => Path.Combine(Environment.SystemDirectory, "hostname.exe");

    private static (int Exit, string Out, string Err) RunInBash(string script)
    {
        var psi = new ProcessStartInfo(Bash()) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        using Process p = Process.Start(psi)!;
        p.StandardInput.Close();
        string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(30_000), "shell timed out");
        return (p.ExitCode, o, e);
    }

    public static TheoryData<string> Folders() => new()
    {
        "QuotaDuck",
        "Program Files test/QuotaDuck",
        "O'Brien's tools/QuotaDuck",
        "cost $5 and $HOME/QuotaDuck",
        "back`tick`/QuotaDuck",
        "Zoë Müller 日本語/QuotaDuck",
        "a  double  space/QuotaDuck (x86)/QuotaDuck",
        "semi;colon & amp/QuotaDuck",
    };

    [Theory]
    [MemberData(nameof(Folders))]
    public void TheGeneratedStatusLineCommand_LaunchesTheBridge_ThroughTheShell(string folder)
    {
        string root = Path.Combine(Path.GetTempPath(), "qd-snippet-" + Guid.NewGuid().ToString("N"));
        try
        {
            string dir = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, SetupGuidePresenter.BridgeFileName);
            File.Copy(StandIn(), exe);

            string snippet = SetupGuidePresenter.StatusLineEntry(SetupGuidePresenter.ExeText(exe));
            using JsonDocument doc = JsonDocument.Parse(snippet);                          // valid JSON whatever the path holds
            string command = doc.RootElement.GetProperty("statusLine").GetProperty("command").GetString()!;

            (int exit, string output, string error) = RunInBash(command);
            Assert.True(exit == 0, $"exit {exit}; stderr: {error}; command: {command}");
            Assert.False(string.IsNullOrWhiteSpace(output));                                // the stand-in really ran

            // the tap line: the user pastes it into their own script, where $input holds the status-line JSON
            (int tapExit, _, string tapError) = RunInBash("input='{}'\n" + SetupGuidePresenter.TapLine(SetupGuidePresenter.ExeText(exe)));
            Assert.NotEqual(127, tapExit);                                                  // 127 = not found / not runnable
            Assert.DoesNotContain("No such file", tapError, StringComparison.Ordinal);
            Assert.DoesNotContain("not found", tapError, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("qd-snippet-", StringComparison.Ordinal))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void WithoutTheFix_ASpacedPath_WouldBeCommandNotFound_TheTestsDiscriminate()
    {
        // documents the original defect: the same path, unquoted, is split by the shell at the first space
        string root = Path.Combine(Path.GetTempPath(), "qd-snippet-" + Guid.NewGuid().ToString("N"));
        try
        {
            string dir = Path.Combine(root, "has space");
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, SetupGuidePresenter.BridgeFileName);
            File.Copy(StandIn(), exe);
            (int exit, _, _) = RunInBash(SetupGuidePresenter.ExeText(exe));
            Assert.Equal(127, exit);
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("qd-snippet-", StringComparison.Ordinal))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Theory]
    [InlineData("C:/Program Files/QuotaDuck/quotaduck-statusline.exe", "\"C:/Program Files/QuotaDuck/quotaduck-statusline.exe\"")]
    [InlineData("C:/Users/O'Brien/x.exe", "\"C:/Users/O'Brien/x.exe\"")]
    [InlineData("C:/a$b/x.exe", "\"C:/a\\$b/x.exe\"")]
    [InlineData("C:/a`b/x.exe", "\"C:/a\\`b/x.exe\"")]
    [InlineData("C:/Zoë/日本/x.exe", "\"C:/Zoë/日本/x.exe\"")]
    public void ShellQuote_EscapesOnlyWhatIsSpecialInsideDoubleQuotes(string path, string expected) => Assert.Equal(expected, SetupGuidePresenter.ShellQuote(path));

    [Fact]
    public void TheCommand_IsAlwaysAQuotedWord_EvenWhenThePathHasNoSpaces_AndIsJsonEscaped()
    {
        string snippet = SetupGuidePresenter.StatusLineEntry("C:/Tools/quotaduck-statusline.exe");
        Assert.Contains("\"command\": \"\\\"C:/Tools/quotaduck-statusline.exe\\\"\"", snippet, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(snippet);
        Assert.Equal("\"C:/Tools/quotaduck-statusline.exe\"", doc.RootElement.GetProperty("statusLine").GetProperty("command").GetString());
        string dollar = SetupGuidePresenter.StatusLineEntry("C:/a$b/x.exe");
        using JsonDocument d2 = JsonDocument.Parse(dollar);
        Assert.Equal("\"C:/a\\$b/x.exe\"", d2.RootElement.GetProperty("statusLine").GetProperty("command").GetString());
    }

    [Fact]
    public void ThePlaceholderCommand_IsQuotedToo_SoEditingItInPlaceKeepsItValid()
    {
        string snippet = SetupGuidePresenter.StatusLineEntry(SetupGuidePresenter.ExeText(null));
        using JsonDocument doc = JsonDocument.Parse(snippet);
        Assert.Equal("\"C:/Path/To/quotaduck-statusline.exe\"", doc.RootElement.GetProperty("statusLine").GetProperty("command").GetString());
    }
}
