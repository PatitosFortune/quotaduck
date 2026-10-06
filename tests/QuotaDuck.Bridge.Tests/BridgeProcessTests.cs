using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using QuotaDuck.Core.Claude;
using static QuotaDuck.Core.Tests.PayloadFx;

namespace QuotaDuck.Bridge.Tests;

/// <summary>
/// End-to-end: runs the REAL built executable as a child process with a redirected LOCALAPPDATA, real stdin/stdout pipes and the
/// real clock. (scripts/verify-bridge-aot.ps1 repeats the smoke checks against the Native AOT publish.)
/// </summary>
public class BridgeProcessTests
{
    /// <summary>
    /// Process-level tests run the TEST-SEAM executable (production sources, different entry point that takes
    /// QD_TEST_STATE_DIR and refuses to run without it), because the production executable's state path is fixed and cannot be
    /// redirected. Default: the managed apphost built by the solution; QD_BRIDGE_EXE (set by scripts/verify-bridge-aot.ps1) points
    /// the same tests at the Native AOT build of the seam instead.
    /// </summary>
    private static string ExePath() => Exes.TestSeam();

    private static string StateDirOf(string root) => Path.Combine(root, "QuotaDuck", "bridge");

    private sealed record Result(int Exit, byte[] Stdout, byte[] Stderr);

    private static Result RunExe(string localAppData, byte[] stdin, params string[] args)
    {
        var psi = new ProcessStartInfo(ExePath())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["QD_TEST_STATE_DIR"] = StateDirOf(localAppData);
        using Process p = Process.Start(psi)!;
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        Task t1 = p.StandardOutput.BaseStream.CopyToAsync(stdout);
        Task t2 = p.StandardError.BaseStream.CopyToAsync(stderr);
        try
        {
            p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
            p.StandardInput.BaseStream.Flush();
        }
        catch (IOException)
        {
            // the child may exit before reading everything (e.g. rejected arguments); that is a valid outcome
        }

        try
        {
            p.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        Assert.True(p.WaitForExit(60_000), "bridge did not exit");
        Task.WaitAll(t1, t2);
        return new Result(p.ExitCode, stdout.ToArray(), stderr.ToArray());
    }

    private static string LivePayload(double five, double seven) =>
        Payload(Win(five, DateTimeOffset.UtcNow.AddHours(3)), Win(seven, DateTimeOffset.UtcNow.AddDays(6)));

    private static string StatePath(string localAppData) => Path.Combine(localAppData, "QuotaDuck", "bridge", FileClaudeStateStore.FileName);

    [Fact]
    public void NormalMode_RealProcess_PrintsOneLine_WritesState_NothingOnStderr()
    {
        using var t = new TempDir();
        Result r = RunExe(t.Path, Bytes(LivePayload(12.5, 40)));
        Assert.Equal(0, r.Exit);
        Assert.Equal("QuotaDuck | 5h 12.5% used | wk 40% used\n", Encoding.UTF8.GetString(r.Stdout));
        Assert.Empty(r.Stderr);
        ClaudeStateDecodeResult s = ClaudeStateCodec.Decode(File.ReadAllBytes(StatePath(t.Path)));
        Assert.Equal(ClaudeStateDecodeStatus.Ok, s.Status);
        Assert.Equal(12.5, s.State.FiveHour!.UsedPercent);
        Assert.Equal(40, s.State.SevenDay!.UsedPercent);
    }

    [Fact]
    public void TapMode_RealProcess_WritesZeroBytesToStdout_StillUpdatesState()
    {
        using var t = new TempDir();
        Result r = RunExe(t.Path, Bytes(LivePayload(33, 44)), "--tap");
        Assert.Equal(0, r.Exit);
        Assert.Empty(r.Stdout);                                                // exactly zero bytes
        Assert.Empty(r.Stderr);
        Assert.Equal(33, ClaudeStateCodec.Decode(File.ReadAllBytes(StatePath(t.Path))).State.FiveHour!.UsedPercent);
    }

    [Fact]
    public void TapMode_RealProcess_GarbageAndEmptyInput_ZeroBytes_ExitZero_NoStateCreated()
    {
        using var t = new TempDir();
        byte[] invalidUtf8 = [.. Bytes("{\"a\":\""), 0xFF, .. Bytes("\"}")];
        byte[][] inputs = [Bytes("not json at all"), Array.Empty<byte>(), new byte[2_000_000], invalidUtf8];
        foreach (byte[] input in inputs)
        {
            Result r = RunExe(t.Path, input, "--tap");
            Assert.Equal(0, r.Exit);
            Assert.Empty(r.Stdout);
            Assert.Empty(r.Stderr);
        }

        Assert.False(File.Exists(StatePath(t.Path)));
    }

    [Fact]
    public void InvalidArguments_RealProcess_ExitTwo_NoOutput_NoState()
    {
        using var t = new TempDir();
        Result r = RunExe(t.Path, Bytes(LivePayload(1, 2)), "--nope");
        Assert.Equal(2, r.Exit);
        Assert.Empty(r.Stdout);
        Assert.False(Directory.Exists(Path.Combine(t.Path, "QuotaDuck")));
    }

    [Fact]
    public void NormalMode_RealProcess_UnusableInput_PrintsWaiting_ExitZero()
    {
        using var t = new TempDir();
        Result r = RunExe(t.Path, Bytes("}{"));
        Assert.Equal(0, r.Exit);
        Assert.Equal("QuotaDuck | waiting for first report\n", Encoding.UTF8.GetString(r.Stdout));
        Result empty = RunExe(t.Path, []);
        Assert.Equal("QuotaDuck | waiting for first report\n", Encoding.UTF8.GetString(empty.Stdout));
    }

    [Fact]
    public void StateDirectory_RealProcess_IsUserOnlyProtected()
    {
        using var t = new TempDir();
        RunExe(t.Path, Bytes(LivePayload(5, 6)), "--tap");
        DirectorySecurity sec = new DirectoryInfo(Path.Combine(t.Path, "QuotaDuck", "bridge")).GetAccessControl();
        Assert.True(sec.AreAccessRulesProtected);
        FileSystemAccessRule rule = Assert.Single(sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
        Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
    }

    [Fact]
    public void UnwritableLocation_RealProcess_FailsQuietly_ExitZero()
    {
        using var t = new TempDir();
        string blocker = Path.Combine(t.Path, "afile");
        File.WriteAllText(blocker, "I am a file, not a directory");
        Result r = RunExe(blocker, Bytes(LivePayload(5, 6)));                  // LOCALAPPDATA points at a file
        Assert.Equal(0, r.Exit);
        Assert.Empty(r.Stderr);
        Assert.Equal("I am a file, not a directory", File.ReadAllText(blocker));
    }

    [Fact]
    public async Task SixSimultaneousProcesses_NeverCorruptTheState_AndConvergeOnTheMaximum()
    {
        using var t = new TempDir();
        double[] values = [11, 23.5, 7, 48.25, 31, 19];
        Result[] results = await Task.WhenAll(values.Select(v => Task.Run(() => RunExe(t.Path, Bytes(Payload(Win(v, DateTimeOffset.UtcNow.AddHours(3)), null)), "--tap"))));
        Assert.All(results, x => Assert.Equal(0, x.Exit));
        ClaudeStateDecodeResult s = ClaudeStateCodec.Decode(File.ReadAllBytes(StatePath(t.Path)));
        Assert.Equal(ClaudeStateDecodeStatus.Ok, s.Status);
        Assert.Equal(values.Max(), s.State.FiveHour!.UsedPercent);
        string[] files = Directory.GetFiles(Path.GetDirectoryName(StatePath(t.Path))!).Select(f => Path.GetFileName(f)!).ToArray();
        Assert.Equal([FileClaudeStateStore.FileName], files);
    }
}
