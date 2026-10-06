using System.Text;
using QuotaDuck.Bridge;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.Coordinator.Tests;

internal sealed class Temp : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qd-coord-tests", Guid.NewGuid().ToString("N")[..10]);

    public string State => System.IO.Path.Combine(Path, "bridge");

    public Temp() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        for (int i = 0; i < 10 && Directory.Exists(Path); i++)
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>The real file reader and watcher against real files — including the bridge's own writer.</summary>
public class FileSourceTests
{
    private static readonly ClaudeState StateA = Fx.Claude(Fx.Rec(25, Fx.T0.AddHours(2), Fx.T0), Fx.Rec(10, Fx.T0.AddDays(3), Fx.T0));
    private static readonly ClaudeState StateB = Fx.Claude(Fx.Rec(26, Fx.T0.AddHours(2), Fx.T0.AddMinutes(1)), Fx.Rec(11, Fx.T0.AddDays(3), Fx.T0.AddMinutes(1)));

    private static void Put(string dir, byte[] bytes)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, ClaudeStateLocation.FileName), bytes);
    }

    // ---------------- reading ----------------

    [Fact]
    public void AbsentDirectory_AbsentFile_Empty_Corrupt_Oversized_FutureSchema_AreClassified()
    {
        using var t = new Temp();
        var src = new FileClaudeStateSource(t.State);
        Assert.Equal(ClaudeSourceStatus.Absent, src.Read().Status);                      // directory missing

        Directory.CreateDirectory(t.State);
        Assert.Equal(ClaudeSourceStatus.Absent, src.Read().Status);                      // file missing

        Put(t.State, []);
        Assert.Equal(ClaudeSourceStatus.Empty, src.Read().Status);

        Put(t.State, "{\"schema\":1,\"fiveHour\":"u8.ToArray());                         // truncated mid-write
        Assert.Equal(ClaudeSourceStatus.Corrupt, src.Read().Status);

        Put(t.State, Encoding.UTF8.GetBytes(new string('x', 5000)));
        Assert.Equal(ClaudeSourceStatus.Corrupt, src.Read().Status);                      // > 4096 bytes: never read further

        Put(t.State, "{\"schema\":2,\"anything\":true}"u8.ToArray());
        ClaudeSourceRead future = src.Read();
        Assert.Equal(ClaudeSourceStatus.FutureSchema, future.Status);
        Assert.Equal(ClaudeState.Empty, future.State);
    }

    [Fact]
    public void ReadsExactlyWhatTheBridgeWrites_ViaTheBridgesOwnStore()
    {
        using var t = new Temp();
        var store = new FileClaudeStateStore(t.State);
        Assert.True(store.Update(_ => new StateChange(StateA, true)).Wrote);
        Assert.Equal(new ClaudeSourceRead(ClaudeSourceStatus.Ok, StateA), new FileClaudeStateSource(t.State).Read());
        Assert.True(store.Update(_ => new StateChange(StateB, true)).Wrote);
        Assert.Equal(StateB, new FileClaudeStateSource(t.State).Read().State);
    }

    [Fact]
    public void ALockedFile_IsUnreadable_NotCorrupt_AndAHeldReadHandleNeverCorruptsTheState()
    {
        using var t = new Temp();
        Put(t.State, ClaudeStateCodec.Encode(StateA));
        string file = Path.Combine(t.State, ClaudeStateLocation.FileName);
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(ClaudeSourceStatus.Unreadable, new FileClaudeStateSource(t.State).Read().Status);
        }

        // FINDING (reported with the gate): on Windows an atomic rename-over can be refused (access denied) while ANY handle to the target is
        // open, even a FILE_SHARE_DELETE one. The reader holds the file for microseconds, and a refused bridge write is harmless (fail closed:
        // the old state stays intact and the next status-line event writes again) — but it must never corrupt or lose the state.
        var store = new FileClaudeStateStore(t.State);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            store.Update(_ => new StateChange(StateB, true));
            Assert.Equal(StateA, new FileClaudeStateSource(t.State).Read().State);          // StateA intact (or already B if the OS allowed it)
        }

        Assert.True(store.Update(_ => new StateChange(StateB, true)).Wrote);                  // handle released: the write goes through
        Assert.Equal(StateB, new FileClaudeStateSource(t.State).Read().State);
    }

    [Fact]
    public async Task AtomicReplace_NeverShowsAnIntermediateState_ToAConcurrentReader()
    {
        using var t = new Temp();
        Directory.CreateDirectory(t.State);
        byte[] a = ClaudeStateCodec.Encode(StateA), b = ClaudeStateCodec.Encode(StateB);
        string file = Path.Combine(t.State, ClaudeStateLocation.FileName);
        File.WriteAllBytes(file, a);
        var src = new FileClaudeStateSource(t.State);
        using var stop = new CancellationTokenSource();
        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < 400 && !stop.IsCancellationRequested; i++)
            {
                string tmp = file + "." + i + ".tmp";
                File.WriteAllBytes(tmp, i % 2 == 0 ? b : a);
                try
                {
                    File.Move(tmp, file, overwrite: true);                               // exactly what the bridge does
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                {
                    File.Delete(tmp);                                                    // refused while the reader had the file open: skipped, as the bridge does
                }
            }
        });

        int ok = 0, transient = 0;
        while (!writer.IsCompleted)
        {
            ClaudeSourceRead r = src.Read();
            if (r.Status == ClaudeSourceStatus.Ok)
            {
                Assert.True(r.State == StateA || r.State == StateB);
                ok++;
            }
            else
            {
                Assert.Equal(ClaudeSourceStatus.Unreadable, r.Status);                   // a rename race may briefly deny access — never "corrupt"/partial
                transient++;
            }
        }

        stop.Cancel();
        await writer;
        Assert.True(ok > 20, $"ok={ok} transient={transient}");
    }

    // ---------------- location (same as the bridge's) ----------------

    [Fact]
    public void Location_And_FileName_AreExactlyTheBridges()
    {
        Assert.Equal(FileClaudeStateStore.FileName, ClaudeStateLocation.FileName);
        string? ours = ClaudeStateLocation.DefaultDirectory();
        Assert.NotNull(ours);
        Assert.Equal(StateLocation.DefaultDirectory(), ours);
        Assert.EndsWith(Path.Combine("QuotaDuck", "bridge"), ours);
    }

    [Fact]
    public void Location_IsNotRedirectableByTheEnvironment()
    {
        string? real = ClaudeStateLocation.DefaultDirectory();
        string[] vars = ["LOCALAPPDATA", "USERPROFILE", "APPDATA", "HOME", "HOMEDRIVE", "HOMEPATH"];
        var saved = vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        using var evil = new Temp();
        try
        {
            foreach (string v in vars)
            {
                Environment.SetEnvironmentVariable(v, evil.Path);
            }

            Assert.Equal(real, ClaudeStateLocation.DefaultDirectory());
            Assert.DoesNotContain("qd-coord-tests", ClaudeStateLocation.DefaultDirectory()!);
        }
        finally
        {
            foreach ((string k, string? v) in saved)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    [Theory]
    [InlineData(@"C:\Users\bob\AppData\Local", @"C:\Users\bob", true)]
    [InlineData(@"C:\Users\bob2\AppData\Local", @"C:\Users\bob", false)]
    [InlineData(@"C:\Users\bob\..\eve\AppData", @"C:\Users\bob", false)]
    [InlineData(@"C:\Users\bob", @"C:\Users\bob", false)]
    [InlineData(@"relative\path", @"C:\Users\bob", false)]
    public void IsStrictlyInside_RefusesSiblingsTraversalAndRelativePaths(string folder, string profile, bool expected) =>
        Assert.Equal(expected, ClaudeStateLocation.IsStrictlyInside(folder, profile));

    // ---------------- watching ----------------

    private static bool WaitFor(ManualResetEventSlim e, int ms = 4000) => e.Wait(ms);

    [Fact]
    public void Watcher_DirectoryAbsent_ThenAppears_AttachesAndSeesAtomicReplace()
    {
        using var t = new Temp();
        using var w = new FileSystemClaudeStateWatcher(t.State);
        Assert.False(w.TryAttach());
        Assert.False(w.IsAttached);

        Directory.CreateDirectory(t.State);
        Assert.True(w.TryAttach());
        Assert.True(w.IsAttached);
        Assert.True(w.TryAttach());                                                        // idempotent

        using var seen = new ManualResetEventSlim();
        w.Changed += seen.Set;
        string file = Path.Combine(t.State, ClaudeStateLocation.FileName);
        string tmp = file + ".abc123.tmp";
        File.WriteAllBytes(tmp, ClaudeStateCodec.Encode(StateA));
        Assert.False(seen.Wait(600), "a temp file must not raise a change");               // the bridge's temp files are ignored by name
        File.Move(tmp, file, overwrite: true);
        Assert.True(WaitFor(seen), "atomic replace not noticed");

        seen.Reset();
        File.Delete(file);
        Assert.True(WaitFor(seen), "deletion not noticed");
    }

    [Fact]
    public void Watcher_DirectoryRemoved_IsDetectedOnVerify_AndReattachesWhenItReturns()
    {
        using var t = new Temp();
        Directory.CreateDirectory(t.State);
        using var w = new FileSystemClaudeStateWatcher(t.State);
        Assert.True(w.TryAttach());
        Directory.Delete(t.State, true);
        Assert.False(w.TryAttach());                                                       // the safety check notices
        Assert.False(w.IsAttached);
        Directory.CreateDirectory(t.State);
        Assert.True(w.TryAttach());
    }

    [Fact]
    public void Watcher_Dispose_IsIdempotent_AndPreventsReattach()
    {
        using var t = new Temp();
        Directory.CreateDirectory(t.State);
        var w = new FileSystemClaudeStateWatcher(t.State);
        Assert.True(w.TryAttach());
        w.Dispose();
        w.Dispose();
        Assert.False(w.TryAttach());
        Assert.False(w.IsAttached);
    }

    // ---------------- the whole real stack (real files + watcher + timers, fake Codex) ----------------

    [Fact]
    public async Task EndToEnd_BridgeWritesAndTheCoordinatorSees_IncludingAFileThatAppearsLater()
    {
        using var t = new Temp();                                                         // the directory does NOT exist yet
        var codex = new FakeCodex();
        var options = new CoordinatorOptions
        {
            CodexEnabled = false,
            Policy = new CoordinatorPolicy { ClaudeDebounce = TimeSpan.FromMilliseconds(40), WatcherReattach = TimeSpan.FromMilliseconds(120) },
        };
        await using var c = new QuotaCoordinator(
            options,
            new FileClaudeStateSource(t.State),
            new FileSystemClaudeStateWatcher(t.State),
            codex,
            TimeProvider.System,
            new ThreadPoolTimerScheduler());
        c.Start();
        Assert.Equal(ProviderIssue.WaitingForTerminalClaudeCode, c.Capture().Claude.Issue);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ClaudeState live = Fx.Claude(Fx.Rec(42, now.AddHours(2), now.AddSeconds(-5)), Fx.Rec(9, now.AddDays(3), now.AddSeconds(-5)));
        var store = new FileClaudeStateStore(t.State);                                    // creates the directory + file, like the real bridge
        Assert.True(store.Update(_ => new StateChange(live, true)).Wrote);
        await Wait.Until(() => c.Capture().Claude.View.Windows.Any(w => w.DisplayedUsedPercent == 42));
        Assert.Equal(ProviderIssue.None, c.Capture().Claude.Issue);

        live = Fx.Claude(Fx.Rec(55, now.AddHours(2), DateTimeOffset.UtcNow), Fx.Rec(9, now.AddDays(3), now.AddSeconds(-5)));
        Assert.True(store.Update(_ => new StateChange(live, true)).Wrote);
        await Wait.Until(() => c.Capture().Claude.View.Windows.Any(w => w.DisplayedUsedPercent == 55));
        Assert.Equal(0, codex.Calls);
    }
}
