using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using QuotaDuck.Core.Claude;
using static QuotaDuck.Core.Tests.PayloadFx;
using static QuotaDuck.Bridge.Tests.Harness;

namespace QuotaDuck.Bridge.Tests;

public class StateStoreTests
{
    private static void Seed(Harness h, byte[] content)
    {
        Directory.CreateDirectory(h.StateDir);
        File.WriteAllBytes(h.StateFile, content);
    }

    // ================= state-file corruption =================

    public static TheoryData<string, byte[]> CorruptFiles => new()
    {
        { "garbage text", Bytes("this is not json at all") },
        { "binary noise", [0x00, 0xFF, 0xFE, 0x01, 0x80, 0x81] },
        { "truncated json", Bytes("{\"schema\":1,\"fiveHour\":{\"usedPercent\":5") },
        { "empty object (no schema)", Bytes("{}") },
        { "array root", Bytes("[1,2,3]") },
        { "oversized (5000 bytes)", Bytes("{\"schema\":1," + new string(' ', 5000) + "}") },
        { "zero bytes", [] },
        { "BOM only", [0xEF, 0xBB, 0xBF] },
        { "invalid utf-8", [.. Bytes("{\"schema\":1,\"x\":\""), 0xFF, .. Bytes("\"}")] },
        { "schema 0", Bytes("{\"schema\":0}") },
        { "duplicate keys", Bytes("{\"schema\":1,\"schema\":1}") },
    };

    [Theory]
    [MemberData(nameof(CorruptFiles))]
    public void CorruptStateFile_IsNeverTrusted_AndASubsequentValidReportHealsIt(string _, byte[] corrupt)
    {
        using var h = new Harness();
        Seed(h, corrupt);

        // a payload with nothing usable must NOT rewrite (or "repair") the file — it is left exactly as found
        h.Run("{}", "--tap");
        Assert.Equal(corrupt, h.FileBytes());

        // normal mode over a corrupt file prints "waiting", never data derived from garbage
        Assert.Equal("QuotaDuck | waiting for first report", h.RunText("{}"));

        // a valid report replaces it with a fresh, valid state containing ONLY the new data
        h.Run(Payload(Five(21), null), "--tap");
        ClaudeState s = h.State();
        Assert.Equal(21, s.FiveHour!.UsedPercent);
        Assert.Null(s.SevenDay);
        Assert.Equal([FileClaudeStateStore.FileName], h.FilesInStateDir());
    }

    [Fact]
    public void FutureSchemaFile_IsNeverOverwritten_NorDisplayed()
    {
        using var h = new Harness();
        byte[] future = Bytes("{\"schema\":2,\"fiveHour\":{\"somethingNew\":true},\"extra\":\"" + Canary + "\"}");
        Seed(h, future);
        (int exit, byte[] stdout) = h.Run(Both(10, 20));
        Assert.Equal(0, exit);
        Assert.Equal(future, h.FileBytes());                                   // byte-for-byte untouched
        Assert.Equal("QuotaDuck | waiting for first report\n", Encoding.UTF8.GetString(stdout));
        Assert.Equal(0, h.Writes);
        Assert.Equal([FileClaudeStateStore.FileName], h.FilesInStateDir());
    }

    [Fact]
    public void PartiallyInvalidState_KeepsTheGoodWindow_DropsTheBadOne()
    {
        using var h = new Harness();
        h.Run(Both(10, 20), "--tap");
        string json = Encoding.UTF8.GetString(h.FileBytes()!).Replace("\"usedPercent\":10", "\"usedPercent\":1000");   // corrupt five_hour only
        Seed(h, Bytes(json));
        h.Now = Epoch.AddMinutes(1);
        h.Run(Payload(null, Seven(25)), "--tap");                                // update only the weekly window
        ClaudeState s = h.State();
        Assert.Null(s.FiveHour);                                                // the corrupt window was dropped, not "repaired" with a guess
        Assert.Equal(25, s.SevenDay!.UsedPercent);
    }

    [Fact]
    public void ValidStateWithBomAndUnknownFields_IsReadAndRewrittenWithoutThem()
    {
        using var h = new Harness();
        h.Run(Both(10, 20), "--tap");
        string json = Encoding.UTF8.GetString(h.FileBytes()!).Replace("{\"schema\":1,", "{\"schema\":1,\"note\":\"" + Canary + "\",");
        Seed(h, [0xEF, 0xBB, 0xBF, .. Bytes(json)]);
        h.Now = Epoch.AddMinutes(1);
        h.Run(Payload(Five(30), null), "--tap");
        Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(h.FileBytes()!));
        Assert.Equal(30, h.State().FiveHour!.UsedPercent);
        Assert.Equal(20, h.State().SevenDay!.UsedPercent);
    }

    // ================= interrupted / failed atomic writes =================

    [Fact]
    public void FailureBetweenTempWriteAndReplace_LeavesThePreviousFileIntact_AndNoTempBehind()
    {
        using var h = new Harness();
        h.Run(Both(10, 20), "--tap");
        byte[] before = h.FileBytes()!;
        h.Now = Epoch.AddMinutes(1);

        h.FailNextReplace = true;                                               // throws after the temp file is fully written
        (int exit, byte[] stdout) = h.Run(Both(55, 66));
        Assert.Equal(0, exit);                                                  // the status line never fails loudly
        Assert.Equal(before, h.FileBytes());                                    // never half-written, never lost
        Assert.Equal([FileClaudeStateStore.FileName], h.FilesInStateDir());     // the temp file was cleaned up
        // display falls back to what is actually on disk — not an empty "waiting" line hiding valid state
        Assert.Equal("QuotaDuck | 5h 10% used | wk 20% used\n", Encoding.UTF8.GetString(stdout));

        h.Now = Epoch.AddMinutes(2);                                            // and the next run recovers by itself
        h.Run(Both(55, 66), "--tap");
        Assert.Equal(55, h.State().FiveHour!.UsedPercent);
    }

    [Fact]
    public void ADirectoryWhereTheStateFileShouldBe_FailsTheReplace_CleanlyWithoutTempLeaks()
    {
        using var h = new Harness();
        Directory.CreateDirectory(h.StateFile);                                // hostile: a directory occupies the file name
        (int exit, _) = h.Run(Both(10, 20));
        Assert.Equal(0, exit);
        Assert.True(Directory.Exists(h.StateFile));
        Assert.Empty(Directory.GetFiles(h.StateDir));                           // no temp file left
    }

    [Fact]
    public void StaleTempFilesFromACrashedRun_AreRemovedOnTheNextWrite_NeverReadAsState_AndOtherFilesSurvive()
    {
        using var h = new Harness();
        h.Run(Both(10, 20), "--tap");
        File.WriteAllText(Path.Combine(h.StateDir, "claude-v1.json.aaaaaaaaaaaa.tmp"), "{\"schema\":1,\"fiveHour\":{\"usedPercent\":99}}");
        File.WriteAllText(Path.Combine(h.StateDir, "claude-v1.json.bbbbbbbbbbbb.tmp"), "garbage");
        File.WriteAllText(Path.Combine(h.StateDir, "claude-v1.json.keep"), "not ours to delete");
        File.WriteAllText(Path.Combine(h.StateDir, "other.tmp"), "not ours either");

        Assert.Equal(10, h.State().FiveHour!.UsedPercent);                      // a stale temp is never treated as state
        h.Now = Epoch.AddMinutes(1);
        h.Run(Both(11, 21), "--tap");

        Assert.Equal(["claude-v1.json", "claude-v1.json.keep", "other.tmp"], h.FilesInStateDir());
        Assert.Equal(11, h.State().FiveHour!.UsedPercent);
    }

    [Fact]
    public void ALockTimeout_SkipsTheWrite_ReturnsPromptly_AndDisplaysTheOnDiskState()
    {
        using var h = new Harness(lockTimeout: TimeSpan.FromMilliseconds(150));
        h.Run(Both(10, 20), "--tap");
        byte[] before = h.FileBytes()!;
        int writes = h.Writes;

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var m = new Mutex(false, h.Store.MutexName);                  // another bridge holding the lock
            m.WaitOne();
            held.Set();
            release.Wait();
            m.ReleaseMutex();
        });
        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)));

        h.Now = Epoch.AddMinutes(1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        (int exit, byte[] stdout) = h.Run(Both(77, 88));
        sw.Stop();
        release.Set();
        holder.Join();

        Assert.Equal(0, exit);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"blocked for {sw.ElapsedMilliseconds} ms");
        Assert.Equal(writes, h.Writes);
        Assert.Equal(before, h.FileBytes());
        Assert.Equal("QuotaDuck | 5h 10% used | wk 20% used\n", Encoding.UTF8.GetString(stdout));
    }

    [Fact]
    public void AnAbandonedLock_IsRecoveredFrom()
    {
        using var h = new Harness(lockTimeout: TimeSpan.FromSeconds(5));
        var thread = new Thread(() =>
        {
            var m = new Mutex(false, h.Store.MutexName);
            m.WaitOne();                                                         // exit while owning => abandoned
        });
        thread.Start();
        thread.Join();
        h.Run(Both(10, 20), "--tap");
        Assert.Equal(10, h.State().FiveHour!.UsedPercent);
    }

    // ================= permissions =================

    [Fact]
    public void StateDirectory_IsCreatedProtected_WithExactlyOneAllowRule_ForTheCurrentUser_AndFilesInheritIt()
    {
        using var h = new Harness();
        h.Run(Both(10, 20), "--tap");
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;

        DirectorySecurity dsec = new DirectoryInfo(h.StateDir).GetAccessControl();
        Assert.True(dsec.AreAccessRulesProtected, "DACL must not inherit from %LOCALAPPDATA%");
        var rules = dsec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        FileSystemAccessRule rule = Assert.Single(rules);
        Assert.Equal(me, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        Assert.False(rule.IsInherited);
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);

        FileSecurity fsec = new FileInfo(h.StateFile).GetAccessControl();
        var fileRules = fsec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        FileSystemAccessRule frule = Assert.Single(fileRules);
        Assert.Equal(me, frule.IdentityReference);
        Assert.True(frule.IsInherited);
        Assert.DoesNotContain(fileRules, r => r.IdentityReference.Value is "S-1-1-0" or "S-1-5-32-545" or "S-1-5-11");   // Everyone / Users / Authenticated Users
    }

    [Fact]
    public void ParentDirectory_IsNotTightened_OnlyTheStateDirectoryIs()
    {
        using var h = new Harness();
        h.Run(Both(10, 20), "--tap");
        DirectorySecurity parent = new DirectoryInfo(Path.GetDirectoryName(h.StateDir)!).GetAccessControl();
        Assert.False(parent.AreAccessRulesProtected);                           // %LOCALAPPDATA%\QuotaDuck keeps normal inheritance (settings.json lives there later)
    }

    [Fact]
    public void RestrictedDirectoryCreation_FailsClosed_OnAnInvalidSid()
    {
        using var t = new TempDir();
        string dir = Path.Combine(t.Path, "x");
        Assert.False(Native.TryCreateUserOnlyDirectory(dir, "not-a-sid"));
        Assert.False(Directory.Exists(dir));                                    // never created with default (broader) permissions
        Assert.False(Native.TryCreateUserOnlyDirectory(dir, "S-1-5-21-1-2-3-4)(A;;GA;;;WD"));   // SDDL injection attempt is rejected
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void ExistingStateDirectory_IsLeftAsIs_NoPermissionChurnPerInvocation()
    {
        using var h = new Harness();
        h.Run(Both(10, 20), "--tap");
        DirectorySecurity before = new DirectoryInfo(h.StateDir).GetAccessControl();
        string sddlBefore = before.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        h.Now = Epoch.AddMinutes(5);
        h.Run(Both(11, 21), "--tap");
        Assert.Equal(sddlBefore, new DirectoryInfo(h.StateDir).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    // ================= store construction =================
    // (the production LOCATION is covered by StatePathHardeningTests)

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("..\\x")]
    public void TheStore_RefusesNonAbsoluteDirectories(string dir) =>
        Assert.Throws<ArgumentException>(() => new FileClaudeStateStore(dir));

    [Fact]
    public void EachStateDirectory_HasItsOwnLock_SoIsolatedInstancesNeverContend()
    {
        using var a = new Harness();
        using var b = new Harness();
        Assert.NotEqual(a.Store.MutexName, b.Store.MutexName);
        Assert.StartsWith("Local\\", a.Store.MutexName);
    }
}
