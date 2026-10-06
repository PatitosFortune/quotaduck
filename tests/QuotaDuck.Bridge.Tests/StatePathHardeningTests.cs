using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace QuotaDuck.Bridge.Tests;

/// <summary>
/// The production state location is the user's real LocalAppData folder (from the process-user token) + a fixed
/// QuotaDuck\bridge\claude-v1.json. It must not be redirectable by any environment variable or command-line argument.
/// Finding that drove the design: Environment.GetFolderPath and SHGetKnownFolderPath (with or without a token) expand
/// %USERPROFILE% from the PROCESS ENVIRONMENT, so a plain "known folder" call is NOT enough.
/// </summary>
public class StatePathHardeningTests
{
    private static readonly string[] HostileVariables =
    [
        "LOCALAPPDATA", "USERPROFILE", "APPDATA", "HOMEDRIVE", "HOMEPATH", "HOME", "ProgramData", "PUBLIC",
        "ALLUSERSPROFILE", "TEMP", "TMP", "XDG_DATA_HOME", "QD_TEST_STATE_DIR",
    ];

    private static Dictionary<string, string> Hostile(string root) =>
        HostileVariables.ToDictionary(v => v, v => Path.Combine(root, "hostile-" + v));

    private static string Expected() =>
        StateLocation.DefaultDirectory() ?? throw new InvalidOperationException("production resolver returned null on a normal machine");

    [Fact]
    public void ProductionLocation_IsTheRealLocalAppData_PlusTheFixedRelativePath()
    {
        string expected = Expected();
        Assert.EndsWith(Path.Combine("QuotaDuck", "bridge"), expected, StringComparison.OrdinalIgnoreCase);
        Assert.True(Path.IsPathFullyQualified(expected));

        // on an unmodified environment it equals what .NET reports (sanity that we resolved the SAME real folder)
        string dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaDuck", "bridge");
        Assert.Equal(dotnet, expected, ignoreCase: true);

        // and the file name inside it is a compile-time constant
        Assert.Equal("claude-v1.json", FileClaudeStateStore.FileName);
    }

    [Fact]
    public void InProcess_HostileEnvironment_DoesNotRedirectTheResolver()
    {
        using var t = new TempDir();
        string expected = Expected();
        var saved = HostileVariables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach ((string k, string v) in Hostile(t.Path))
            {
                Environment.SetEnvironmentVariable(k, v);
            }

            string actual = Expected();
            Assert.Equal(expected, actual, ignoreCase: true);
            Assert.False(actual.StartsWith(t.Path, StringComparison.OrdinalIgnoreCase), "resolver followed a hostile environment variable");
        }
        finally
        {
            foreach ((string k, string? v) in saved)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    [Fact]
    public void FreshProcess_HostileEnvironment_FirstEverLookup_StillResolvesTheRealFolder()
    {
        // The decisive case: a brand-new process whose environment is hostile BEFORE its first folder lookup (this is exactly what
        // fooled Environment.GetFolderPath in the investigation). Run the production resolver (via the test-seam entry point) there.
        using var t = new TempDir();
        string expected = Expected();
        (int exit, byte[] stdout, byte[] stderr) = Exes.Run(Exes.TestSeam(), [], Hostile(t.Path), "--print-production-state-dir");
        Assert.Equal(0, exit);
        Assert.Empty(stderr);
        string actual = Encoding.UTF8.GetString(stdout);
        Assert.Equal(expected, actual, ignoreCase: true);
        Assert.False(actual.Contains("hostile-", StringComparison.OrdinalIgnoreCase), actual);
        Assert.False(actual.StartsWith(t.Path, StringComparison.OrdinalIgnoreCase), actual);
    }

    [Fact]
    public void FreshProcess_HostileEnvironment_EvenAfterAnotherComponentPoisonedTheShellCache_StillResolvesTheRealFolder()
    {
        // Why the resolver uses KF_FLAG_DEFAULT_PATH + the user token and cross-checks against the token's profile directory:
        // the shell caches a per-process answer, so if ANY component first calls Environment.GetFolderPath under a hostile
        // environment, a plain known-folder call from the bridge would return that poisoned value. This must still give the real path.
        using var t = new TempDir();
        string expected = Expected();
        (int exit, byte[] stdout, byte[] stderr) = Exes.Run(Exes.TestSeam(), [], Hostile(t.Path), "--print-production-state-dir-after-plain-lookup");
        Assert.Equal(0, exit);
        Assert.Empty(stderr);
        string actual = Encoding.UTF8.GetString(stdout);
        Assert.Equal(expected, actual, ignoreCase: true);
        Assert.False(actual.Contains("hostile-", StringComparison.OrdinalIgnoreCase), actual);
    }

    [Fact]
    public void ProductionExecutable_WithAHostileEnvironment_AndNothingUsableOnStdin_TouchesNoHostilePath()
    {
        // Only inputs that cannot write state are used against the production exe (its state path is the user's real one).
        using var t = new TempDir();
        string exe = Exes.Production();
        foreach (string[] args in new[] { new[] { "--tap" }, Array.Empty<string>() })
        {
            (int exit, byte[] stdout, byte[] stderr) = Exes.Run(exe, Encoding.UTF8.GetBytes("{}"), Hostile(t.Path), args);
            Assert.Equal(0, exit);
            Assert.Empty(stderr);
            if (args.Length == 0)
            {
                Assert.StartsWith("QuotaDuck | ", Encoding.UTF8.GetString(stdout));   // waiting, or whatever REAL state exists — never a hostile dir's
            }
            else
            {
                Assert.Empty(stdout);
            }
        }

        Assert.Empty(Directory.GetFileSystemEntries(t.Path));                       // nothing was created anywhere under the hostile root
    }

    [Fact]
    public void ProductionExecutable_RejectsEveryStatePathArgument_BeforeReadingOrWriting()
    {
        using var t = new TempDir();
        string exe = Exes.Production();
        foreach (string[] args in new[]
        {
            new[] { "--state-dir=" + t.Path }, new[] { "--state-dir", t.Path }, new[] { "--dir=" + t.Path }, new[] { t.Path },
            new[] { "--tap", "--output=" + Path.Combine(t.Path, "x.json") },
        })
        {
            (int exit, byte[] stdout, _) = Exes.Run(exe, Encoding.UTF8.GetBytes("{}"), new Dictionary<string, string>(), args);
            Assert.Equal(2, exit);
            Assert.Empty(stdout);
        }

        Assert.Empty(Directory.GetFileSystemEntries(t.Path));
    }

    [Fact]
    public void TestSeamExecutable_RefusesToRun_WithoutAnExplicitAbsoluteTestDirectory()
    {
        // so a test can never accidentally operate on the user's real state through the seam
        foreach (string? dir in new string?[] { null, "", "relative\\dir", "..\\x" })
        {
            var env = new Dictionary<string, string>();
            if (dir is not null)
            {
                env["QD_TEST_STATE_DIR"] = dir;
            }

            (int exit, byte[] stdout, _) = Exes.Run(Exes.TestSeam(), Encoding.UTF8.GetBytes("{}"), env, "--tap");
            Assert.Equal(3, exit);
            Assert.Empty(stdout);
        }
    }

    // ===== lazy resolution =====

    [Fact]
    public void DeferredStore_DoesNotResolveUntilUsed_AndResolvesOnlyOnce()
    {
        using var t = new TempDir();
        int calls = 0;
        var store = new DeferredStateStore(() => { calls++; return Path.Combine(t.Path, "QuotaDuck", "bridge"); });
        Assert.Equal(0, calls);                                                    // construction never resolves (no cost for no-op runs)
        Assert.Equal(QuotaDuck.Core.Claude.ClaudeState.Empty, store.Peek());
        Assert.Equal(1, calls);
        Assert.Equal(StoreStatus.Ok, store.Update(s => new StateChange(s, false)).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DeferredStore_WhenTheDirectoryCannotBeEstablished_ActsAsNoState_AndWritesNothing()
    {
        var store = new DeferredStateStore(() => null);
        Assert.Equal(QuotaDuck.Core.Claude.ClaudeState.Empty, store.Peek());
        StoreOutcome o = store.Update(s => new StateChange(s, true));
        Assert.Equal(StoreStatus.IoError, o.Status);
        Assert.False(o.Wrote);
    }

    [Fact]
    public void NothingUsable_NeverTriggersResolution()
    {
        int calls = 0;
        var store = new DeferredStateStore(() => { calls++; return null; });
        using var stdout = new MemoryStream();
        BridgeRunner.Run(["--tap"], new MemoryStream(Encoding.UTF8.GetBytes("{}")), stdout, store, () => DateTimeOffset.UtcNow);
        Assert.Equal(0, calls);
        BridgeRunner.Run(["--tap"], new MemoryStream(Encoding.UTF8.GetBytes("garbage")), stdout, store, () => DateTimeOffset.UtcNow);
        Assert.Equal(0, calls);
    }

    // ===== the defense-in-depth cross-check, tested directly (it is unreachable end-to-end once DEFAULT_PATH is used) =====

    [Theory]
    [InlineData("C:\\Users\\bob\\AppData\\Local", "C:\\Users\\bob", true)]
    [InlineData("c:\\users\\BOB\\appdata\\local\\", "C:\\Users\\bob\\", true)]
    [InlineData("C:\\Users\\bob\\AppData\\Local\\..\\Local", "C:\\Users\\bob", true)]
    [InlineData("C:\\Users\\bob", "C:\\Users\\bob", false)]                              // the profile itself is not "inside"
    [InlineData("C:\\Users\\bob2\\AppData\\Local", "C:\\Users\\bob", false)]             // prefix trick
    [InlineData("C:\\Users\\bobby", "C:\\Users\\bob", false)]
    [InlineData("C:\\hostile-USERPROFILE\\AppData\\Local", "C:\\Users\\bob", false)]
    [InlineData("C:\\Users\\bob\\..\\evil\\AppData\\Local", "C:\\Users\\bob", false)]    // traversal out of the profile
    [InlineData("D:\\Users\\bob\\AppData\\Local", "C:\\Users\\bob", false)]              // other drive
    [InlineData("\\\\server\\share\\AppData\\Local", "C:\\Users\\bob", false)]           // UNC
    [InlineData("relative\\AppData\\Local", "C:\\Users\\bob", false)]
    [InlineData("C:\\Users\\bob\\AppData\\Local", "relative", false)]
    [InlineData("", "C:\\Users\\bob", false)]
    public void ProfileCrossCheck_AcceptsOnlyProperDescendants(string folder, string profile, bool expected) =>
        Assert.Equal(expected, Native.IsStrictlyInside(folder, profile));

    // ===== structural: the production assemblies contain no environment access and no test hook at all =====

    [Fact]
    public void ProductionAssemblies_NeverReadEnvironmentVariables()
    {
        foreach (Assembly a in new[] { typeof(BridgeRunner).Assembly, typeof(QuotaDuck.Core.WindowKey).Assembly })
        {
            using FileStream fs = File.OpenRead(a.Location);
            using var pe = new PEReader(fs);
            MetadataReader md = pe.GetMetadataReader();
            foreach (MemberReferenceHandle h in md.MemberReferences)
            {
                MemberReference m = md.GetMemberReference(h);
                string name = md.GetString(m.Name);
                Assert.False(
                    name is "GetEnvironmentVariable" or "GetEnvironmentVariables" or "ExpandEnvironmentVariables" or "get_CommandLine" or "GetCommandLineArgs",
                    $"{a.GetName().Name} references {name}");
            }
        }
    }

    [Fact]
    public void ProductionAssemblies_ContainNoTestHooksOrEnvironmentVariableNames()
    {
        foreach (Assembly a in new[] { typeof(BridgeRunner).Assembly, typeof(QuotaDuck.Core.WindowKey).Assembly })
        {
            byte[] bytes = File.ReadAllBytes(a.Location);
            foreach (string needle in new[] { "QD_TEST_STATE_DIR", "QD_TEST", "TestSeam", "LOCALAPPDATA", "USERPROFILE", "QD_BRIDGE_EXE" })
            {
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(needle)) || Contains(bytes, Encoding.Unicode.GetBytes(needle)),
                    $"{a.GetName().Name} contains \"{needle}\"");
            }
        }
    }

    [Fact]
    public void ProductionProgram_UsesOnlyTheFixedResolver()
    {
        string program = File.ReadAllText(Path.Combine(BridgeArchitectureTests.RepoRoot(), "src", "QuotaDuck.Bridge", "Program.cs"));
        Assert.Contains("new DeferredStateStore(StateLocation.DefaultDirectory)", program);
        Assert.DoesNotContain("args", program.Replace("BridgeRunner.Run(args", string.Empty));   // Program.cs only forwards args to the runner
        Assert.DoesNotContain("Environment", program);
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
