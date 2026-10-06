using System.Diagnostics;
using System.Text;

namespace QuotaDuck.Codex;

/// <summary>Everything that defines how the child is started. Production specs come only from <see cref="CodexLaunch.Production"/>.</summary>
internal sealed record CodexLaunchSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

internal static class CodexLaunch
{
    /// <summary>
    /// <c>app-server --stdio</c> over stdio only (never <c>--listen</c>, <c>proxy</c>, <c>daemon</c>, <c>exec</c>), with Codex's own analytics
    /// switched off for this child (verified valid; not <c>--strict-config</c>, which would make a future config change fatal).
    /// </summary>
    public static readonly IReadOnlyList<string> ProductionArguments = ["app-server", "--stdio", "-c", "analytics.enabled=false"];

    /// <summary>
    /// The complete child environment: <c>SystemRoot</c> and nothing else (verified minimal by Step-0 spike 3; an empty environment
    /// fails). No PATH, no HOME/USERPROFILE/CODEX_*, no proxy or API-key variables are inherited. The value comes from the Windows
    /// folder API, not from our own environment.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ProductionEnvironment() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows),
        };

    /// <summary>The child's working directory: the system directory, so no project-local Codex configuration can be picked up from it.</summary>
    public static string ProductionWorkingDirectory() => System.Environment.SystemDirectory;

    public static CodexLaunchSpec Production(CodexExecutable exe) =>
        new(exe.Path, ProductionArguments, ProductionWorkingDirectory(), ProductionEnvironment());
}

/// <summary>A started app-server child. Raw streams are handed only to the restricted channel types, never to callers.</summary>
internal interface IAppServerProcess : IDisposable
{
    Stream Input { get; }

    Stream Output { get; }

    bool HasExited { get; }

    /// <summary>Total stderr bytes drained and discarded (content is never kept).</summary>
    long StderrBytesDiscarded { get; }

    Task<bool> WaitForExitAsync(TimeSpan timeout);

    /// <summary>Kills the child and every process it started.</summary>
    void KillTree();
}

internal interface IAppServerLauncher
{
    /// <summary>Starts the child. Throws <see cref="AppServerStartException"/> if it cannot be started.</summary>
    IAppServerProcess Start(CodexLaunchSpec spec);
}

internal sealed class AppServerStartException(Exception inner) : Exception("app-server could not be started", inner);

/// <summary>
/// The one place a process is launched: fully-qualified executable, <c>UseShellExecute=false</c>, constant <c>ArgumentList</c> (never a command
/// line string), explicit working directory, cleared + explicit environment, redirected binary stdio (so no encoding preamble can ever be
/// written), no window.
/// </summary>
internal sealed class ProcessAppServerLauncher : IAppServerLauncher
{
    public IAppServerProcess Start(CodexLaunchSpec spec)
    {
        if (!Path.IsPathFullyQualified(spec.ExecutablePath))
        {
            throw new AppServerStartException(new ArgumentException("executable path must be fully qualified"));
        }

        var psi = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string a in spec.Arguments)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment.Clear();
        foreach ((string k, string v) in spec.Environment)
        {
            psi.Environment[k] = v;
        }

        try
        {
            Process p = Process.Start(psi) ?? throw new InvalidOperationException("no process");
            return new ChildProcess(p);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new AppServerStartException(e);
        }
    }

    private sealed class ChildProcess : IAppServerProcess
    {
        private readonly Process _p;
        private readonly Task _stderrDrain;
        private long _stderrBytes;

        public ChildProcess(Process p)
        {
            _p = p;
            _stderrDrain = Task.Run(DrainStderrAsync);
        }

        public Stream Input => _p.StandardInput.BaseStream;

        public Stream Output => _p.StandardOutput.BaseStream;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _p.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public long StderrBytesDiscarded => Interlocked.Read(ref _stderrBytes);

        public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await _p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public void KillTree()
        {
            try
            {
                if (!_p.HasExited)
                {
                    _p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
            {
                // already gone, or access race at exit: nothing left to do
            }
        }

        public void Dispose()
        {
            KillTree();                                         // never leave a child behind, whatever path got us here
            try
            {
                _stderrDrain.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            _p.Dispose();
        }

        // stderr is read only so the child can never block on a full pipe; its content is discarded unseen and never logged.
        private async Task DrainStderrAsync()
        {
            var buf = new byte[4096];
            try
            {
                Stream s = _p.StandardError.BaseStream;
                int n;
                while ((n = await s.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    Interlocked.Add(ref _stderrBytes, n);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }
}
