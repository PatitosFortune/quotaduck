using QuotaDuck.Core.Claude;

namespace QuotaDuck.Coordinator;

/// <summary>
/// Read-only reader of the bridge's state file: bounded (<see cref="ClaudeStateCodec.MaxBytes"/> + 1), shared-read so it never blocks the
/// bridge's atomic replace, never creates, writes, locks or lists anything. The decoded values are exactly what the bridge persisted:
/// rereading changes nothing about freshness (Core derives that from the stored ChangedAt/ReceivedAt, not from when we looked).
/// </summary>
internal sealed class FileClaudeStateSource(string directory) : IClaudeStateSource
{
    private readonly string _path = Path.Combine(directory, ClaudeStateLocation.FileName);

    public ClaudeSourceRead Read()
    {
        byte[] buffer;
        int length;
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            buffer = new byte[ClaudeStateCodec.MaxBytes + 1];
            length = 0;
            int n;
            while (length < buffer.Length && (n = fs.Read(buffer, length, buffer.Length - length)) > 0)
            {
                length += n;
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ClaudeSourceRead(ClaudeSourceStatus.Absent, ClaudeState.Empty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new ClaudeSourceRead(ClaudeSourceStatus.Unreadable, ClaudeState.Empty);
        }

        ClaudeStateDecodeResult r = ClaudeStateCodec.Decode(buffer.AsSpan(0, length));
        ClaudeSourceStatus status = r.Status switch
        {
            ClaudeStateDecodeStatus.Ok => ClaudeSourceStatus.Ok,
            ClaudeStateDecodeStatus.Empty => ClaudeSourceStatus.Empty,
            ClaudeStateDecodeStatus.FutureSchema => ClaudeSourceStatus.FutureSchema,
            _ => ClaudeSourceStatus.Corrupt,
        };
        return new ClaudeSourceRead(status, status == ClaudeSourceStatus.Ok ? r.State : ClaudeState.Empty);
    }
}

/// <summary>
/// A FileSystemWatcher on the bridge directory, filtered to the state file's exact name (so the bridge's temp files are ignored; the atomic
/// replace surfaces as Renamed/Changed/Created for the final name). No scanning. If the directory does not exist yet, <see cref="TryAttach"/>
/// is a single existence check. An overflow/error or the directory disappearing marks the watcher detached and raises a (spurious) change so
/// the coordinator rereads and re-attaches.
/// </summary>
internal sealed class FileSystemClaudeStateWatcher(string directory) : IClaudeStateWatcher
{
    private readonly object _gate = new();
    private FileSystemWatcher? _w;
    private bool _disposed;

    public event Action? Changed;

    public bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                return _w is not null;
            }
        }
    }

    public bool TryAttach()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_w is not null)
            {
                if (Directory.Exists(directory))
                {
                    return true;
                }

                // the watched directory vanished (a watcher on a deleted directory can go silent): drop it and report detached
                FileSystemWatcher dead = _w;
                _w = null;
                try
                {
                    dead.Dispose();
                }
                catch (Exception e) when (e is ObjectDisposedException or IOException)
                {
                }

                return false;
            }

            try
            {
                if (!Directory.Exists(directory))
                {
                    return false;
                }

                var w = new FileSystemWatcher(directory, ClaudeStateLocation.FileName)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    InternalBufferSize = 8192,
                };
                w.Created += OnEvent;
                w.Changed += OnEvent;
                w.Deleted += OnEvent;
                w.Renamed += OnEvent;
                w.Error += OnError;
                w.EnableRaisingEvents = true;
                _w = w;
                return true;
            }
            catch (Exception e) when (e is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Changed?.Invoke();

    private void OnError(object sender, ErrorEventArgs e)
    {
        Detach();
        Changed?.Invoke();                      // notifications may have been lost: force a reread
    }

    private void Detach()
    {
        FileSystemWatcher? w;
        lock (_gate)
        {
            w = _w;
            _w = null;
        }

        try
        {
            w?.Dispose();
        }
        catch (Exception e) when (e is ObjectDisposedException or IOException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Detach();
    }
}
