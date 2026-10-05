// SPIKE 5 — MSIX Codex discovery + validation (prototype; NOT production code).
// Contract: nothing is executed. A path is returned only when every check passes.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace QdSpike.Discovery;

public enum DiscoveryStatus
{
    Ok, NotInstalled, PackageQueryFailed, PackagePathMissing, ExecutableMissing,
    PathEscapesPackage, NotLocalPath, SignatureInvalid, SignerMismatch, SignerUnreadable
}

public sealed record DiscoveryResult(
    DiscoveryStatus Status, string? ExecutablePath, string? PackageFullName, string? Version,
    string? SignerSubject, string Detail, double ValidateMs);

public sealed record PackageCandidate(string FullName, Version Version, string InstallRoot);

public interface IPackageLocator { IReadOnlyList<PackageCandidate> Find(); }

/// <summary>Required signer, matched on CN and O of the leaf certificate's subject (NOT thumbprint:
/// OpenAI uses short-lived Microsoft ID Verified certs that rotate every few days).</summary>
public sealed record SignerPolicy(string CommonName, string Organization);

public sealed class StaticPackageLocator(params PackageCandidate[] candidates) : IPackageLocator
{
    public IReadOnlyList<PackageCandidate> Find() => candidates;
}

/// <summary>Real lookup by package family via kernel32 (no WinRT, no capability, works unpackaged).</summary>
public sealed class FamilyPackageLocator(string packageFamilyName) : IPackageLocator
{
    public IReadOnlyList<PackageCandidate> Find()
    {
        uint count = 0, bufLen = 0;
        int rc = Native.GetPackagesByPackageFamily(packageFamilyName, ref count, IntPtr.Zero, ref bufLen, IntPtr.Zero);
        if (rc == 0 && count == 0) return [];                       // not installed
        if (rc != Native.ERROR_INSUFFICIENT_BUFFER && rc != 0) throw new InvalidOperationException($"GetPackagesByPackageFamily failed ({rc})");
        IntPtr names = Marshal.AllocHGlobal((int)count * IntPtr.Size), buf = Marshal.AllocHGlobal((int)bufLen * 2);
        try
        {
            rc = Native.GetPackagesByPackageFamily(packageFamilyName, ref count, names, ref bufLen, buf);
            if (rc != 0) throw new InvalidOperationException($"GetPackagesByPackageFamily(2) failed ({rc})");
            var list = new List<PackageCandidate>();
            for (int i = 0; i < count; i++)
            {
                string full = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size))!;
                var parts = full.Split('_');                         // Name_Version_Arch_ResourceId_PublisherId
                if (parts.Length != 5 || parts[3].Length != 0) continue; // skip resource/language packages
                if (!ArchOk(parts[2]) || !Version.TryParse(parts[1], out var v)) continue;
                uint plen = 0;
                rc = Native.GetPackagePathByFullName(full, ref plen, IntPtr.Zero);
                if (rc != Native.ERROR_INSUFFICIENT_BUFFER) continue;
                IntPtr pb = Marshal.AllocHGlobal((int)plen * 2);
                try
                {
                    if (Native.GetPackagePathByFullName(full, ref plen, pb) == 0)
                        list.Add(new PackageCandidate(full, v, Marshal.PtrToStringUni(pb)!));
                }
                finally { Marshal.FreeHGlobal(pb); }
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(names); Marshal.FreeHGlobal(buf); }
    }

    static bool ArchOk(string a) => a.Equals("neutral", StringComparison.OrdinalIgnoreCase) ||
        a.Equals(RuntimeInformation.OSArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => "x86" }, StringComparison.OrdinalIgnoreCase);
}

public static class CodexDiscovery
{
    public const string CodexFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";
    public static readonly SignerPolicy OpenAiPolicy = new("OpenAI OpCo, LLC", "OpenAI OpCo, LLC");
    public static readonly string[] RelativeExe = ["app", "resources", "codex.exe"];

    /// <summary>Re-resolves from scratch on every call. Never cache a versioned path across launches.</summary>
    public static DiscoveryResult Resolve(IPackageLocator locator, SignerPolicy policy)
    {
        var sw = Stopwatch.StartNew();
        DiscoveryResult Fail(DiscoveryStatus s, string detail, PackageCandidate? c = null, string? signer = null) =>
            new(s, null, c?.FullName, c?.Version.ToString(), signer, detail, sw.Elapsed.TotalMilliseconds);

        IReadOnlyList<PackageCandidate> found;
        try { found = locator.Find(); }
        catch (Exception e) { return Fail(DiscoveryStatus.PackageQueryFailed, e.Message); }
        if (found.Count == 0) return Fail(DiscoveryStatus.NotInstalled, "No matching package installed for this user.");

        // Highest version only: no silent fallback to an older copy (mid-update or tampering).
        var cand = found.OrderByDescending(c => c.Version).First();
        if (!Directory.Exists(cand.InstallRoot)) return Fail(DiscoveryStatus.PackagePathMissing, "Package install location does not exist.", cand);

        string? root = Canonical(cand.InstallRoot);
        if (root is null) return Fail(DiscoveryStatus.NotLocalPath, "Package root could not be canonicalised or is not a local drive path.", cand);

        string candidate = Path.Combine(root, Path.Combine(RelativeExe));
        if (!File.Exists(candidate)) return Fail(DiscoveryStatus.ExecutableMissing, "app\\resources\\codex.exe not found in the package.", cand);

        string? exe = Canonical(candidate);
        if (exe is null) return Fail(DiscoveryStatus.NotLocalPath, "Executable could not be canonicalised or is not a local drive path.", cand);
        if (!exe.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return Fail(DiscoveryStatus.PathEscapesPackage, "Canonical executable path is outside the package root (reparse point/junction).", cand);
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Fail(DiscoveryStatus.ExecutableMissing, "Target is not an .exe.", cand);

        int trust = Native.VerifyAuthenticode(exe);
        if (trust != 0) return Fail(DiscoveryStatus.SignatureInvalid, $"WinVerifyTrust failed: 0x{trust:X8} ({TrustName(trust)}).", cand);

        string subject;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(exe));
#pragma warning restore SYSLIB0057
            subject = cert.Subject;
            string? cn = null, o = null;
            foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
            {
                var t = rdn.GetSingleElementType().Value; var v = rdn.GetSingleElementValue();
                if (t == "2.5.4.3") cn = v; else if (t == "2.5.4.10") o = v;
            }
            if (!string.Equals(cn, policy.CommonName, StringComparison.Ordinal) || !string.Equals(o, policy.Organization, StringComparison.Ordinal))
                return Fail(DiscoveryStatus.SignerMismatch, $"Valid signature, but signer is not the expected publisher (CN='{cn}', O='{o}').", cand, subject);
        }
        catch (Exception e) { return Fail(DiscoveryStatus.SignerUnreadable, e.GetType().Name, cand); }

        return new DiscoveryResult(DiscoveryStatus.Ok, exe, cand.FullName, cand.Version.ToString(), subject, "validated", sw.Elapsed.TotalMilliseconds);
    }

    static string TrustName(int hr) => unchecked((uint)hr) switch
    {
        0x800B0100 => "TRUST_E_NOSIGNATURE", 0x80096010 => "TRUST_E_BAD_DIGEST (file modified)",
        0x800B0109 => "CERT_E_UNTRUSTEDROOT", 0x800B0101 => "CERT_E_EXPIRED", 0x800B010A => "CERT_E_CHAINING",
        0x80096004 => "TRUST_E_CERT_SIGNATURE", _ => "other"
    };

    /// <summary>Final on-disk path (resolves junctions/symlinks/8.3). Null if not a local drive-letter path.</summary>
    static string? Canonical(string path)
    {
        using var h = Native.CreateFileW(path, 0x80 /*FILE_READ_ATTRIBUTES*/, 7, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0x02000000 /*BACKUP_SEMANTICS*/, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var sb = new char[1024];
        uint n = Native.GetFinalPathNameByHandleW(h, sb, (uint)sb.Length, 0);
        if (n == 0 || n >= sb.Length) return null;
        string s = new(sb, 0, (int)n);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return null;
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        return s.Length > 2 && s[1] == ':' ? s : null;
    }
}

static class Native
{
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr fullNames, ref uint bufLen, IntPtr buf);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackagePathByFullName(string fullName, ref uint len, IntPtr path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, [Out] char[] buf, uint len, uint flags);

    [StructLayout(LayoutKind.Sequential)] struct WINTRUST_FILE_INFO { public uint cbStruct; public IntPtr pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice; public uint fdwRevocationChecks;
        public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
        public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
    }
    [DllImport("wintrust.dll")] static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    /// <summary>Embedded-Authenticode verify. UI none; revocation checks OFF and URL retrieval cache-only so this
    /// never triggers network traffic from our process. Returns 0 on success, else the HRESULT.</summary>
    public static int VerifyAuthenticode(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        IntPtr pPath = Marshal.StringToCoTaskMemUni(path);
        IntPtr pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = pPath }, pFile, false);
            var d = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2 /*NONE*/, fdwRevocationChecks = 0 /*NONE*/, dwUnionChoice = 1 /*FILE*/,
                pFile = pFile, dwStateAction = 1 /*VERIFY*/, dwProvFlags = 0x10 /*REVOCATION_CHECK_NONE*/ | 0x1000 /*CACHE_ONLY_URL_RETRIEVAL*/
            };
            int r = WinVerifyTrust(new IntPtr(-1), ref action, ref d);
            d.dwStateAction = 2; /*CLOSE*/
            WinVerifyTrust(new IntPtr(-1), ref action, ref d);
            return r;
        }
        finally { Marshal.FreeCoTaskMem(pPath); Marshal.FreeCoTaskMem(pFile); }
    }
}
