namespace QuotaDuck.Codex;

/// <summary>
/// The ONLY messages QuotaDuck can ever send to <c>codex app-server</c>. There is deliberately no API that takes caller-supplied text
/// or bytes: a message is one of these three enum values and its bytes are compile-time constants (UTF-8, no BOM, LF-terminated,
/// first byte '{').
/// </summary>
internal enum OutboundMessage
{
    Initialize = 1,
    Initialized = 2,
    ReadRateLimits = 3,
}

internal static class CodexProtocol
{
    public const long InitializeId = 1;
    public const long ReadRateLimitsId = 2;

    public static ReadOnlySpan<byte> Bytes(OutboundMessage message) => message switch
    {
        OutboundMessage.Initialize =>
            "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"quotaduck\",\"title\":\"QuotaDuck\",\"version\":\"0.1.0\"}}}\n"u8,
        OutboundMessage.Initialized => "{\"method\":\"initialized\"}\n"u8,
        OutboundMessage.ReadRateLimits => "{\"id\":2,\"method\":\"account/rateLimits/read\"}\n"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(message)),
    };
}

/// <summary>Write side of the child's stdin. Exposes ONLY <see cref="SendAsync"/> of a fixed <see cref="OutboundMessage"/>.</summary>
internal sealed class AppServerInput(Stream stdin)
{
    public async Task SendAsync(OutboundMessage message, CancellationToken ct)
    {
        byte[] bytes = CodexProtocol.Bytes(message).ToArray();
        await stdin.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stdin.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Closing stdin is the polite "we are done" signal to the child.</summary>
    public void Close()
    {
        try
        {
            stdin.Dispose();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal enum LineKind
{
    Line = 0,
    Eof = 1,
    TooLong = 2,
}

internal readonly record struct LineResult(LineKind Kind, ReadOnlyMemory<byte> Line);

/// <summary>Bounded newline-delimited reader over the child's stdout. Handles fragmented writes; a line longer than the cap aborts the read.</summary>
internal sealed class BoundedLineReader(Stream source, int maxLineBytes)
{
    private byte[] _buf = new byte[8192];
    private int _start;
    private int _end;

    public async Task<LineResult> ReadLineAsync(CancellationToken ct)
    {
        int scanned = 0;
        while (true)
        {
            int nl = scanned < _end - _start ? Array.IndexOf(_buf, (byte)'\n', _start + scanned, _end - _start - scanned) : -1;
            if (nl >= 0)
            {
                int lineEnd = nl > _start && _buf[nl - 1] == (byte)'\r' ? nl - 1 : nl;
                if (lineEnd - _start > maxLineBytes)
                {
                    return new LineResult(LineKind.TooLong, default);
                }

                var line = new ReadOnlyMemory<byte>(_buf, _start, lineEnd - _start);
                _start = nl + 1;
                return new LineResult(LineKind.Line, line);
            }

            scanned = _end - _start;
            if (scanned > maxLineBytes + 1)
            {
                return new LineResult(LineKind.TooLong, default);
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buf.Length)
            {
                Array.Resize(ref _buf, Math.Min(_buf.Length * 2, maxLineBytes + 8192));
            }

            int n = await source.ReadAsync(_buf.AsMemory(_end), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return new LineResult(LineKind.Eof, default);           // a trailing partial line is discarded, never parsed
            }

            _end += n;
        }
    }
}
