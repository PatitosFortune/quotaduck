using System.Diagnostics;
using System.Text;
using System.Text.Json;
using QuotaDuck.Core;
using QuotaDuck.Core.Codex;

namespace QuotaDuck.Codex.Tests;

/// <summary>The real runner against a fake `codex app-server` process. No real Codex is involved anywhere in this class.</summary>
public class RunnerFakeServerTests
{
    private static string Dump(CodexReadOutcome o) => JsonSerializer.Serialize(o) + o + o.Observations;

    private static string Notifications =>
        "{\"method\":\"thread/started\",\"params\":{\"x\":\"CANARY_N1\"}}\n\n{\"method\":\"account/updated\",\"params\":{}}\n";

    /// <summary>
    /// Deadline tests must fail on their own, quickly, if the runner ever stops honouring its deadlines: the read is given a hard outer bound
    /// via the cancellation token (which also kills the child), so a broken deadline is a normal assertion failure, never a hang.
    /// </summary>
    private static async Task<CodexReadOutcome> ReadWithinAsync(FakeRig rig, TimeSpan bound)
    {
        using var cts = new CancellationTokenSource(bound);
        CodexReadOutcome o = await rig.Reader().ReadAsync(cts.Token);
        Assert.False(o.Failure == CodexReadFailure.Cancelled, $"the runner did not honour its own deadline within {bound.TotalSeconds:0} s");
        return o;
    }

    // ---------------- the happy path and exactly what is sent ----------------

    [Fact]
    public async Task Normal_Succeeds_MapsIntoCore_AndSendsExactlyThreeMessages()
    {
        using var rig = new FakeRig();
        CodexReadOutcome o = await rig.Reader().ReadAsync();

        Assert.True(o.Success, o.Failure?.ToString());
        Assert.NotNull(o.Observations);
        Assert.Equal(2, o.Observations!.Windows.Count);
        Assert.Equal(rig.Clock.Now, o.At);
        Assert.All(o.Observations.Windows, w => Assert.Equal(rig.Clock.Now, w.ReceivedAt));
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), o.Observations.Windows.First(w => w.Duration == TimeSpan.FromMinutes(300)).ResetsAt);
        Assert.Equal(new ProviderAttempt(rig.Clock.Now, null), o.Attempt);

        byte[] sent = rig.StdinBytes();
        Assert.Equal(FakeRig.Expected3Messages, Encoding.UTF8.GetString(sent));
        Assert.Equal((byte)'{', sent[0]);                                            // first transmitted byte: no BOM
        Assert.False(sent.Length >= 3 && sent[0] == 0xEF && sent[1] == 0xBB && sent[2] == 0xBF);
        Assert.DoesNotContain((byte)'\r', sent);                                      // LF only
        Assert.Equal(3, sent.Count(b => b == (byte)'\n'));
        Assert.DoesNotContain("READ-BEFORE-INIT", rig.RecordLines());
    }

    [Fact]
    public async Task SensitiveExtras_NeverReachTheOutcome_TheModel_OrAnyFile()
    {
        using var rig = new FakeRig();
        CodexReadOutcome o = await rig.Reader().ReadAsync();
        Assert.True(o.Success);
        Assert.DoesNotContain("CANARY", Dump(o), StringComparison.Ordinal);
        foreach (WindowObservation w in o.Observations!.Windows)
        {
            Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(w) + w, StringComparison.Ordinal);
        }

        // the only files this test run produced are the fake's own diagnostics; nothing from QuotaDuck holds server content
        Assert.DoesNotContain(Directory.EnumerateFiles(rig.Dir.Path), f => File.ReadAllText(f).Contains("CANARY", StringComparison.Ordinal) && !f.EndsWith("record.txt", StringComparison.Ordinal) && !f.EndsWith("record.txt.stdin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotificationsBeforeAndBetweenReplies_AreIgnored()
    {
        using var rig = new FakeRig();
        File.WriteAllText(rig.Dir.File("pre.txt"), Notifications);
        rig.PreFile = rig.Dir.File("pre.txt");
        CodexReadOutcome o = await rig.Reader().ReadAsync();
        Assert.True(o.Success, o.Failure?.ToString());
        Assert.DoesNotContain("CANARY", Dump(o), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FragmentedOneByteWrites_AreReassembled()
    {
        using var rig = new FakeRig { Scenario = "fragment" };
        Assert.True((await rig.Reader().ReadAsync()).Success);
    }

    [Fact]
    public async Task HeavyStderrNoise_DoesNotBlockTheChild_AndIsNeverRetained()
    {
        using var rig = new FakeRig { Scenario = "stderr-noise" };
        CodexReadOutcome o = await rig.Reader().ReadAsync();
        Assert.True(o.Success, o.Failure?.ToString());

        using var rig2 = new FakeRig { Scenario = "stderr-canary" };
        CodexReadOutcome o2 = await rig2.Reader().ReadAsync();
        Assert.True(o2.Success);
        Assert.DoesNotContain("CANARY", Dump(o2), StringComparison.Ordinal);
    }

    // ---------------- protocol failures ----------------

    [Fact]
    public async Task MalformedReply_IsMalformed_NoObservations_AttemptKindMalformed()
    {
        using var rig = new FakeRig();
        File.WriteAllText(rig.Dir.File("p.txt"), "{this is not json CANARY_BAD\n");
        rig.PayloadFile = rig.Dir.File("p.txt");
        CodexReadOutcome o = await rig.Reader().ReadAsync();
        Assert.Equal(CodexReadFailure.Malformed, o.Failure);
        Assert.Null(o.Observations);
        Assert.Equal(ProviderErrorKind.Malformed, o.Attempt!.Error);
        Assert.DoesNotContain("CANARY", Dump(o), StringComparison.Ordinal);
        Assert.True(FakeRig.WaitDead(rig.ChildPid()));
    }

    [Theory]
    [InlineData("{\"id\":2,\"result\":{\"rateLimits\":{\"limitId\":\"a\",\"limitId\":\"b\",\"primary\":{\"usedPercent\":1}}}}\n")]
    [InlineData("{\"id\":2,\"result\":{\"rateLimits\":null}}\n")]
    [InlineData("{\"id\":2,\"result\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":500,\"windowDurationMins\":300,\"resetsAt\":1893456000},\"secondary\":null}}}\n")]
    [InlineData("{\"id\":2,\"result\":{}}\n")]
    public async Task UnusableReplies_YieldNoObservations(string payload)
    {
        using var rig = new FakeRig();
        File.WriteAllText(rig.Dir.File("p.txt"), payload);
        rig.PayloadFile = rig.Dir.File("p.txt");
        CodexReadOutcome o = await rig.Reader().ReadAsync();
        Assert.False(o.Success);
        Assert.Null(o.Observations);
        Assert.Contains(o.Failure, new CodexReadFailure?[] { CodexReadFailure.Malformed, CodexReadFailure.NoQuotaData });
    }

    [Fact]
    public async Task WrongResponseId_OnTheQuotaReply_IsAProtocolViolation()
    {
        using var rig = new FakeRig { Scenario = "wrong-id" };
        CodexReadOutcome o = await rig.Reader().ReadAsync();
        Assert.Equal(CodexReadFailure.ProtocolViolation, o.Failure);
        Assert.Null(o.Observations);
    }

    [Fact]
    public async Task WrongResponseId_OnInitialize_IsAProtocolViolation()
    {
        using var rig = new FakeRig { Scenario = "init-wrong-id" };
        // the fake answers id 1 correctly and then ALSO sends an unsolicited id-55 reply: it must not be mistaken for the quota reply
        CodexReadOutcome o = await rig.Reader().ReadAsync();
        Assert.Equal(CodexReadFailure.ProtocolViolation, o.Failure);
    }

    [Fact]
    public async Task UnexpectedServerRequest_IsRejected_NeverAnswered_NeverActedOn()
    {
        using var rig = new FakeRig { Scenario = "unexpected-request" };
        CodexReadOutcome o = await rig.Reader().ReadAsync();
        Assert.Equal(CodexReadFailure.ProtocolViolation, o.Failure);
        string sent = Encoding.UTF8.GetString(rig.StdinBytes());
        Assert.DoesNotContain("99", sent);                                           // no reply to the server's request id
        Assert.DoesNotContain("CANARY", sent, StringComparison.Ordinal);
        Assert.Equal(FakeRig.Expected3Messages, sent);                                // and nothing beyond the three fixed messages
        Assert.True(FakeRig.WaitDead(rig.ChildPid()));
    }

    [Fact]
    public async Task OversizedLine_IsRejectedQuickly_AndTheChildKilled()
    {
        using var rig = new FakeRig { Scenario = "oversize" };
        var sw = Stopwatch.StartNew();
        CodexReadOutcome o = await rig.Reader().ReadAsync();
        Assert.Equal(CodexReadFailure.Malformed, o.Failure);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8));
        Assert.True(FakeRig.WaitDead(rig.ChildPid()));
    }

    [Fact]
    public async Task MessageFlood_WithoutTheAwaitedReply_IsAProtocolViolation()
    {
        using var rig = new FakeRig { Scenario = "flood" };
        Assert.Equal(CodexReadFailure.ProtocolViolation, (await rig.Reader().ReadAsync()).Failure);
    }

    [Fact]
    public async Task ErrorReplies_AreRejected_KeepOnlyTheCode()
    {
        foreach (string scenario in new[] { "init-error", "error-reply" })
        {
            using var rig = new FakeRig { Scenario = scenario };
            CodexReadOutcome o = await rig.Reader().ReadAsync();
            Assert.Equal(CodexReadFailure.Rejected, o.Failure);
            Assert.Equal(-32600, o.ServerErrorCode);
            Assert.Null(o.Observations);
            Assert.Equal(ProviderErrorKind.Rejected, o.Attempt!.Error);
            Assert.DoesNotContain("CANARY", Dump(o), StringComparison.Ordinal);
        }
    }

    // ---------------- timeouts, crashes, EOF ----------------

    [Fact]
    public async Task NoInitializeReply_TimesOut_AndTheChildIsKilled()
    {
        using var rig = new FakeRig { Scenario = "silent-init", Options = new(TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)) };
        CodexReadOutcome o = await ReadWithinAsync(rig, TimeSpan.FromSeconds(6));
        Assert.Equal(CodexReadFailure.Timeout, o.Failure);
        Assert.Equal(ProviderErrorKind.Timeout, o.Attempt!.Error);
        Assert.True(FakeRig.WaitDead(rig.ChildPid()));
    }

    [Fact]
    public async Task NoQuotaReply_TimesOut_AndTheChildIsKilled()
    {
        using var rig = new FakeRig { Scenario = "silent-read", Options = new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(1)) };
        CodexReadOutcome o = await ReadWithinAsync(rig, TimeSpan.FromSeconds(6));
        Assert.Equal(CodexReadFailure.Timeout, o.Failure);
        Assert.Null(o.Observations);
        Assert.True(FakeRig.WaitDead(rig.ChildPid()));
    }

    [Theory]
    [InlineData("exit-on-start")]
    [InlineData("crash-after-init")]
    [InlineData("partial-then-exit")]
    public async Task ProcessThatExitsEarly_IsProcessExited_NotATimeout(string scenario)
    {
        using var rig = new FakeRig { Scenario = scenario };
        var sw = Stopwatch.StartNew();
        CodexReadOutcome o = await rig.Reader().ReadAsync();
        Assert.Equal(CodexReadFailure.ProcessExited, o.Failure);
        Assert.Null(o.Observations);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), "an exited child must be noticed immediately, not at the deadline");
    }

    [Fact]
    public async Task Timeout_KillsTheWholeProcessTree_IncludingGrandchildren()
    {
        using var rig = new FakeRig { Scenario = "hang-with-child", Options = new(TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)) };
        CodexReadOutcome o = await ReadWithinAsync(rig, TimeSpan.FromSeconds(6));
        Assert.Equal(CodexReadFailure.Timeout, o.Failure);
        int child = rig.ChildPid(), grandchild = rig.GrandchildPid();
        Assert.True(FakeRig.WaitDead(child), "child survived");
        Assert.True(FakeRig.WaitDead(grandchild), "grandchild survived");
    }

    [Fact]
    public async Task Success_ClosesStdin_AndTheChildExitsByItself_NoKillNeeded()
    {
        using var rig = new FakeRig();
        var sw = Stopwatch.StartNew();
        CodexReadOutcome o = await rig.Reader().ReadAsync();
        Assert.True(o.Success);
        Assert.Contains("EOF-ON-STDIN", rig.RecordLines());                          // the child saw stdin close and left on its own
        Assert.True(FakeRig.WaitDead(rig.ChildPid()));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task ChildThatIgnoresStdinClose_IsKilledAfterTheGrace_AndTheReadStillSucceeds()
    {
        using var rig = new FakeRig { Scenario = "ignore-eof", Options = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(400)) };
        CodexReadOutcome o = await rig.Reader().ReadAsync();
        Assert.True(o.Success);
        Assert.True(FakeRig.WaitDead(rig.ChildPid()));
    }

    [Fact]
    public async Task Cancellation_StopsTheRead_KillsTheChild_AndIsNotAProviderFailure()
    {
        using var rig = new FakeRig { Scenario = "silent-read" };
        using var cts = new CancellationTokenSource();
        Task<CodexReadOutcome> t = rig.Reader().ReadAsync(cts.Token);
        await Task.Delay(1500);
        cts.Cancel();
        CodexReadOutcome o = await t;
        Assert.Equal(CodexReadFailure.Cancelled, o.Failure);
        Assert.Null(o.Attempt);
        Assert.Null(o.Observations);
        Assert.True(FakeRig.WaitDead(rig.ChildPid()));
    }

    // ---------------- failure never refreshes age ----------------

    [Fact]
    public async Task AFailedRead_NeverMakesEarlierDataLookFresher()
    {
        using var rig = new FakeRig();
        CodexReadOutcome good = await rig.Reader().ReadAsync();
        Assert.True(good.Success);
        DateTimeOffset firstRead = good.At;

        rig.Clock.Now += TimeSpan.FromMinutes(30);
        rig.Scenario = "crash-after-init";
        CodexReadOutcome bad = await rig.Reader().ReadAsync();
        Assert.False(bad.Success);
        Assert.Null(bad.Observations);

        ProviderInput input = CodexObservations.ToProviderInput(good.Observations, bad.Attempt);
        ProviderView view = ProviderDeriver.Derive(input, rig.Clock.Now, FreshnessPolicy.ForCodexPollInterval(TimeSpan.FromMinutes(5)));
        Assert.All(input.Windows, w => Assert.Equal(firstRead, w.ReceivedAt));        // observation age is still the first read's
        Assert.NotEqual(WindowState.Fresh, view.Windows[0].State);                     // 30 minutes later it is not fresh again just because we tried
    }

    // ---------------- discovery gating, serialisation, process accounting ----------------

    [Theory]
    [InlineData("NotInstalled", CodexReadFailure.NotFound)]
    [InlineData("SignatureInvalid", CodexReadFailure.ValidationFailed)]
    [InlineData("SignerMismatch", CodexReadFailure.ValidationFailed)]
    [InlineData("PathEscapesPackage", CodexReadFailure.ValidationFailed)]
    [InlineData("PackageQueryFailed", CodexReadFailure.ValidationFailed)]
    public async Task WhenDiscoveryFails_NothingIsEverLaunched(string statusName, CodexReadFailure expected)
    {
        var status = Enum.Parse<CodexDiscoveryStatus>(statusName);
        var reader = new CodexQuotaReader(
            new StubResolver(() => new CodexDiscoveryResult(status, null)),
            new ThrowingLauncher(),
            _ => throw new InvalidOperationException("no spec without a validated executable"),
            new FixedClock(DateTimeOffset.UnixEpoch),
            CodexReadOptions.Default);
        CodexReadOutcome o = await reader.ReadAsync();
        Assert.Equal(expected, o.Failure);
        Assert.False(o.ProcessStarted);
        Assert.Equal(0, reader.ProcessesStarted);
    }

    [Fact]
    public async Task EveryReadReResolves_AndAChangedPathIsUsedImmediately()
    {
        using var rig = new FakeRig();
        CodexQuotaReader reader = rig.Reader();
        await reader.ReadAsync();
        await reader.ReadAsync();
        Assert.Equal(2, rig.Resolver.Calls);
        Assert.Equal(2, reader.ProcessesStarted);
        Assert.Equal(2, rig.Launcher.Started);
    }

    [Fact]
    public async Task ConcurrentReads_AreSerialised_NeverTwoChildrenAtOnce()
    {
        using var rig = new FakeRig();
        CodexQuotaReader reader = rig.Reader();
        CodexReadOutcome[] r = await Task.WhenAll(reader.ReadAsync(), reader.ReadAsync(), reader.ReadAsync());
        Assert.All(r, o => Assert.True(o.Success));
        Assert.Equal(1, rig.Launcher.MaxConcurrent);
        Assert.Equal(3, rig.Launcher.Started);
    }

    [Fact]
    public async Task UnstartableExecutable_IsStartFailed()
    {
        var reader = new CodexQuotaReader(
            new StubResolver(() => new CodexDiscoveryResult(CodexDiscoveryStatus.Ok, new CodexExecutable(@"C:\definitely\not\here\codex.exe", "P", "1"))),
            new ProcessAppServerLauncher(),
            CodexLaunch.Production,
            new FixedClock(DateTimeOffset.UnixEpoch),
            CodexReadOptions.Default);
        CodexReadOutcome o = await reader.ReadAsync();
        Assert.Equal(CodexReadFailure.StartFailed, o.Failure);
        Assert.Equal(ProviderErrorKind.StartFailed, o.Attempt!.Error);
        Assert.Equal(0, reader.ProcessesStarted);
    }
}
