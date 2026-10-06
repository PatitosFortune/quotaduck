using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QuotaDuck.Codex;

/// <summary>
/// The single P/Invoke surface of the Codex runner: package lookup by family (kernel32), final-path canonicalisation and the
/// embedded-Authenticode check (wintrust). Nothing here executes, reads or writes a file's contents.
/// </summary>
internal static unsafe partial class Native
{
    public const int ErrorInsufficientBuffer = 122;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, nint* packageFullNames, ref uint bufferLength, char* buffer);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetPackagePathByFullName(string packageFullName, ref uint pathLength, char* path);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* buffer, uint length, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint window, ref Guid action, ref WinTrustData data);

    /// <summary>
    /// The final on-disk path of an existing file or directory (junctions, symlinks and 8.3 names resolved), or null when it cannot be
    /// opened or is not a local drive-letter path (UNC/device paths are refused).
    /// </summary>
    public static string? TryGetFinalPath(string path)
    {
        // FILE_READ_ATTRIBUTES, share all, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS (needed to open directories)
        using SafeFileHandle h = CreateFile(path, 0x80, 7, 0, 3, 0x02000000, 0);
        if (h.IsInvalid)
        {
            return null;
        }

        char* buf = stackalloc char[1024];
        uint n = GetFinalPathNameByHandle(h, buf, 1024, 0);
        if (n == 0 || n >= 1024)
        {
            return null;
        }

        string s = new(buf, 0, (int)n);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (s.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            s = s[4..];
        }

        return s.Length > 2 && s[1] == ':' && s[2] == '\\' ? s : null;
    }

    /// <summary>
    /// Embedded-Authenticode verification. No UI, revocation checking OFF and URL retrieval cache-only, so this never makes QuotaDuck
    /// (or the OS on its behalf) go to the network. Returns 0 on success, otherwise the HRESULT.
    /// </summary>
    public static int VerifyEmbeddedSignature(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
        nint pPath = Marshal.StringToCoTaskMemUni(path);
        nint pFile = Marshal.AllocCoTaskMem(sizeof(WinTrustFileInfo));
        try
        {
            *(WinTrustFileInfo*)pFile = new WinTrustFileInfo { Size = (uint)sizeof(WinTrustFileInfo), FilePath = pPath };
            var data = new WinTrustData
            {
                Size = (uint)sizeof(WinTrustData),
                UiChoice = 2,                       // WTD_UI_NONE
                RevocationChecks = 0,               // WTD_REVOKE_NONE
                UnionChoice = 1,                    // WTD_CHOICE_FILE
                FileInfo = pFile,
                StateAction = 1,                    // WTD_STATEACTION_VERIFY
                ProvFlags = 0x10 | 0x1000,          // REVOCATION_CHECK_NONE | CACHE_ONLY_URL_RETRIEVAL
            };
            int result = WinVerifyTrust(-1, ref action, ref data);
            data.StateAction = 2;                   // WTD_STATEACTION_CLOSE
            WinVerifyTrust(-1, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pPath);
            Marshal.FreeCoTaskMem(pFile);
        }
    }
}
