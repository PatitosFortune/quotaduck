using System.Text;
using QuotaDuck.Core.Claude;
using static QuotaDuck.Core.Tests.PayloadFx;

namespace QuotaDuck.Bridge.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qd-bridge-tests", Guid.NewGuid().ToString("N")[..10]);

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>An isolated "LOCALAPPDATA" with a real file store, a controllable clock and write counting.</summary>
internal sealed class Harness : IDisposable
{
    private readonly TempDir _root = new();

    public Harness(TimeSpan? lockTimeout = null, Action<string>? beforeReplace = null)
    {
        Store = new FileClaudeStateStore(
            StateDir,
            new StoreOptions
            {
                LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5),
                BeforeReplace = t =>
                {
                    Writes++;
                    if (FailNextReplace)
                    {
                        FailNextReplace = false;
                        throw new IOException("injected failure after temp file was written");
                    }

                    beforeReplace?.Invoke(t);
                },
            });
    }

    public string Root => _root.Path;

    public string StateDir => Path.Combine(_root.Path, "QuotaDuck", "bridge");

    public string StateFile => Path.Combine(StateDir, FileClaudeStateStore.FileName);

    public FileClaudeStateStore Store { get; }

    public DateTimeOffset Now { get; set; } = Epoch;

    public int Writes { get; private set; }

    public bool FailNextReplace { get; set; }

    public (int Exit, byte[] Stdout) Run(string payload, params string[] args) => Run(Encoding.UTF8.GetBytes(payload), args);

    public (int Exit, byte[] Stdout) Run(byte[] payload, params string[] args)
    {
        using var stdin = new MemoryStream(payload);
        using var stdout = new MemoryStream();
        int code = BridgeRunner.Run(args, stdin, stdout, Store, () => Now);
        return (code, stdout.ToArray());
    }

    public string RunText(string payload, params string[] args) => Encoding.UTF8.GetString(Run(payload, args).Stdout).TrimEnd('\n');

    public byte[]? FileBytes() => File.Exists(StateFile) ? File.ReadAllBytes(StateFile) : null;

    public ClaudeState State()
    {
        byte[]? b = FileBytes();
        Assert.NotNull(b);
        ClaudeStateDecodeResult r = ClaudeStateCodec.Decode(b);
        Assert.Equal(ClaudeStateDecodeStatus.Ok, r.Status);
        return r.State;
    }

    public string[] FilesInStateDir() => Directory.Exists(StateDir) ? Directory.GetFiles(StateDir).Select(f => Path.GetFileName(f)!).Order().ToArray() : [];

    public void Dispose() => _root.Dispose();

    // ---- synthetic payload helpers (all relative to the harness epoch) ----
    public static string Five(double pct, double resetHours = 3) => Win(pct, Epoch.AddHours(resetHours));

    public static string Seven(double pct, double resetHours = 24 * 7) => Win(pct, Epoch.AddHours(resetHours));

    public static string Both(double five, double seven) => Payload(Five(five), Seven(seven));
}

/// <summary>A stream that produces N bytes of whitespace and records how many were actually read.</summary>
internal sealed class EndlessStream(long length) : Stream
{
    public long BytesRead { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long remaining = length - BytesRead;
        if (remaining <= 0)
        {
            return 0;
        }

        int n = (int)Math.Min(count, remaining);
        Array.Fill(buffer, (byte)' ', offset, n);
        BytesRead += n;
        return n;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class ExplodingStream : Stream
{
    public bool ReadAttempted { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadAttempted = true;
        throw new InvalidOperationException("stdin must not be read");
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
