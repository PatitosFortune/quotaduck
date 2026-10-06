using QuotaDuck.Core;
using QuotaDuck.Core.Codex;

namespace QuotaDuck.Codex;

public enum CodexReadFailure
{
    /// <summary>Codex is not installed for this user.</summary>
    NotFound = 1,

    /// <summary>Executable failed package/path/signature/signer validation; it was never launched.</summary>
    ValidationFailed = 2,

    /// <summary>The process could not be started.</summary>
    StartFailed = 3,

    /// <summary>No initialize reply or no quota reply within its deadline.</summary>
    Timeout = 4,

    /// <summary>The process exited / closed its output before replying.</summary>
    ProcessExited = 5,

    /// <summary>The server answered with a JSON-RPC error (only its numeric code is retained).</summary>
    Rejected = 6,

    /// <summary>A reply that is not valid JSON-RPC / not the expected shape / oversized.</summary>
    Malformed = 7,

    /// <summary>The server did something the protocol subset forbids (a server-initiated request, a reply with the wrong id, message flood).</summary>
    ProtocolViolation = 8,

    /// <summary>A well-formed reply that contains no usable quota window.</summary>
    NoQuotaData = 9,

    /// <summary>The caller cancelled. Not a provider failure: no attempt is recorded.</summary>
    Cancelled = 10,
}

/// <summary>Deadlines for one read. Defaults are generous relative to the measured ~110 ms + ~650 ms.</summary>
public sealed record CodexReadOptions(
    TimeSpan InitializeTimeout,
    TimeSpan ReadTimeout,
    TimeSpan ExitGrace)
{
    public static CodexReadOptions Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(2));
}

/// <summary>
/// The result of one read attempt. On success carries Core observations (read time = when the reply was received). On failure carries
/// NO observations, and <see cref="Attempt"/> records only that an attempt failed — a failure can never make older data look fresher.
/// </summary>
public sealed record CodexReadOutcome(
    CodexReadFailure? Failure,
    CodexObservationSet? Observations,
    DateTimeOffset At,
    int? ServerErrorCode,
    bool ProcessStarted)
{
    public bool Success => Failure is null;

    /// <summary>The attempt record for Core's provider derivation; null for caller cancellation (not a provider failure).</summary>
    public ProviderAttempt? Attempt => Failure switch
    {
        null => new ProviderAttempt(At, null),
        CodexReadFailure.Cancelled => null,
        CodexReadFailure.NotFound => new ProviderAttempt(At, ProviderErrorKind.NotFound),
        CodexReadFailure.ValidationFailed => new ProviderAttempt(At, ProviderErrorKind.ValidationFailed),
        CodexReadFailure.StartFailed => new ProviderAttempt(At, ProviderErrorKind.StartFailed),
        CodexReadFailure.Timeout => new ProviderAttempt(At, ProviderErrorKind.Timeout),
        CodexReadFailure.Rejected => new ProviderAttempt(At, ProviderErrorKind.Rejected),
        CodexReadFailure.Malformed or CodexReadFailure.ProtocolViolation or CodexReadFailure.NoQuotaData =>
            new ProviderAttempt(At, ProviderErrorKind.Malformed),
        _ => new ProviderAttempt(At, ProviderErrorKind.Unknown),
    };
}

/// <summary>
/// Reads Codex quota once, on demand. No scheduling, no polling, no persistence. One child process per read; reads are serialised.
/// <para>Network boundary: this assembly contains no networking code at all. Only the Codex child process (which QuotaDuck does not
/// control) talks to the network; see docs/codex/CODEX-RUNNER.md.</para>
/// </summary>
public sealed class CodexQuotaReader
{
    private const int MaxMessagesPerPhase = 256;

    private readonly ICodexExecutableResolver _resolver;
    private readonly IAppServerLauncher _launcher;
    private readonly Func<CodexExecutable, CodexLaunchSpec> _specFactory;
    private readonly TimeProvider _time;
    private readonly CodexReadOptions _options;
    private readonly SemaphoreSlim _one = new(1, 1);
    private int _spawned;

    /// <summary>The production reader: genuine-package discovery, the fixed launch spec, the real process launcher.</summary>
    public CodexQuotaReader(CodexReadOptions? options = null, TimeProvider? time = null)
        : this(CodexExecutableResolver.CreateDefault(), new ProcessAppServerLauncher(), CodexLaunch.Production, time ?? TimeProvider.System, options ?? CodexReadOptions.Default)
    {
    }

    internal CodexQuotaReader(
        ICodexExecutableResolver resolver,
        IAppServerLauncher launcher,
        Func<CodexExecutable, CodexLaunchSpec> specFactory,
        TimeProvider time,
        CodexReadOptions options)
    {
        _resolver = resolver;
        _launcher = launcher;
        _specFactory = specFactory;
        _time = time;
        _options = options;
    }

    /// <summary>How many app-server child processes this instance has started.</summary>
    public int ProcessesStarted => Volatile.Read(ref _spawned);

    public async Task<CodexReadOutcome> ReadAsync(CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail(CodexReadFailure.Cancelled, false);
        }
        finally
        {
            _one.Release();
        }
    }

    private CodexReadOutcome Fail(CodexReadFailure failure, bool started, int? code = null) =>
        new(failure, null, _time.GetUtcNow(), code, started);

    private async Task<CodexReadOutcome> ReadCoreAsync(CancellationToken ct)
    {
        CodexDiscoveryResult found = await Task.Run(_resolver.Resolve, ct).ConfigureAwait(false);
        if (found.Status != CodexDiscoveryStatus.Ok || found.Executable is null)
        {
            return Fail(found.Status == CodexDiscoveryStatus.NotInstalled ? CodexReadFailure.NotFound : CodexReadFailure.ValidationFailed, false);
        }

        CodexLaunchSpec spec = _specFactory(found.Executable);
        IAppServerProcess process;
        try
        {
            process = _launcher.Start(spec);
        }
        catch (AppServerStartException)
        {
            return Fail(CodexReadFailure.StartFailed, false);
        }

        Interlocked.Increment(ref _spawned);
        bool graceful = false;
        try
        {
            CodexReadOutcome outcome = await ConverseAsync(process, ct).ConfigureAwait(false);
            graceful = outcome.Success;
            return outcome;
        }
        finally
        {
            await ShutDownAsync(process, graceful).ConfigureAwait(false);
        }
    }

    private async Task<CodexReadOutcome> ConverseAsync(IAppServerProcess process, CancellationToken ct)
    {
        var input = new AppServerInput(process.Input);
        var lines = new BoundedLineReader(process.Output, CodexResponseParser.MaxLineBytes);

        try
        {
            await input.SendAsync(OutboundMessage.Initialize, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return Fail(CodexReadFailure.ProcessExited, true);
        }

        (CodexMessage? init, CodexReadFailure? initFail) =
            await AwaitReplyAsync(lines, CodexProtocol.InitializeId, _options.InitializeTimeout, ct).ConfigureAwait(false);
        if (initFail is { } f1)
        {
            return Fail(f1, true, init?.ErrorCode);
        }

        try
        {
            await input.SendAsync(OutboundMessage.Initialized, ct).ConfigureAwait(false);
            await input.SendAsync(OutboundMessage.ReadRateLimits, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return Fail(CodexReadFailure.ProcessExited, true);
        }

        (CodexMessage? reply, CodexReadFailure? readFail) =
            await AwaitReplyAsync(lines, CodexProtocol.ReadRateLimitsId, _options.ReadTimeout, ct).ConfigureAwait(false);
        if (readFail is { } f2)
        {
            return Fail(f2, true, reply?.ErrorCode);
        }

        DateTimeOffset readAt = _time.GetUtcNow();
        CodexObservationSet set = CodexObservations.FromRead(reply!.Buckets, readAt);
        if (set.Windows.Count == 0)
        {
            return Fail(CodexReadFailure.NoQuotaData, true);
        }

        return new CodexReadOutcome(null, set, readAt, null, true);
    }

    /// <summary>Waits for the reply carrying <paramref name="expectedId"/>. Notifications are ignored unread; anything else unexpected is a failure.</summary>
    private static async Task<(CodexMessage? Message, CodexReadFailure? Failure)> AwaitReplyAsync(
        BoundedLineReader lines, long expectedId, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            for (int seen = 0; seen < MaxMessagesPerPhase; seen++)
            {
                LineResult r = await lines.ReadLineAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                if (r.Kind == LineKind.Eof)
                {
                    return (null, CodexReadFailure.ProcessExited);
                }

                if (r.Kind == LineKind.TooLong)
                {
                    return (null, CodexReadFailure.Malformed);
                }

                if (r.Line.Length == 0)
                {
                    continue;
                }

                CodexMessage m = CodexResponseParser.Parse(r.Line.Span);
                switch (m.Kind)
                {
                    case CodexMessageKind.Notification:
                        continue;
                    case CodexMessageKind.Malformed:
                        return (null, CodexReadFailure.Malformed);
                    case CodexMessageKind.ServerRequest:
                        return (null, CodexReadFailure.ProtocolViolation);   // never answered, never acted on
                    default:
                        if (m.Id != expectedId)
                        {
                            return (null, CodexReadFailure.ProtocolViolation);
                        }

                        return m.Kind == CodexMessageKind.ErrorResponse ? (m, CodexReadFailure.Rejected) : (m, null);
                }
            }

            return (null, CodexReadFailure.ProtocolViolation);               // message flood without the awaited reply
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, CodexReadFailure.Timeout);
        }
        catch (IOException)
        {
            return (null, CodexReadFailure.ProcessExited);
        }
        catch (ObjectDisposedException)
        {
            return (null, CodexReadFailure.ProcessExited);
        }
    }

    /// <summary>Success: close stdin and give the child a short grace to exit by itself. Everything else (and the grace timing out): kill the whole tree.</summary>
    private async Task ShutDownAsync(IAppServerProcess process, bool graceful)
    {
        try
        {
            new AppServerInput(process.Input).Close();
            if (!graceful || !await process.WaitForExitAsync(_options.ExitGrace).ConfigureAwait(false))
            {
                process.KillTree();
                await process.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        finally
        {
            process.Dispose();
        }
    }
}
