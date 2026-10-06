using System.Diagnostics;
using System.Text;
using QuotaDuck.Core.Codex;

// Real child processes + process-wide environment changes: run the whole assembly sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace QuotaDuck.Codex.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qd-codex-tests", Guid.NewGuid().ToString("N")[..10]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(Path); attempt++)
        {
            try
            {
                // junctions are removed as links first so the recursive delete can never follow (or choke on) them
                foreach (string d in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).ToArray())
                {
                    if (Directory.Exists(d) && new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        Directory.Delete(d);
                    }
                }

                Directory.Delete(Path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);                  // a just-killed child may still hold its working files for a moment
            }
        }
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class StubResolver(Func<CodexDiscoveryResult> resolve) : ICodexExecutableResolver
{
    public int Calls { get; private set; }

    public CodexDiscoveryResult Resolve()
    {
        Calls++;
        return resolve();
    }
}

internal sealed class ThrowingLauncher : IAppServerLauncher
{
    public IAppServerProcess Start(CodexLaunchSpec spec) => throw new InvalidOperationException("the launcher must not be called");
}

/// <summary>Wraps the real launcher to observe lifetime/concurrency.</summary>
internal sealed class CountingLauncher : IAppServerLauncher
{
    private readonly ProcessAppServerLauncher _inner = new();
    private int _active;

    public int Started { get; private set; }

    public int MaxConcurrent { get; private set; }

    public IAppServerProcess Start(CodexLaunchSpec spec)
    {
        IAppServerProcess p = _inner.Start(spec);
        Started++;
        int now = Interlocked.Increment(ref _active);
        MaxConcurrent = Math.Max(MaxConcurrent, now);
        return new Tracked(p, () => Interlocked.Decrement(ref _active));
    }

    private sealed class Tracked(IAppServerProcess inner, Action onDispose) : IAppServerProcess
    {
        public Stream Input => inner.Input;

        public Stream Output => inner.Output;

        public bool HasExited => inner.HasExited;

        public long StderrBytesDiscarded => inner.StderrBytesDiscarded;

        public Task<bool> WaitForExitAsync(TimeSpan timeout) => inner.WaitForExitAsync(timeout);

        public void KillTree() => inner.KillTree();

        public void Dispose()
        {
            inner.Dispose();
            onDispose();
        }
    }
}

/// <summary>
/// Runs the REAL runner (real process launcher, real pipes) against the fake app-server, which is started through the dotnet host with
/// the production argument list + test-only scenario arguments and the production environment (SystemRoot only).
/// </summary>
internal sealed class FakeRig : IDisposable
{
    public TempDir Dir { get; } = new();

    public string Scenario { get; set; } = "normal";

    public string? PayloadFile { get; set; }

    public string? PreFile { get; set; }

    public string Record => Dir.File("record.txt");

    public FixedClock Clock { get; } = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero) - TimeSpan.FromHours(2));

    public CountingLauncher Launcher { get; } = new();

    public StubResolver Resolver { get; }

    public CodexLaunchSpec? LastSpec { get; private set; }

    public FakeRig()
    {
        Resolver = new StubResolver(() => new CodexDiscoveryResult(
            CodexDiscoveryStatus.Ok, new CodexExecutable(HostPath(), "Fake.Package_1.0.0.0_x64__0000000000000", "1.0.0.0")));
    }

    public CodexReadOptions Options { get; set; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3));

    public CodexLaunchSpec SpecFor(CodexExecutable exe)
    {
        var args = new List<string> { FakeDll() };
        args.AddRange(CodexLaunch.ProductionArguments);
        args.AddRange(["--qd-scenario", Scenario, "--qd-record", Record]);
        if (PayloadFile is not null) args.AddRange(["--qd-payload", PayloadFile]);
        if (PreFile is not null) args.AddRange(["--qd-pre", PreFile]);
        LastSpec = new CodexLaunchSpec(exe.Path, args, CodexLaunch.ProductionWorkingDirectory(), CodexLaunch.ProductionEnvironment());
        return LastSpec;
    }

    public CodexQuotaReader Reader() => new(Resolver, Launcher, SpecFor, Clock, Options);

    public IReadOnlyList<string> RecordLines() => File.Exists(Record) ? File.ReadAllLines(Record) : [];

    public byte[] StdinBytes() => File.Exists(Record + ".stdin") ? File.ReadAllBytes(Record + ".stdin") : [];

    public int ChildPid() => PidOf("PID:");

    public int GrandchildPid() => PidOf("CHILDPID:");

    private int PidOf(string prefix)
    {
        for (int i = 0; i < 100; i++)
        {
            string? l = RecordLines().FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
            if (l is not null) return int.Parse(l[prefix.Length..]);
            Thread.Sleep(20);
        }

        throw new InvalidOperationException("no " + prefix + " record");
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool WaitDead(int pid, int ms = 4000)
    {
        for (int waited = 0; waited < ms; waited += 50)
        {
            if (!IsAlive(pid)) return true;
            Thread.Sleep(50);
        }

        return !IsAlive(pid);
    }

    public static string Expected3Messages =>
        Encoding.UTF8.GetString(CodexProtocol.Bytes(OutboundMessage.Initialize)) +
        Encoding.UTF8.GetString(CodexProtocol.Bytes(OutboundMessage.Initialized)) +
        Encoding.UTF8.GetString(CodexProtocol.Bytes(OutboundMessage.ReadRateLimits));

    public static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir, "QuotaDuck.sln")))
        {
            dir = System.IO.Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>The dotnet host that runs the fake (absolute .exe; the fake itself is a managed dll).</summary>
    public static string HostPath()
    {
        string? fromCli = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromCli) && File.Exists(fromCli)) return fromCli;
        string local = System.IO.Path.Combine(RepoRoot(), ".tools", "dotnet", "dotnet.exe");
        Assert.True(File.Exists(local), "dotnet host not found");
        return local;
    }

    public static string FakeDll()
    {
        string root = RepoRoot();
        string[] candidates = new[] { "Release", "Debug" }
            .Select(c => System.IO.Path.Combine(root, "tests", "QuotaDuck.Codex.FakeServer", "bin", c, "net10.0", "quotaduck-codex-fake.dll"))
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        Assert.True(candidates.Length > 0, "fake server not built; build the solution first");
        return candidates[0];
    }

    public void Dispose() => Dir.Dispose();
}
