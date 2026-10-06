using QuotaDuck.Core.Claude;
using static QuotaDuck.Core.Tests.Fx;

namespace QuotaDuck.Core.Tests;

/// <summary>Grid/sweep/property tests: the truthfulness rules must hold for every combination, not just hand-picked cases.</summary>
public class InvariantTests
{
    private static readonly double[] Pcts = [0, 0.5, 23.5, 50, 99.9, 100];
    private static readonly double[] AgesMinutes = [0, 1, 9.99, 10, 11, 19.99, 20, 21, 119.99, 120, 121, 600, 5000];
    // minutes until reset: past, boundary, near, mid, far, implausibly far
    private static readonly double?[] ResetsInMinutes = [-600, -1, 0, 0.001, 1, 9, 30, 179, 300, 310, 311, 600, 12_000, null];

    [Fact]
    public void Grid_TruthfulnessInvariantsHoldForEveryCombination()
    {
        int cases = 0;
        foreach (ObservationSource src in new[] { ObservationSource.PushReported, ObservationSource.PolledRead })
        foreach (double pct in Pcts)
        foreach (double age in AgesMinutes)
        foreach (double? resetIn in ResetsInMinutes)
        {
            cases++;
            WindowObservation o = Obs(src, pct, M(age), resetsIn: resetIn is { } r ? M(r) : null, hasReset: resetIn is not null);
            WindowView v = WindowDeriver.Derive(o, T0, Policy);
            string ctx = $"{src} pct={pct} age={age}m resetIn={resetIn}m";

            bool displayable = v.State is WindowState.Fresh or WindowState.Quiet or WindowState.Sleeping;

            // 1. never a number when there is no truthful number
            if (!displayable)
            {
                Assert.True(v.DisplayedUsedPercent is null && v.DisplayedRemainingPercent is null && v.ResetsIn is null, ctx);
            }
            else
            {
                Assert.True(v.DisplayedUsedPercent == pct && v.DisplayedRemainingPercent == 100 - pct, ctx);
            }

            // 2. a passed reset time always wins
            bool resetPassed = resetIn is <= 0;
            Assert.True(resetPassed == (v.State == WindowState.Elapsed), ctx);

            // 3. Elapsed carries no future-oriented fields; others carry a positive countdown when a reset is known
            if (v.State == WindowState.Elapsed)
            {
                Assert.True(v.ElapsedSince >= TimeSpan.Zero && v.NextTransitionAt is null && v.LastKnownUsedPercent == pct, ctx);
            }
            else if (displayable && resetIn is not null)
            {
                Assert.True(v.ResetsIn > TimeSpan.Zero, ctx);
            }

            // 4. Fresh is only ever claimed for young evidence without anomalies
            if (v.State == WindowState.Fresh)
            {
                double bound = src == ObservationSource.PushReported ? 10 : 20;
                Assert.True(age < bound && v.Anomalies == WindowAnomalies.None || (v.Anomalies == WindowAnomalies.MissingResetTime && age < bound), ctx);
            }

            // 5. the scheduled next transition is strictly in the future
            if (v.NextTransitionAt is { } next)
            {
                Assert.True(next > T0, ctx);
            }
        }

        Assert.Equal(2 * Pcts.Length * AgesMinutes.Length * ResetsInMinutes.Length, cases);
    }

    [Theory]
    [InlineData(ObservationSource.PushReported)]
    [InlineData(ObservationSource.PolledRead)]
    public void TimeSweep_StateOnlyMovesForward_AndNextTransitionIsExact(ObservationSource src)
    {
        // Evidence at T0, reset 3 h later; sweep 6 h in 30-second steps.
        WindowObservation o = Obs(src, 40, TimeSpan.Zero, resetsIn: H(3));
        WindowState prev = WindowState.Fresh;
        DateTimeOffset? predicted = WindowDeriver.Derive(o, T0, Policy).NextTransitionAt;
        var seen = new List<WindowState> { prev };

        for (int s = 0; s <= 6 * 3600; s += 30)
        {
            DateTimeOffset now = T0 + TimeSpan.FromSeconds(s);
            WindowView v = WindowDeriver.Derive(o, now, Policy);
            Assert.True(v.State >= prev, $"state went backwards at +{s}s: {prev} -> {v.State}");
            if (v.State != prev)
            {
                Assert.NotNull(predicted);
                // the change must not happen BEFORE the predicted instant, and by this step must have happened at/after it
                Assert.True(now >= predicted, $"transition to {v.State} at {now - T0} before predicted {predicted - T0}");
                Assert.True(now - predicted < TimeSpan.FromSeconds(30), "transition detected more than one step after predicted");
                prev = v.State;
                seen.Add(prev);
                predicted = v.NextTransitionAt;
            }
        }

        // Quiet bound (2 h) < reset (3 h), so the full ladder is walked exactly once, in order.
        Assert.Equal([WindowState.Fresh, WindowState.Quiet, WindowState.Sleeping, WindowState.Elapsed], seen);
    }

    [Fact]
    public void ExactTransitionInstants_StateDiffersAtNextTransition_AndNotOneTickBefore()
    {
        var tick = TimeSpan.FromTicks(1);
        foreach (WindowObservation o in new[]
        {
            Obs(evidenceAge: M(3), resetsIn: H(3)),            // Fresh -> Quiet
            Obs(evidenceAge: M(30), resetsIn: H(3)),           // Quiet -> Sleeping
            Obs(evidenceAge: M(30), resetsIn: M(10)),          // Quiet -> Elapsed (reset first)
            Obs(evidenceAge: M(1), resetsIn: M(2)),            // Fresh -> Elapsed
            Obs(evidenceAge: H(5), resetsIn: H(1)),            // Sleeping -> Elapsed
            Fx.Polled(40, M(5), H(4)),                         // polled Fresh -> Quiet
        })
        {
            WindowView now = WindowDeriver.Derive(o, T0, Policy);
            DateTimeOffset next = now.NextTransitionAt!.Value;
            Assert.Equal(now.State, WindowDeriver.Derive(o, next - tick, Policy).State);
            Assert.NotEqual(now.State, WindowDeriver.Derive(o, next, Policy).State);
        }
    }

    [Fact]
    public void MergedRandomHistory_NeverViolatesRecordInvariants()
    {
        // Deterministic fuzz of the Claude merge over a long synthetic session history.
        var rng = new Random(20300101);
        ClaudeWindowRecord? rec = null;
        DateTimeOffset now = T0;
        DateTimeOffset epoch = now + H(5);
        double truth = 0;

        for (int i = 0; i < 4000; i++)
        {
            now += TimeSpan.FromSeconds(rng.Next(0, 1200));
            if (now >= epoch)
            {
                epoch = now + H(5);       // provider rolls the window
                truth = 0;
            }

            truth = Math.Min(100, truth + rng.NextDouble() * 0.5);

            ClaudeIncomingWindow? incoming = rng.Next(20) switch
            {
                0 => null,
                1 => new ClaudeIncomingWindow(null, epoch),
                2 => new ClaudeIncomingWindow(rng.Next(2) == 0 ? -5 : 140, epoch),
                3 => new ClaudeIncomingWindow(truth, now - H(1)),                 // reset in the past
                4 => new ClaudeIncomingWindow(truth, now + H(48)),                // implausible
                5 => new ClaudeIncomingWindow(Math.Max(0, truth - 7), epoch),     // stale session, lower
                6 => new ClaudeIncomingWindow(truth, epoch - H(5)),               // older epoch
                _ => new ClaudeIncomingWindow(Math.Round(truth, 1), epoch + TimeSpan.FromSeconds(rng.Next(-30, 30))),
            };

            ClaudeWindowRecord? before = rec;
            ClaudeMergeResult r = ClaudeWindowMerger.Merge(ClaudeWindowKind.FiveHour, before, incoming, now, Policy);
            rec = r.Record;

            if (rec is null)
            {
                continue;
            }

            Assert.InRange(rec.UsedPercent, 0, 100);
            Assert.True(rec.ChangedAt <= rec.ReceivedAt, $"i={i} changed after received");
            Assert.True(rec.ReceivedAt <= now, $"i={i} received in the future");
            if (before is not null)
            {
                Assert.True(rec.ChangedAt >= before.ChangedAt, $"i={i} ChangedAt went backwards");
                Assert.True(rec.ReceivedAt >= before.ReceivedAt, $"i={i} ReceivedAt went backwards");
                if (r.Decision is ClaudeMergeDecision.Unchanged or ClaudeMergeDecision.OlderEpochIgnored or ClaudeMergeDecision.LowerIgnored
                    or ClaudeMergeDecision.NoIncoming or ClaudeMergeDecision.RejectedInvalidPercent or ClaudeMergeDecision.RejectedMissingPercent
                    or ClaudeMergeDecision.RejectedResetInPast or ClaudeMergeDecision.RejectedImplausibleReset)
                {
                    Assert.Equal(before.ChangedAt, rec.ChangedAt);       // only real information moves ChangedAt
                    Assert.Equal(before.UsedPercent, rec.UsedPercent);
                }

                if (r.Decision is ClaudeMergeDecision.Advanced or ClaudeMergeDecision.NewEpoch or ClaudeMergeDecision.AdjustedDown)
                {
                    Assert.Equal(now, rec.ChangedAt);
                }

            }

            // derived view must obey the display rules for the stored record
            WindowView v = WindowDeriver.Derive(ClaudeObservations.ToObservation(ClaudeWindowKind.FiveHour, rec), now, Policy);
            if (v.State is WindowState.Elapsed or WindowState.NoData)
            {
                Assert.Null(v.DisplayedUsedPercent);
            }
        }

        Assert.NotNull(rec);
    }

    [Fact]
    public void JitteringNonMonotonicClock_NeverProducesAnUndecodableRecord_AndTimestampsOnlyMoveForward()
    {
        // Many "processes" interleave with slightly out-of-order clock readings (and the odd big step back).
        var rng = new Random(20300107);
        ClaudeWindowRecord? rec = null;
        DateTimeOffset baseTime = T0;
        DateTimeOffset epoch = T0 + H(5);
        double truth = 1;
        for (int i = 0; i < 6000; i++)
        {
            baseTime += TimeSpan.FromSeconds(rng.Next(0, 90));
            DateTimeOffset now = baseTime + TimeSpan.FromSeconds(rng.Next(-120, 30));           // jitter, can be before earlier readings
            if (rng.Next(500) == 0)
            {
                now -= H(2);                                                                    // occasional large step back
            }

            truth = Math.Min(100, truth + rng.NextDouble() * 0.2);
            ClaudeWindowRecord? before = rec;
            ClaudeMergeResult r = ClaudeWindowMerger.Merge(
                ClaudeWindowKind.FiveHour, before, new ClaudeIncomingWindow(Math.Round(truth, 2), epoch), now.ToUniversalTime() + TimeSpan.FromHours(0), Policy);
            rec = r.Record;
            if (rec is null)
            {
                continue;
            }

            Assert.True(rec.ChangedAt <= rec.ReceivedAt, $"i={i}");
            if (before is not null)
            {
                Assert.True(rec.ReceivedAt >= before.ReceivedAt, $"i={i} ReceivedAt backwards");
            }

            ClaudeStateDecodeResult back = ClaudeStateCodec.Decode(ClaudeStateCodec.Encode(new ClaudeState(rec, null)));
            Assert.Equal(ClaudeStateDecodeStatus.Ok, back.Status);
            Assert.NotNull(back.State.FiveHour);
        }

        Assert.NotNull(rec);
    }
}
