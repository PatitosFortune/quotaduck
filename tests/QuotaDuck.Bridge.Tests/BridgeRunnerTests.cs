using System.Text;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;
using static QuotaDuck.Core.Tests.PayloadFx;
using static QuotaDuck.Bridge.Tests.Harness;

namespace QuotaDuck.Bridge.Tests;

public class BridgeRunnerTests
{
    // ================= modes & arguments =================

    [Fact]
    public void TapMode_EmitsZeroBytes_ForEveryKindOfInput_YetStillUpdatesStateWhenValid()
    {
        using var h = new Harness();
        byte[] invalidUtf8 = [.. Bytes("{\"x\":\""), 0xFF, .. Bytes("\"}")];
        byte[][] inputs =
        [
            Bytes(Both(23.5, 41.2)),                                   // valid
            Bytes("{{{ not json"),                                     // malformed
            Array.Empty<byte>(),                                       // empty
            Bytes(Payload(null, null)),                                // valid but nothing usable
            invalidUtf8,                                               // invalid UTF-8
            new byte[400_000],                                         // oversized
        ];
        foreach (byte[] input in inputs)
        {
            (int exit, byte[] stdout) = h.Run(input, "--tap");
            Assert.Equal(0, exit);
            Assert.Empty(stdout);                                      // tap = zero bytes, always
        }

        ClaudeState s = h.State();
        Assert.Equal(23.5, s.FiveHour!.UsedPercent);
        Assert.Equal(41.2, s.SevenDay!.UsedPercent);
    }

    [Fact]
    public void TapMode_WithNothingUsable_DoesNotEvenTouchTheDisk()
    {
        using var h = new Harness();
        h.Run(Payload(null, null), "--tap");
        h.Run("garbage", "--tap");
        Assert.False(Directory.Exists(h.StateDir));
        Assert.Equal(0, h.Writes);
    }

    [Fact]
    public void NormalMode_PrintsExactlyOneShortLine_FromTheMergedState()
    {
        using var h = new Harness();
        (int exit, byte[] stdout) = h.Run(Both(23.5, 41.2));
        Assert.Equal(0, exit);
        Assert.Equal("QuotaDuck | 5h 23.5% used | wk 41.2% used\n", Encoding.UTF8.GetString(stdout));
        Assert.Equal(1, stdout.Count(b => b == (byte)'\n'));
        Assert.All(stdout, b => Assert.True(b is >= 0x20 and <= 0x7E or (byte)'\n'));
    }

    [Fact]
    public void NormalMode_CustomLabel()
    {
        using var h = new Harness();
        Assert.Equal("Duck-1 | 5h 23.5% used | wk 41.2% used", h.RunText(Both(23.5, 41.2), "--label=Duck-1"));
    }

    [Fact]
    public void NormalMode_NothingKnownYet_SaysWaiting_NeverZero()
    {
        using var h = new Harness();
        Assert.Equal("QuotaDuck | waiting for first report", h.RunText("{}"));
        Assert.Equal("QuotaDuck | waiting for first report", h.RunText("garbage"));
        Assert.Equal("QuotaDuck | waiting for first report", h.RunText(""));
    }

    [Fact]
    public void NormalMode_NoStoreAvailable_StillPrintsAndExitsZero()
    {
        var stdout = new MemoryStream();
        int exit = BridgeRunner.Run([], new MemoryStream(Bytes(Both(1, 2))), stdout, null, () => Epoch);
        Assert.Equal(0, exit);
        Assert.Equal("QuotaDuck | waiting for first report\n", Encoding.UTF8.GetString(stdout.ToArray()));
    }

    public static TheoryData<string[]> BadArguments => new()
    {
        new[] { "--bogus" },
        new[] { "-t" },
        new[] { "tap" },
        new[] { "--tap", "--tap" },
        new[] { "--label=a", "--label=b" },
        new[] { "--label=" },
        new[] { "--label=bad|x" },
        new[] { "--label=\u001b[31m" },
        new[] { "--label=1234567890123456789012345" },
        new[] { "--tap", "extra" },
        new[] { "--label" },
        new[] { "--TAP" },
        // there is no way to choose where state lives: every plausible override spelling is just an unknown argument
        new[] { "--state-dir=C:\\evil" },
        new[] { "--state-dir", "C:\\evil" },
        new[] { "--state=C:\\evil\\claude-v1.json" },
        new[] { "--dir=C:\\evil" },
        new[] { "--path=C:\\evil" },
        new[] { "--localappdata=C:\\evil" },
        new[] { "--output=C:\\evil\\x.json" },
        new[] { "C:\\evil" },
        new[] { "--tap", "--state-dir=C:\\evil" },
    };

    [Theory]
    [MemberData(nameof(BadArguments))]
    public void InvalidArguments_ExitTwo_ReadNothing_WriteNothing(string[] args)
    {
        using var h = new Harness();
        var stdin = new ExplodingStream();
        var stdout = new MemoryStream();
        int exit = BridgeRunner.Run(args, stdin, stdout, h.Store, () => Epoch);
        Assert.Equal(2, exit);
        Assert.False(stdin.ReadAttempted);
        Assert.Empty(stdout.ToArray());
        Assert.False(Directory.Exists(h.StateDir));
    }

    [Fact]
    public void TapAndLabelTogether_AreAccepted()
    {
        using var h = new Harness();
        (int exit, byte[] stdout) = h.Run(Both(10, 20), "--tap", "--label=X");
        Assert.Equal(0, exit);
        Assert.Empty(stdout);
    }

    // ================= windows present / absent =================

    [Fact]
    public void FiveHourOnly_StoresFiveHour_WeeklyStaysNull()
    {
        using var h = new Harness();
        h.Run(Payload(Five(12.5), null), "--tap");
        ClaudeState s = h.State();
        Assert.Equal(12.5, s.FiveHour!.UsedPercent);
        Assert.Null(s.SevenDay);
        Assert.Equal("QuotaDuck | 5h 12.5% used | wk --", h.RunText(Payload(Five(12.5), null)));
    }

    [Fact]
    public void WeeklyOnly_StoresWeekly_FiveHourStaysNull()
    {
        using var h = new Harness();
        h.Run(Payload(null, Seven(30)), "--tap");
        ClaudeState s = h.State();
        Assert.Null(s.FiveHour);
        Assert.Equal(30, s.SevenDay!.UsedPercent);
        Assert.Equal("QuotaDuck | 5h -- | wk 30% used", h.RunText(Payload(null, Seven(30))));
    }

    [Fact]
    public void BothWindows_AreStoredIndependently_AndTheSecondPayloadCanAddTheOther()
    {
        using var h = new Harness();
        h.Run(Payload(Five(10), null), "--tap");
        h.Now = Epoch.AddMinutes(1);
        h.Run(Payload(null, Seven(20)), "--tap");
        ClaudeState s = h.State();
        Assert.Equal(10, s.FiveHour!.UsedPercent);
        Assert.Equal(20, s.SevenDay!.UsedPercent);
        Assert.Equal(Epoch, s.FiveHour.ChangedAt);                     // the weekly-only payload did not touch five_hour
        Assert.Equal(Epoch, s.FiveHour.ReceivedAt);
    }

    [Fact]
    public void NeitherWindow_LeavesExistingStateByteForByteUntouched()
    {
        using var h = new Harness();
        h.Run(Both(10, 20), "--tap");
        byte[] before = h.FileBytes()!;
        int writes = h.Writes;
        h.Now = Epoch.AddHours(1);
        foreach (string p in new[] { "{}", Payload(null, null), "{\"rate_limits\":null}", "{\"rate_limits\":{\"five_hour\":null}}" })
        {
            h.Run(p, "--tap");
            h.Run(p);
        }

        Assert.Equal(before, h.FileBytes());
        Assert.Equal(writes, h.Writes);
    }

    // ================= nothing but the allow-list is ever retained =================

    [Fact]
    public void Canary_NoUnknownFieldValueReachesTheStateFile_TheOutput_OrAnyFile()
    {
        using var h = new Harness();
        string payload = Payload(Five(23.5), Seven(41.2), extras: true,
            rateExtras: "\"future_window\":{\"used_percentage\":77,\"note\":\"" + Canary + "-fw\"}",
            rootExtras: "\"prompt\":\"" + Canary + "-prompt\",\"response\":\"" + Canary + "-resp\",\"account\":{\"email\":\"" + Canary + "@example.invalid\"},\"deep\":" + Nested(20));
        (int exit, byte[] stdout) = h.Run(payload);
        Assert.Equal(0, exit);
        Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(stdout));

        foreach (string f in Directory.EnumerateFiles(h.Root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(Canary, File.ReadAllText(f));
            Assert.DoesNotContain("synthetic", File.ReadAllText(f));          // paths from cwd/transcript_path/workspace
        }

        // exactly one file exists, and it is the canonical encoding of just the two allow-listed windows
        Assert.Equal([FileClaudeStateStore.FileName], h.FilesInStateDir());
        ClaudeState s = h.State();
        Assert.Equal(new ClaudeState(s.FiveHour, s.SevenDay), s);
        Assert.Equal(ClaudeStateCodec.Encode(s), h.FileBytes());
        Assert.Equal(23.5, s.FiveHour!.UsedPercent);                         // and the decoys (context_window 8, spend_limit 1) were not read
        Assert.Equal(41.2, s.SevenDay!.UsedPercent);
    }

    [Fact]
    public void Canary_InRejectedPayloads_NeverLeaks_EitherWayItIsRejected()
    {
        using var h = new Harness();
        string[] rejected =
        [
            Payload(Five(23.5), Seven(41.2)).Replace("}}", "}") + Canary,                                   // malformed (truncated + trailing)
            Payload(Win("999", "1893456000"), null, rootExtras: "\"leak\":\"" + Canary + "\""),             // invalid percent
            Payload(Five(23.5), null, rootExtras: "\"deep\":" + Nested(80) + ",\"leak\":\"" + Canary + "\""), // too deep
        ];
        foreach (string p in rejected)
        {
            Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(h.Run(p).Stdout));
        }

        foreach (string f in Directory.Exists(h.Root) ? Directory.EnumerateFiles(h.Root, "*", SearchOption.AllDirectories) : [])
        {
            Assert.DoesNotContain(Canary, File.ReadAllText(f));
        }
    }

    // ================= parser-level rejections leave state alone =================

    [Fact]
    public void DeepNesting_InUnknownFields_WithinLimit_IsAccepted_BeyondItRejectsTheWholePayload()
    {
        using var h = new Harness();
        h.Run(Payload(Five(10), null, rootExtras: "\"deep\":" + Nested(30)), "--tap");
        Assert.Equal(10, h.State().FiveHour!.UsedPercent);

        byte[] before = h.FileBytes()!;
        h.Now = Epoch.AddMinutes(30);
        h.Run(Payload(Five(55), Seven(66), rootExtras: "\"deep\":" + Nested(100)), "--tap");
        Assert.Equal(before, h.FileBytes());                              // too deep => nothing from it is trusted
    }

    [Fact]
    public void MalformedAndTruncatedPayloads_NeverCorruptOrChangeExistingState()
    {
        using var h = new Harness();
        h.Run(Both(10, 20), "--tap");
        byte[] before = h.FileBytes()!;
        int writes = h.Writes;
        h.Now = Epoch.AddMinutes(30);

        byte[] good = Bytes(Both(77, 88));
        for (int len = 0; len < good.Length; len += 7)                    // every 7th prefix, incl. 0
        {
            (int exit, _) = h.Run(good.AsSpan(0, len).ToArray(), "--tap");
            Assert.Equal(0, exit);
            Assert.Equal(before, h.FileBytes());
        }

        foreach (string bad in new[] { "{", "[]", "null", "{\"a\":}", "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":1,}}}", "{}{}", "\0\0\0" })
        {
            h.Run(bad, "--tap");
            Assert.Equal(before, h.FileBytes());
        }

        Assert.Equal(writes, h.Writes);
        Assert.Equal([FileClaudeStateStore.FileName], h.FilesInStateDir());
    }

    [Fact]
    public void OversizedStdin_IsBoundedRead_DrainedForTheCaller_AndRejected()
    {
        using var h = new Harness();
        h.Run(Both(10, 20), "--tap");
        byte[] before = h.FileBytes()!;

        var stdin = new EndlessStream(50L * 1024 * 1024);                   // 50 MB of input
        using var stdout = new MemoryStream();
        int exit = BridgeRunner.Run(["--tap"], stdin, stdout, h.Store, () => Epoch.AddMinutes(1));
        Assert.Equal(0, exit);
        Assert.Equal(before, h.FileBytes());
        // we buffer at most the cap + 1 byte; the rest is read-and-discarded only up to a fixed drain limit (4 MiB)
        Assert.InRange(stdin.BytesRead, ClaudeStatusLineParser.MaxPayloadBytes, ClaudeStatusLineParser.MaxPayloadBytes + 1 + 4L * 1024 * 1024 + 16 * 1024);
        Assert.True(stdin.BytesRead < 50L * 1024 * 1024);
    }

    [Fact]
    public void ExactlyAtTheSizeLimit_IsAccepted()
    {
        using var h = new Harness();
        string valid = Payload(Five(33), null, extras: false);
        byte[] atLimit = new byte[ClaudeStatusLineParser.MaxPayloadBytes];
        Encoding.UTF8.GetBytes(valid).CopyTo(atLimit, 0);
        Array.Fill(atLimit, (byte)' ', valid.Length, atLimit.Length - valid.Length);
        h.Run(atLimit, "--tap");
        Assert.Equal(33, h.State().FiveHour!.UsedPercent);
    }

    [Fact]
    public void InvalidUtf8_AnywhereInThePayload_RejectsIt()
    {
        using var h = new Harness();
        byte[] bad = [.. Bytes("{\"note\":\"x"), 0xC0, 0x80, .. Bytes("\",\"rate_limits\":{\"five_hour\":" + Five(50) + "}}")];
        h.Run(bad, "--tap");
        Assert.False(File.Exists(h.StateFile));
    }

    // ================= semantic (clock-relative) rejections =================

    [Theory]
    [InlineData("-0.5")]
    [InlineData("100.5")]
    [InlineData("\"50\"")]
    [InlineData("null")]
    [InlineData(null)]
    public void InvalidOrMissingPercent_IsRejected_TheOtherWindowStillStored(string? rawPct)
    {
        using var h = new Harness();
        h.Run(Payload(Win(rawPct, Unix(Epoch.AddHours(3)).ToString()), Seven(40)), "--tap");
        ClaudeState s = h.State();
        Assert.Null(s.FiveHour);
        Assert.Equal(40, s.SevenDay!.UsedPercent);
    }

    [Fact]
    public void MissingResetTimestamp_IsAccepted_ResetUnknown()
    {
        using var h = new Harness();
        h.Run(Payload(Win("12", null), null), "--tap");
        ClaudeWindowRecord r = h.State().FiveHour!;
        Assert.Equal(12, r.UsedPercent);
        Assert.Null(r.ResetsAt);
    }

    [Theory]
    [InlineData("\"1893456000\"")]
    [InlineData("1893456000.5")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("99999999999")]
    public void InvalidResetTimestamp_RejectsThatWindow(string rawReset)
    {
        using var h = new Harness();
        h.Run(Payload(Win("12", rawReset), Seven(40)), "--tap");
        ClaudeState s = h.State();
        Assert.Null(s.FiveHour);
        Assert.NotNull(s.SevenDay);
    }

    [Fact]
    public void ImplausibleResetTimestamps_AreRejectedByTheCoreMerge_PolicyBounds()
    {
        using var h = new Harness();
        // five-hour window: reset 48 h ahead is implausible; reset 1 h in the past is stale
        h.Run(Payload(Win(50, Epoch.AddHours(48)), Seven(10)), "--tap");
        Assert.Null(h.State().FiveHour);
        h.Run(Payload(Win(50, Epoch.AddHours(-1)), null), "--tap");
        Assert.Null(h.State().FiveHour);
        // boundary: exactly length + tolerance (5 h + 10 min) is accepted
        h.Run(Payload(Win(50, Epoch.AddHours(5).AddMinutes(10)), null), "--tap");
        Assert.Equal(50, h.State().FiveHour!.UsedPercent);
    }

    // ================= unchanged readings: coalesced, never fake ChangedAt =================

    [Fact]
    public void RepeatedUnchangedReadings_AreCoalesced_AndNeverMoveChangedAt()
    {
        using var h = new Harness();
        string p = Both(40, 50);
        h.Run(p, "--tap");
        Assert.Equal(1, h.Writes);
        byte[] first = h.FileBytes()!;

        foreach (int seconds in new[] { 1, 10, 30, 59 })                         // all within the 60 s coalescing window
        {
            h.Now = Epoch.AddSeconds(seconds);
            h.Run(p, "--tap");
        }

        Assert.Equal(1, h.Writes);                                              // no rewrite at all
        Assert.Equal(first, h.FileBytes());

        h.Now = Epoch.AddSeconds(60);                                           // the coalescing interval elapses
        h.Run(p, "--tap");
        Assert.Equal(2, h.Writes);
        ClaudeWindowRecord r = h.State().FiveHour!;
        Assert.Equal(Epoch, r.ChangedAt);                                       // NEVER faked
        Assert.Equal(Epoch.AddSeconds(60), r.ReceivedAt);

        h.Now = Epoch.AddSeconds(100);                                          // 40 s after the last persisted ReceivedAt
        h.Run(p, "--tap");
        Assert.Equal(2, h.Writes);

        // an hour of unchanged reports every minute: ChangedAt is still the very first instant
        for (int m = 2; m <= 60; m++)
        {
            h.Now = Epoch.AddMinutes(m);
            h.Run(p, "--tap");
        }

        Assert.Equal(Epoch, h.State().FiveHour!.ChangedAt);
        Assert.Equal(Epoch.AddHours(1), h.State().FiveHour!.ReceivedAt);
        Assert.InRange(h.Writes, 2, 2 + 59);                                    // at most one write per minute, never one per run
    }

    [Fact]
    public void BridgeKeepsRunning_DataNeverChanges_ThePrintedLineDecaysInsteadOfStayingFresh()
    {
        using var h = new Harness();
        string p = Both(40, 50);
        h.Run(p, "--tap");
        h.Now = Epoch.AddMinutes(5);
        Assert.Equal("QuotaDuck | 5h 40% used | wk 50% used", h.RunText(p));
        h.Now = Epoch.AddHours(2).AddMinutes(30);                                // hook ran again just now, but data is 2.5 h old
        Assert.Equal("QuotaDuck | 5h 40% used (old) | wk 50% used (old)", h.RunText(p));
    }

    // ================= lower-reading rule end to end =================

    [Fact]
    public void LowerReadings_FirstAndSecondIgnored_ThirdIdenticalAccepted()
    {
        using var h = new Harness();
        h.Run(Payload(Five(60), null), "--tap");
        for (int i = 1; i <= 2; i++)
        {
            h.Now = Epoch.AddMinutes(i);
            h.Run(Payload(Five(50), null), "--tap");
            Assert.Equal(60, h.State().FiveHour!.UsedPercent);
        }

        h.Now = Epoch.AddMinutes(3);
        h.Run(Payload(Five(50), null), "--tap");
        ClaudeWindowRecord r = h.State().FiveHour!;
        Assert.Equal(50, r.UsedPercent);
        Assert.Equal(Epoch.AddMinutes(3), r.ChangedAt);
    }

    [Fact]
    public void LowerReadings_DifferingValuesResetTheSequence()
    {
        using var h = new Harness();
        h.Run(Payload(Five(60), null), "--tap");
        int minute = 1;
        foreach (double v in new[] { 50.0, 50, 45, 45 })
        {
            h.Now = Epoch.AddMinutes(minute++);
            h.Run(Payload(Five(v), null), "--tap");
            Assert.Equal(60, h.State().FiveHour!.UsedPercent);                  // still no acceptance (50,50 then 45,45)
        }

        h.Now = Epoch.AddMinutes(minute);
        h.Run(Payload(Five(45), null), "--tap");                                 // third identical 45
        Assert.Equal(45, h.State().FiveHour!.UsedPercent);
    }

    // ================= epochs =================

    [Fact]
    public void NewEpoch_ReplacesTheWindow_EvenWithALowerPercentage_AndClearsLowerTracking()
    {
        using var h = new Harness();
        h.Run(Payload(Five(80), null), "--tap");
        h.Now = Epoch.AddMinutes(1);
        h.Run(Payload(Five(70), null), "--tap");                                 // pending lower candidate
        Assert.Equal(1, h.State().FiveHour!.LowerStreak);

        h.Now = Epoch.AddHours(3).AddMinutes(1);                                 // old window has elapsed
        h.Run(Payload(Win(5, Epoch.AddHours(8)), null), "--tap");                // new reset time = new epoch
        ClaudeWindowRecord r = h.State().FiveHour!;
        Assert.Equal(5, r.UsedPercent);
        Assert.Equal(Epoch.AddHours(8), r.ResetsAt);
        Assert.Equal(h.Now, r.ChangedAt);
        Assert.Equal(0, r.LowerStreak);
        Assert.Null(r.LowerCandidate);
    }

    [Fact]
    public void ElapsedPriorWindow_IsShownAsReset_StalePayloadCannotResurrectIt_NewEpochRefillsIt()
    {
        using var h = new Harness();
        h.Run(Payload(Five(80), null), "--tap");
        h.Now = Epoch.AddHours(3).AddMinutes(30);                                // 30 min past the reset, no new report yet

        // another terminal that has not refreshed still emits the OLD window => rejected as "reset in the past"
        string line = h.RunText(Payload(Five(80), null));
        Assert.Equal("QuotaDuck | 5h reset | wk --", line);
        Assert.DoesNotContain("80", line);                                       // the obsolete number is not shown

        h.Run(Payload(Win(2, Epoch.AddHours(8)), null), "--tap");
        Assert.Equal("QuotaDuck | 5h 2% used | wk --", h.RunText(Payload(Win(2, Epoch.AddHours(8)), null)));
    }

    // ================= competing synthetic sessions =================

    [Fact]
    public void CompetingSessions_StaleSessionNeverDragsTheLiveValueDown_NorResurrectsAnOldEpoch()
    {
        using var h = new Harness();
        var rng = new Random(20300106);
        double truth = 5;
        DateTimeOffset epochReset = Epoch.AddHours(3);
        int consecutiveStale = 0;
        double staleValue = 0;
        DateTimeOffset staleReset = epochReset;

        int liveReports = 0, staleSent = 0;
        for (int step = 1; step <= 400; step++)
        {
            h.Now = Epoch.AddMinutes(step);
            if (h.Now >= epochReset)                                              // the live session rolls into the next window
            {
                epochReset = h.Now.AddHours(5);
                truth = 2;
                consecutiveStale = 0;
            }

            ClaudeWindowRecord? before = File.Exists(h.StateFile) ? h.State().FiveHour : null;
            bool stale = rng.Next(3) == 0 && consecutiveStale < 2;                // never 3 identical stale reports in a row
            if (stale)
            {
                consecutiveStale++;
                staleValue = Math.Max(0, truth - 3 - rng.Next(10));
                staleReset = rng.Next(4) == 0 ? epochReset.AddHours(-5) : epochReset;      // sometimes an older epoch
                if (staleReset > h.Now.AddMinutes(-2))
                {
                    staleSent++;
                    h.Run(Payload(Win(staleValue, staleReset), null), "--tap");
                }

                // a stale session can never lower (or change) the stored value within the same window epoch
                ClaudeWindowRecord? after = File.Exists(h.StateFile) ? h.State().FiveHour : null;
                if (before is not null && after is not null && after.ResetsAt == before.ResetsAt)
                {
                    Assert.Equal(before.UsedPercent, after.UsedPercent);
                    Assert.Equal(before.ChangedAt, after.ChangedAt);
                }
            }
            else
            {
                consecutiveStale = 0;
                liveReports++;
                truth = Math.Min(100, truth + rng.NextDouble());
                truth = Math.Round(truth, 2);
                h.Run(Payload(Win(truth, epochReset), null), "--tap");
                ClaudeWindowRecord r = h.State().FiveHour!;
                Assert.Equal(truth, r.UsedPercent);                               // the live report always wins
                Assert.Equal(epochReset, r.ResetsAt);
            }
        }

        Assert.True(liveReports > 150 && staleSent > 20, $"model exercised both paths (live={liveReports}, stale={staleSent})");
    }

    [Fact]
    public void CompetingSessions_RealThreads_SharedStateDirectory_NeverCorruptAndConvergeOnTheMaximum()
    {
        using var h = new Harness();
        string dir = h.StateDir;
        const int threads = 8, runsPerThread = 40;
        double globalMax = 0;
        object maxLock = new();
        int tick = 0;
        var errors = new List<Exception>();

        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            // each "terminal" has its OWN store instance (like a separate process) but they share the directory + named mutex
            var store = new FileClaudeStateStore(dir, new StoreOptions { LockTimeout = TimeSpan.FromSeconds(20) });
            for (int i = 0; i < runsPerThread; i++)
            {
                double pct = Math.Round(1 + i * 1.1 + t * 0.01, 3);                // unique per report, increasing per thread
                lock (maxLock)
                {
                    globalMax = Math.Max(globalMax, pct);
                }

                try
                {
                    using var stdin = new MemoryStream(Bytes(Payload(Win(pct, Epoch.AddHours(3)), null, extras: false)));
                    using var stdout = new MemoryStream();
                    BridgeRunner.Run(["--tap"], stdin, stdout, store, () => Epoch.AddSeconds(Interlocked.Increment(ref tick)));
                }
                catch (Exception e)
                {
                    lock (errors)
                    {
                        errors.Add(e);
                    }
                }
            }
        })).ToArray();

        foreach (Thread w in workers)
        {
            w.Start();
        }

        foreach (Thread w in workers)
        {
            Assert.True(w.Join(TimeSpan.FromSeconds(60)));
        }

        Assert.Empty(errors);
        ClaudeStateDecodeResult r = ClaudeStateCodec.Decode(File.ReadAllBytes(h.StateFile));
        Assert.Equal(ClaudeStateDecodeStatus.Ok, r.Status);
        Assert.Equal(globalMax, r.State.FiveHour!.UsedPercent, 6);               // serialised merges => the max wins, nothing lost
        Assert.Equal([FileClaudeStateStore.FileName], h.FilesInStateDir());       // no temp files left behind
    }

    // ================= clock canonicalisation =================

    [Fact]
    public void Instants_AreStoredAsUtcMilliseconds()
    {
        using var h = new Harness { Now = new DateTimeOffset(2030, 1, 1, 14, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567) };
        h.Run(Payload(Win(10, Epoch.AddHours(3)), null), "--tap");
        ClaudeWindowRecord r = h.State().FiveHour!;
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 12, 0, 0, 123, TimeSpan.Zero), r.ChangedAt);
    }
}
