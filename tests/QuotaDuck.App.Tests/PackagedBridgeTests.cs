using System.Diagnostics;
using System.Text.Json;
using QuotaDuck.App.Presentation;
using QuotaDuck.App.Services;
using QuotaDuck.Coordinator;

namespace QuotaDuck.App.Tests;

/// <summary>
/// Driven by scripts/release.ps1 after it extracts the release ZIP to a folder whose name contains spaces: the REAL packaged bridge, located the
/// way the app locates it (beside the app), launched with the REAL generated <c>statusLine.command</c> through Git Bash. Without the environment
/// variable (every normal test run) this test does nothing; the release script requires the result file it writes.
/// <para>Only the bridge's documented no-op mode is used (an unsafe argument: exit 2, nothing read, nothing written), so the user's real QuotaDuck
/// state is never touched.</para>
/// </summary>
public class PackagedBridgeTests
{
    internal const string PackageDirVariable = "QUOTADUCK_PACKAGE_DIR";
    internal const string ResultFileVariable = "QUOTADUCK_PACKAGE_RESULT";

    private sealed class NoProbe : IClaudeSetupProbe
    {
        public ClaudeSetupState Check() => ClaudeSetupState.Unknown;
    }

    [Fact]
    public void ThePackagedBridge_IsFoundBesideTheApp_AndTheGeneratedCommandLaunchesIt()
    {
        string? dir = Environment.GetEnvironmentVariable(PackageDirVariable);
        string? resultFile = Environment.GetEnvironmentVariable(ResultFileVariable);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(resultFile))
        {
            return;                                                                         // only meaningful against an extracted release package
        }

        var info = new ClaudeSetupInfo(new NoProbe(), dir);
        string exe = info.BridgeExePath ?? throw new InvalidOperationException("the app does not find quotaduck-statusline.exe beside itself in " + dir);
        Assert.Equal(Path.Combine(dir, SetupGuidePresenter.BridgeFileName), exe);

        string snippet = SetupGuidePresenter.StatusLineEntry(SetupGuidePresenter.ExeText(exe));
        using JsonDocument doc = JsonDocument.Parse(snippet);
        string command = doc.RootElement.GetProperty("statusLine").GetProperty("command").GetString()!;

        string bash = new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe" }.FirstOrDefault(File.Exists)
                      ?? throw new InvalidOperationException("Git Bash is required");
        var psi = new ProcessStartInfo(bash) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command + " --bogus");                                         // unsafe argument => the bridge exits 2 and touches nothing
        using Process p = Process.Start(psi)!;
        p.StandardInput.Close();
        string output = p.StandardOutput.ReadToEnd(), error = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(30_000));

        File.WriteAllText(resultFile, $"exit={p.ExitCode}\nstdout_bytes={output.Length}\ncommand={command}\nexe={exe}\n");
        Assert.True(p.ExitCode == 2, $"the packaged bridge did not run as expected: exit {p.ExitCode}; stderr: {error}; command: {command}");   // 2 = the bridge's own bad-argument exit (127 would mean the shell could not find it)
        Assert.Equal(0, output.Length);
    }
}
