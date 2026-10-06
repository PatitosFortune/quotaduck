using System.Security.Principal;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.Bridge;

internal enum StoreStatus
{
    Ok = 0,

    /// <summary>Another bridge held the lock too long; this run skipped its write (the next status-line run catches up).</summary>
    LockTimeout,

    /// <summary>The state file belongs to a newer schema and is left untouched.</summary>
    FutureSchema,

    /// <summary>Any I/O failure. The previous state file is intact (atomic replace); nothing was half-written.</summary>
    IoError,
}

internal readonly record struct StateChange(ClaudeState State, bool Write);

internal sealed record StoreOutcome(StoreStatus Status, ClaudeState Final, bool Wrote);

internal interface IClaudeStateStore
{
    /// <summary>Lock-free best-effort read for display purposes only. Never throws.</summary>
    ClaudeState Peek();

    /// <summary>Serialised read-modify-write. <paramref name="mutate"/> receives the current state (empty if missing/corrupt).</summary>
    StoreOutcome Update(Func<ClaudeState, StateChange> mutate);
}

internal sealed record StoreOptions
{
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>TEST SEAM (null in production): invoked after the temp file is fully written, just before the atomic replace.</summary>
    public Action<string>? BeforeReplace { get; init; }
}

/// <summary>
/// The bridge's only persistence: one fixed file in one fixed directory.
/// <list type="bullet">
/// <item>Directory is created with a protected, current-user-only DACL (files inside inherit it). If that cannot be
/// established the store FAILS CLOSED: it never creates the directory with broader permissions and never writes.</item>
/// <item>Serialised across processes with a named mutex (bounded wait; on timeout the run skips its write, never blocking Claude Code).</item>
/// <item>Write = temp file in the same directory (CreateNew, no sharing, write-through, flushed to disk) then atomic replace;
/// on any failure the temp file is deleted and the previous file is untouched. Stale temp files from a crashed run are removed
/// under the lock. No backups, no history, no log.</item>
/// <item>Read is bounded to <see cref="ClaudeStateCodec.MaxBytes"/>; corrupt/oversized/empty state is treated as empty
/// (self-heals on the next write); a FUTURE-schema file is never overwritten.</item>
/// </list>
/// </summary>
internal sealed class FileClaudeStateStore : IClaudeStateStore
{
    public const string FileName = "claude-v1.json";

    private readonly string _dir;
    private readonly string _path;
    private readonly string _mutexName;
    private readonly StoreOptions _options;

    public FileClaudeStateStore(string stateDirectory, StoreOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory) || !Path.IsPathFullyQualified(stateDirectory))
        {
            throw new ArgumentException("state directory must be a fully-qualified path", nameof(stateDirectory));
        }

        _dir = Path.GetFullPath(stateDirectory);
        _path = Path.Combine(_dir, FileName);
        _options = options ?? new StoreOptions();
        _mutexName = @"Local\QuotaDuck.Claude." + Fnv1a(_dir.ToUpperInvariant()).ToString("x16");
    }

    public string StateFilePath => _path;

    /// <summary>Name of the cross-process lock (derived from the directory, so each state directory has its own).</summary>
    public string MutexName => _mutexName;

    public ClaudeState Peek()
    {
        try
        {
            ClaudeStateDecodeResult r = ReadState();
            return r.Status == ClaudeStateDecodeStatus.Ok ? r.State : ClaudeState.Empty;
        }
        catch (Exception)
        {
            return ClaudeState.Empty;
        }
    }

    public StoreOutcome Update(Func<ClaudeState, StateChange> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        Mutex? mutex = null;
        bool owned = false;
        try
        {
            mutex = new Mutex(false, _mutexName);
            try
            {
                owned = mutex.WaitOne(_options.LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                owned = true;                  // a previous holder died; the data on disk is still consistent (atomic replace)
            }

            if (!owned)
            {
                return new StoreOutcome(StoreStatus.LockTimeout, Peek(), false);
            }

            EnsureDirectory();
            ClaudeStateDecodeResult decoded = ReadState();
            if (decoded.Status == ClaudeStateDecodeStatus.FutureSchema)
            {
                return new StoreOutcome(StoreStatus.FutureSchema, ClaudeState.Empty, false);
            }

            ClaudeState current = decoded.Status == ClaudeStateDecodeStatus.Ok ? decoded.State : ClaudeState.Empty;
            StateChange change = mutate(current);
            if (!change.Write)
            {
                return new StoreOutcome(StoreStatus.Ok, change.State, false);
            }

            RemoveStaleTemps();
            WriteAtomic(ClaudeStateCodec.Encode(change.State));
            return new StoreOutcome(StoreStatus.Ok, change.State, true);
        }
        catch (Exception)
        {
            return new StoreOutcome(StoreStatus.IoError, ClaudeState.Empty, false);
        }
        finally
        {
            if (owned)
            {
                try
                {
                    mutex!.ReleaseMutex();
                }
                catch (Exception)
                {
                    // nothing useful to do; never throw out of the status line
                }
            }

            mutex?.Dispose();
        }
    }

    private void EnsureDirectory()
    {
        if (Directory.Exists(_dir))
        {
            return;
        }

        string? parent = Path.GetDirectoryName(_dir);
        if (string.IsNullOrEmpty(parent))
        {
            throw new IOException("no parent directory");
        }

        Directory.CreateDirectory(parent);     // e.g. %LOCALAPPDATA%\QuotaDuck (normal inherited permissions)
        string? sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            sid = identity.User?.Value;
        }

        if (sid is null || !Native.TryCreateUserOnlyDirectory(_dir, sid))
        {
            throw new IOException("could not create the state directory with a user-only DACL");   // fail closed
        }
    }

    private ClaudeStateDecodeResult ReadState()
    {
        var info = new FileInfo(_path);
        if (!info.Exists)
        {
            return new(ClaudeStateDecodeStatus.Empty, ClaudeState.Empty, 0);
        }

        if (info.Length > ClaudeStateCodec.MaxBytes)
        {
            return new(ClaudeStateDecodeStatus.TooLarge, ClaudeState.Empty, 0);
        }

        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            var buffer = new byte[ClaudeStateCodec.MaxBytes + 1];
            int total = 0, n;
            while (total < buffer.Length && (n = fs.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += n;
            }

            return ClaudeStateCodec.Decode(buffer.AsSpan(0, total));
        }
        catch (FileNotFoundException)
        {
            return new(ClaudeStateDecodeStatus.Empty, ClaudeState.Empty, 0);
        }
        catch (DirectoryNotFoundException)
        {
            return new(ClaudeStateDecodeStatus.Empty, ClaudeState.Empty, 0);
        }
    }

    private void WriteAtomic(byte[] bytes)
    {
        string tmp = Path.Combine(_dir, FileName + "." + Guid.NewGuid().ToString("N")[..12] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            _options.BeforeReplace?.Invoke(tmp);
            File.Move(tmp, _path, overwrite: true);   // MoveFileEx(REPLACE_EXISTING): the destination is never observed half-written
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Under the lock any leftover temp file is by definition from a crashed writer.</summary>
    private void RemoveStaleTemps()
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(_dir, FileName + ".*.tmp"))
            {
                TryDelete(f);
            }
        }
        catch (Exception)
        {
            // best effort only
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // best effort only
        }
    }

    private static ulong Fnv1a(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s)
        {
            h = (h ^ c) * 1099511628211UL;
        }

        return h;
    }
}

/// <summary>
/// Resolves the state directory only when the store is first needed. Invocations that never touch state (e.g. <c>--tap</c> with
/// nothing usable in the payload) therefore never pay for the token/known-folder lookup. If the directory cannot be established the
/// store behaves as "no state": Peek is empty and Update reports an I/O error without writing anything.
/// </summary>
internal sealed class DeferredStateStore(Func<string?> resolveDirectory) : IClaudeStateStore
{
    private IClaudeStateStore? _inner;
    private bool _resolved;

    private IClaudeStateStore? Inner()
    {
        if (!_resolved)
        {
            _resolved = true;
            string? dir = resolveDirectory();
            _inner = dir is null ? null : new FileClaudeStateStore(dir);
        }

        return _inner;
    }

    public ClaudeState Peek() => Inner()?.Peek() ?? ClaudeState.Empty;

    public StoreOutcome Update(Func<ClaudeState, StateChange> mutate) =>
        Inner()?.Update(mutate) ?? new StoreOutcome(StoreStatus.IoError, ClaudeState.Empty, false);
}

internal static class StateLocation
{
    /// <summary>
    /// The production state directory: &lt;the user's real LocalAppData&gt;\QuotaDuck\bridge, with the file name fixed by
    /// <see cref="FileClaudeStateStore.FileName"/>. The root comes from the process-user TOKEN (known-folder default path
    /// cross-checked against the token's profile directory) — NOT from LOCALAPPDATA or any other environment variable — and there is
    /// no command-line override and no provider-supplied path. Returns null (=> nothing is written) if it cannot be established.
    /// Tests inject their own directory into <see cref="FileClaudeStateStore"/> directly; this method is never given a path.
    /// </summary>
    public static string? DefaultDirectory() =>
        Native.TryGetLocalAppData(out string root) ? Path.Combine(root, "QuotaDuck", "bridge") : null;
}
