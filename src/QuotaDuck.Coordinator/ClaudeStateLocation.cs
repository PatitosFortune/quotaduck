using System.Runtime.InteropServices;
using System.Security.Principal;

namespace QuotaDuck.Coordinator;

/// <summary>
/// Where the bridge keeps its state: &lt;the user's real LocalAppData&gt;\QuotaDuck\bridge\claude-v1.json — the SAME fixed, non-redirectable
/// location the bridge writes (same derivation: process-user token, known-folder DEFAULT path, cross-checked against the token's profile
/// directory; no environment variable, no argument, no setting). This is the reader's copy of that derivation; a test asserts both agree.
/// Returns null (nothing is read or watched) if the location cannot be established.
/// </summary>
internal static unsafe partial class ClaudeStateLocation
{
    public const string FileName = "claude-v1.json";

    private const uint KfFlagDefaultPath = 0x00000400;
    private static readonly Guid FolderIdLocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

    [LibraryImport("shell32.dll", EntryPoint = "SHGetKnownFolderPath")]
    private static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);

    [LibraryImport("userenv.dll", EntryPoint = "GetUserProfileDirectoryW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserProfileDirectory(nint token, char* buffer, ref uint size);

    /// <summary>The current user's profile directory from the process-user TOKEN (never from USERPROFILE or any other variable); null if unavailable.</summary>
    public static string? UserProfileDirectory()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            char* buffer = stackalloc char[520];
            uint size = 520;
            string? profile = GetUserProfileDirectory(identity.Token, buffer, ref size) ? new string(buffer) : null;
            return profile is not null && Path.IsPathFullyQualified(profile) ? Path.GetFullPath(profile).TrimEnd('\\') : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? DefaultDirectory() =>
        TryGetLocalAppData(out string root) ? Path.Combine(root, "QuotaDuck", "bridge") : null;

    internal static bool TryGetLocalAppData(out string path)
    {
        path = string.Empty;
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            nint token = identity.Token;

            char* buffer = stackalloc char[520];
            uint size = 520;
            string? profile = GetUserProfileDirectory(token, buffer, ref size) ? new string(buffer) : null;
            if (profile is null || !Path.IsPathFullyQualified(profile))
            {
                return false;
            }

            if (SHGetKnownFolderPath(in FolderIdLocalAppData, KfFlagDefaultPath, token, out nint raw) != 0 || raw == 0)
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

            if (string.IsNullOrWhiteSpace(known) || !Path.IsPathFullyQualified(known) || !IsStrictlyInside(known, profile))
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
}
