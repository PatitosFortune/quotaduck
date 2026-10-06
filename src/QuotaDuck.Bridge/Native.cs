using System.Runtime.InteropServices;
using System.Security.Principal;

namespace QuotaDuck.Bridge;

/// <summary>
/// The bridge's only native interop:
/// <list type="bullet">
/// <item>resolve the user's real LocalAppData folder from the process-user TOKEN, not from the environment;</item>
/// <item>create a directory whose DACL is protected (no inheritance) and grants full control to exactly one SID — the
/// current user. Everything created inside it inherits that DACL.</item>
/// </list>
/// </summary>
internal static unsafe partial class Native
{
    private const int ErrorAlreadyExists = 183;
    private const uint SddlRevision1 = 1;

    // KF_FLAG_DEFAULT_PATH: return the folder's built-in default (derived from the token's profile directory) instead of the
    // registry value, which expands %USERPROFILE% from the PROCESS ENVIRONMENT and can therefore be redirected by a hostile one.
    private const uint KfFlagDefaultPath = 0x00000400;

    private static readonly Guid FolderIdLocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectoryNative(string path, ref SecurityAttributes attributes);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSddl(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetKnownFolderPath")]
    private static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [LibraryImport("userenv.dll", EntryPoint = "GetUserProfileDirectoryW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserProfileDirectory(IntPtr token, char* buffer, ref uint size);

    /// <summary>
    /// The current user's real LocalAppData folder, derived from the process-user token via two environment-independent
    /// mechanisms that must AGREE: the known-folder DEFAULT path and the token's profile directory. Returns false (so the
    /// caller writes nothing) if either fails or the folder is not inside the profile. Not influenced by LOCALAPPDATA,
    /// USERPROFILE, APPDATA, HOME*, or any other environment variable (proven by tests, including a fresh hostile process).
    /// </summary>
    public static bool TryGetLocalAppData(out string path)
    {
        path = string.Empty;
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            IntPtr token = identity.Token;

            string? profile = GetProfileDirectory(token);
            if (profile is null || !Path.IsPathFullyQualified(profile))
            {
                return false;
            }

            int hr = SHGetKnownFolderPath(in FolderIdLocalAppData, KfFlagDefaultPath, token, out IntPtr raw);
            if (hr != 0 || raw == IntPtr.Zero)
            {
                return false;
            }

            string? known;
            try
            {
                known = Marshal.PtrToStringUni(raw);
            }
            finally
            {
                Marshal.FreeCoTaskMem(raw);
            }

            if (string.IsNullOrWhiteSpace(known) || !Path.IsPathFullyQualified(known))
            {
                return false;
            }

            if (!IsStrictlyInside(known, profile))
            {
                return false;                  // the two independent derivations disagree: refuse rather than guess
            }

            path = Path.GetFullPath(known).TrimEnd('\\');
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True if <paramref name="folder"/> is a proper descendant of <paramref name="profile"/> after full normalisation
    /// (case-insensitive; "C:\Users\bob2" is NOT inside "C:\Users\bob"; ".." segments cannot escape).</summary>
    internal static bool IsStrictlyInside(string folder, string profile)
    {
        if (!Path.IsPathFullyQualified(folder) || !Path.IsPathFullyQualified(profile))
        {
            return false;
        }

        string root = Path.GetFullPath(profile).TrimEnd('\\') + "\\";
        string full = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetProfileDirectory(IntPtr token)
    {
        char* buffer = stackalloc char[520];
        uint size = 520;
        return GetUserProfileDirectory(token, buffer, ref size) ? new string(buffer) : null;
    }

    internal static bool IsPlainSid(string? s)
    {
        if (s is null || s.Length is < 8 or > 184 || !s.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = 4; i < s.Length; i++)
        {
            char c = s[i];
            if (!(char.IsAsciiDigit(c) || (c == '-' && i > 4 && s[i - 1] != '-' && i < s.Length - 1)))
            {
                return false;
            }
        }

        return char.IsAsciiDigit(s[^1]);
    }

    /// <summary>Creates <paramref name="path"/> (parent must exist) owned by and accessible only to <paramref name="userSid"/>.
    /// Returns true if it was created or already existed; false on any failure (caller must then NOT write state).</summary>
    public static bool TryCreateUserOnlyDirectory(string path, string userSid)
    {
        // The SID is spliced into an SDDL string, so it must be a plain SID literal ("S-1-" followed by digits and hyphens only).
        // Anything else (e.g. text that would add a second ACE) is refused: fail closed, directory not created.
        if (!IsPlainSid(userSid))
        {
            return false;
        }

        // D:P = protected DACL (no inherited ACEs); one allow ACE, object+container inherit, FILE_ALL_ACCESS, this SID only.
        string sddl = "D:P(A;OICI;FA;;;" + userSid + ")";
        if (!ConvertSddl(sddl, SddlRevision1, out IntPtr descriptor, IntPtr.Zero))
        {
            return false;
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0,
            };
            return CreateDirectoryNative(path, ref attributes) || Marshal.GetLastPInvokeError() == ErrorAlreadyExists;
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }
}
