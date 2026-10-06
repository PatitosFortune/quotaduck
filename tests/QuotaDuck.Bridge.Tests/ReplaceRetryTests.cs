using QuotaDuck.Core.Claude;
using static QuotaDuck.Core.Tests.PayloadFx;

namespace QuotaDuck.Bridge.Tests;

/// <summary>The small bounded retry around the final atomic replace (and nothing else).</summary>
public class ReplaceRetryTests
{
    private static readonly DateTimeOffset T = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ClaudeState S(double pct) =>
        new(new ClaudeWindowRecord(pct, T.AddHours(2), T, T), new ClaudeWindowRecord(10, T.AddDays(3), T, T));

    private static IOException Sharing() => new("sharing violation", unchecked((int)0x80070020));

    private sealed class Rig : IDisposable
    {
        private readonly TempDir _root = new();
        public List<int> Sleeps { get; } = [];
        public int Attempts { get; private set; }
        public Queue<Exception?> Script { get; } = new();
        public FileClaudeStateStore Store { get; }
        public string Dir => Path.Combine(_root.Path, "QuotaDuck", "bridge");
        public string File_ => Path.Combine(Dir, FileClaudeStateStore.FileName);

        public Rig()
        {
            Store = new FileClaudeStateStore(Dir, new StoreOptions
            {
                Sleep = ms => Sleeps.Add(ms),
                Replace = (tmp, dest) =>
                {
                    Attempts++;
                    Exception? fail = Script.Count > 0 ? Script.Dequeue() : null;
                    if (fail is not null)
                    {
                        throw fail;
                    }

                    File.Move(tmp, dest, overwrite: true);
                },
            });
        }

        public string[] Temps() => Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.tmp") : [];

        public void Dispose() => _root.Dispose();
    }

    private static StoreOutcome Write(Rig r, ClaudeState s) => r.Store.Update(_ => new StateChange(s, true));

    [Fact]
    public void FirstReplaceFailsTransiently_SecondSucceeds()
    {
        using var r = new Rig();
        r.Script.Enqueue(Sharing());
        StoreOutcome o = Write(r, S(30));
        Assert.True(o.Wrote);
        Assert.Equal(StoreStatus.Ok, o.Status);
        Assert.Equal(2, r.Attempts);
        Assert.Equal([5], r.Sleeps);
        Assert.Equal(S(30), r.Store.Peek());
        Assert.Empty(r.Temps());
    }

    [Fact]
    public void MultipleTransientFailures_ThenSuccess_WithinTheBoundedDelays()
    {
        using var r = new Rig();
        r.Script.Enqueue(Sharing());
        r.Script.Enqueue(new UnauthorizedAccessException("denied while another handle is open"));
        r.Script.Enqueue(Sharing());
        // the destination must exist for "access denied" to count as a sharing conflict
        Directory.CreateDirectory(r.Dir);
        File.WriteAllBytes(r.File_, ClaudeStateCodec.Encode(S(1)));
        StoreOutcome o = Write(r, S(31));
        Assert.True(o.Wrote);
        Assert.Equal(4, r.Attempts);
        Assert.Equal([5, 10, 20], r.Sleeps);
        Assert.True(r.Sleeps.Sum() <= 50, "added delay must stay on the order of tens of milliseconds");
        Assert.Equal(S(31), r.Store.Peek());
    }

    [Fact]
    public void RetryLimitExhausted_FailsClosed_PreviousStateIntact_TempRemoved()
    {
        using var r = new Rig();
        Assert.True(Write(r, S(40)).Wrote);                                          // the "previous" state
        byte[] before = File.ReadAllBytes(r.File_);
        int attemptsBefore = r.Attempts;

        for (int i = 0; i < 10; i++)
        {
            r.Script.Enqueue(Sharing());
        }

        StoreOutcome o = Write(r, S(99));
        Assert.False(o.Wrote);
        Assert.Equal(StoreStatus.IoError, o.Status);
        Assert.Equal(4, r.Attempts - attemptsBefore);                                // 1 try + exactly 3 retries, never more
        Assert.Equal([5, 10, 20], r.Sleeps);
        Assert.Equal(before, File.ReadAllBytes(r.File_));                            // byte-for-byte the old state
        Assert.Equal(S(40), r.Store.Peek());
        Assert.Empty(r.Temps());                                                     // temp file cleaned up
    }

    public static IEnumerable<object[]> Permanent() =>
    [
        [new DirectoryNotFoundException("gone")],
        [new FileNotFoundException("gone")],
        [new PathTooLongException("too long")],
        [new ArgumentException("bad path")],
        [new NotSupportedException("bad path")],
        [new IOException("some other I/O failure")],
        [new IOException("disk full", unchecked((int)0x80070070))],
        [new InvalidOperationException("whatever")],
    ];

    [Theory]
    [MemberData(nameof(Permanent))]
    public void PermanentFailures_AreNotRetried_AndLeaveNoTempFile(Exception failure)
    {
        using var r = new Rig();
        Assert.True(Write(r, S(40)).Wrote);
        byte[] before = File.ReadAllBytes(r.File_);
        int attemptsBefore = r.Attempts;
        r.Script.Enqueue(failure);

        StoreOutcome o = Write(r, S(77));
        Assert.False(o.Wrote);
        Assert.Equal(1, r.Attempts - attemptsBefore);
        Assert.Empty(r.Sleeps);
        Assert.Equal(before, File.ReadAllBytes(r.File_));
        Assert.Empty(r.Temps());
    }

    [Fact]
    public void AccessDenied_OnAReadOnlyOrMissingDestination_IsPermanent()
    {
        using var r = new Rig();
        Assert.True(Write(r, S(40)).Wrote);
        File.SetAttributes(r.File_, FileAttributes.ReadOnly);
        try
        {
            int before = r.Attempts;
            r.Script.Enqueue(new UnauthorizedAccessException("read-only"));
            Assert.False(Write(r, S(50)).Wrote);
            Assert.Equal(1, r.Attempts - before);
            Assert.Empty(r.Sleeps);
        }
        finally
        {
            File.SetAttributes(r.File_, FileAttributes.Normal);
        }

        Assert.False(FileClaudeStateStore.IsTransientSharingConflict(new UnauthorizedAccessException(), Path.Combine(r.Dir, "does-not-exist.json")));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]
    [InlineData(unchecked((int)0x80070021), true)]
    [InlineData(unchecked((int)0x80070005), false)]       // as an IOException (not UnauthorizedAccess): not a sharing code
    [InlineData(unchecked((int)0x80070002), false)]
    [InlineData(unchecked((int)0x80070070), false)]
    [InlineData(unchecked((int)0x80131620), false)]
    [InlineData(0x20, false)]                             // only the HRESULT form of the win32 code counts
    public void Classification_OfIoExceptions(int hresult, bool transient) =>
        Assert.Equal(transient, FileClaudeStateStore.IsTransientSharingConflict(new IOException("x", hresult), @"C:\nonexistent\x.json"));

    [Fact]
    public void MalformedExistingState_IsNotRetriedOrRepaired_ByTheRetryLogic()
    {
        using var r = new Rig();
        Directory.CreateDirectory(r.Dir);
        File.WriteAllText(r.File_, "{not json");                                    // corrupt state is treated as empty and rewritten, as before
        StoreOutcome o = Write(r, S(12));
        Assert.True(o.Wrote);
        Assert.Equal(1, r.Attempts);
        Assert.Empty(r.Sleeps);
    }

    [Fact]
    public void RealContention_AHandleHeldBrieflyByAnotherProcess_IsRiddenOut()
    {
        // the real File.Move (no seam) while another handle is genuinely open for ~8 ms around the replace
        int succeeded = 0;
        for (int round = 0; round < 6; round++)
        {
            using var t = new TempDir();
            string dir = Path.Combine(t.Path, "QuotaDuck", "bridge");
            FileStream? holder = null;
            Timer? release = null;
            var store = new FileClaudeStateStore(dir, new StoreOptions
            {
                BeforeReplace = _ =>
                {
                    string file = Path.Combine(dir, FileClaudeStateStore.FileName);
                    if (File.Exists(file))
                    {
                        holder = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        release = new Timer(_ => holder?.Dispose(), null, 8, Timeout.Infinite);
                    }
                },
            });
            Assert.True(store.Update(_ => new StateChange(S(20), true)).Wrote);          // first write: no destination yet
            StoreOutcome o = store.Update(_ => new StateChange(S(21 + round), true));    // second write meets the held handle
            release?.Dispose();
            holder?.Dispose();
            if (o.Wrote)
            {
                succeeded++;
                Assert.Equal(S(21 + round), store.Peek());
            }
            else
            {
                Assert.Equal(S(20), store.Peek());                                       // never corrupt, never lost
            }
        }

        Assert.True(succeeded >= 5, $"only {succeeded}/6 contended writes succeeded");
    }
}
