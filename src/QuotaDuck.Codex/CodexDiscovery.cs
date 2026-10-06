using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace QuotaDuck.Codex;

internal enum CodexDiscoveryStatus
{
    Ok = 0,
    NotInstalled,
    PackageQueryFailed,
    PackagePathMissing,
    ExecutableMissing,
    PathEscapesPackage,
    NotLocalPath,
    SignatureInvalid,
    SignerMismatch,
    SignerUnreadable,
}

/// <summary>A validated Codex executable. Only <see cref="CodexExecutableResolver"/> creates one, and only after every check passed.</summary>
internal sealed record CodexExecutable(string Path, string PackageFullName, string Version);

internal sealed record CodexDiscoveryResult(CodexDiscoveryStatus Status, CodexExecutable? Executable);

internal interface ICodexExecutableResolver
{
    /// <summary>Re-resolves from the OS package registration every call (a new version/path is picked up; nothing versioned is persisted).</summary>
    CodexDiscoveryResult Resolve();
}

internal sealed record CodexPackageCandidate(string FullName, Version Version, string InstallRoot);

internal interface ICodexPackageLocator
{
    IReadOnlyList<CodexPackageCandidate> Find();
}

/// <summary>Required signer, matched on subject CN and O of the leaf certificate — never on a thumbprint (the publisher rotates short-lived certificates).</summary>
internal sealed record CodexSignerPolicy(string CommonName, string Organization);

internal interface IAuthenticodeVerifier
{
    /// <summary>0 when the embedded signature is valid, otherwise the failing HRESULT.</summary>
    int Verify(string path);

    bool TryReadSigner(string path, out string? commonName, out string? organization);
}

internal sealed class StaticPackageLocator(params CodexPackageCandidate[] candidates) : ICodexPackageLocator
{
    public IReadOnlyList<CodexPackageCandidate> Find() => candidates;
}

/// <summary>The OS package registration lookup by family name (kernel32; no WinRT, no capability, works unpackaged).</summary>
internal sealed unsafe class FamilyPackageLocator(string packageFamilyName) : ICodexPackageLocator
{
    public IReadOnlyList<CodexPackageCandidate> Find()
    {
        uint count = 0, bufLen = 0;
        int rc = Native.GetPackagesByPackageFamily(packageFamilyName, ref count, null, ref bufLen, null);
        if (rc == 0 && count == 0)
        {
            return [];
        }

        if (rc != Native.ErrorInsufficientBuffer && rc != 0)
        {
            throw new InvalidOperationException("package family query failed");
        }

        var names = new nint[count];
        var buffer = new char[bufLen];
        fixed (nint* pn = names)
        fixed (char* pb = buffer)
        {
            rc = Native.GetPackagesByPackageFamily(packageFamilyName, ref count, pn, ref bufLen, pb);
            if (rc != 0)
            {
                throw new InvalidOperationException("package family query failed");
            }

            var list = new List<CodexPackageCandidate>();
            for (int i = 0; i < count; i++)
            {
                string full = Marshal.PtrToStringUni(pn[i])!;
                string[] parts = full.Split('_');                       // Name_Version_Arch_ResourceId_PublisherId
                if (parts.Length != 5 || parts[3].Length != 0)
                {
                    continue;                                           // resource / language packages
                }

                if (!ArchitectureMatches(parts[2]) || !Version.TryParse(parts[1], out Version? v))
                {
                    continue;
                }

                uint len = 0;
                if (Native.GetPackagePathByFullName(full, ref len, null) != Native.ErrorInsufficientBuffer)
                {
                    continue;
                }

                var path = new char[len];
                fixed (char* pp = path)
                {
                    if (Native.GetPackagePathByFullName(full, ref len, pp) == 0)
                    {
                        list.Add(new CodexPackageCandidate(full, v, new string(pp)));
                    }
                }
            }

            return list;
        }
    }

    private static bool ArchitectureMatches(string arch) =>
        arch.Equals("neutral", StringComparison.OrdinalIgnoreCase)
        || arch.Equals(
            RuntimeInformation.OSArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => "x86" },
            StringComparison.OrdinalIgnoreCase);
}

internal sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    public int Verify(string path) => Native.VerifyEmbeddedSignature(path);

    public bool TryReadSigner(string path, out string? commonName, out string? organization)
    {
        commonName = organization = null;
        try
        {
#pragma warning disable SYSLIB0057 // signature validity is established by WinVerifyTrust first; this only reads the leaf subject
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            foreach (X500RelativeDistinguishedName rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
            {
                string? type = rdn.GetSingleElementType().Value;
                string value = rdn.GetSingleElementValue() ?? string.Empty;
                if (type == "2.5.4.3")
                {
                    commonName = value;
                }
                else if (type == "2.5.4.10")
                {
                    organization = value;
                }
            }

            return true;
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Finds the installed OpenAI Codex executable and proves it is the genuine one BEFORE it may ever be launched:
/// package family lookup -> highest installed version only -> canonical final path inside the package root -> signature -> signer.
/// Never uses PATH, a bare name, a shim, a user-configurable path or a thumbprint. The (310 MB) signature check is cached only by the
/// identity tuple (canonical path, package full name, size, last-write time) so a changed file is always re-verified.
/// </summary>
internal sealed class CodexExecutableResolver(
    ICodexPackageLocator locator,
    IAuthenticodeVerifier verifier,
    CodexSignerPolicy policy) : ICodexExecutableResolver
{
    public const string PackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";
    public static readonly CodexSignerPolicy OpenAiPolicy = new("OpenAI OpCo, LLC", "OpenAI OpCo, LLC");

    private static readonly string[] RelativeExecutable = ["app", "resources", "codex.exe"];

    private readonly object _gate = new();
    private (string Path, string Package, long Length, long LastWriteTicks)? _validated;

    public static CodexExecutableResolver CreateDefault() =>
        new(new FamilyPackageLocator(PackageFamilyName), new WindowsAuthenticodeVerifier(), OpenAiPolicy);

    public CodexDiscoveryResult Resolve()
    {
        IReadOnlyList<CodexPackageCandidate> found;
        try
        {
            found = locator.Find();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            return Fail(CodexDiscoveryStatus.PackageQueryFailed);
        }

        if (found.Count == 0)
        {
            return Fail(CodexDiscoveryStatus.NotInstalled);
        }

        // Highest version only: no silent fallback to an older copy (mid-update or tampering).
        CodexPackageCandidate cand = found.OrderByDescending(c => c.Version).First();
        if (!Directory.Exists(cand.InstallRoot))
        {
            return Fail(CodexDiscoveryStatus.PackagePathMissing);
        }

        string? root = Native.TryGetFinalPath(cand.InstallRoot);
        if (root is null)
        {
            return Fail(CodexDiscoveryStatus.NotLocalPath);
        }

        string candidate = Path.Combine(root, Path.Combine(RelativeExecutable));
        if (!File.Exists(candidate))
        {
            return Fail(CodexDiscoveryStatus.ExecutableMissing);
        }

        string? exe = Native.TryGetFinalPath(candidate);
        if (exe is null)
        {
            return Fail(CodexDiscoveryStatus.NotLocalPath);
        }

        if (!exe.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(CodexDiscoveryStatus.PathEscapesPackage);       // a junction/symlink led out of the package
        }

        if (!string.Equals(Path.GetFileName(exe), "codex.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(CodexDiscoveryStatus.ExecutableMissing);
        }

        FileInfo info;
        try
        {
            info = new FileInfo(exe);
            if (!info.Exists)
            {
                return Fail(CodexDiscoveryStatus.ExecutableMissing);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(CodexDiscoveryStatus.ExecutableMissing);
        }

        var identity = (exe, cand.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
        lock (_gate)
        {
            if (_validated is { } v && v == identity)
            {
                return new CodexDiscoveryResult(CodexDiscoveryStatus.Ok, new CodexExecutable(exe, cand.FullName, cand.Version.ToString()));
            }
        }

        int trust = verifier.Verify(exe);
        if (trust != 0)
        {
            return Fail(CodexDiscoveryStatus.SignatureInvalid);
        }

        if (!verifier.TryReadSigner(exe, out string? cn, out string? org))
        {
            return Fail(CodexDiscoveryStatus.SignerUnreadable);
        }

        if (!string.Equals(cn, policy.CommonName, StringComparison.Ordinal) || !string.Equals(org, policy.Organization, StringComparison.Ordinal))
        {
            return Fail(CodexDiscoveryStatus.SignerMismatch);
        }

        lock (_gate)
        {
            _validated = identity;                                      // cached ONLY after every check passed
        }

        return new CodexDiscoveryResult(CodexDiscoveryStatus.Ok, new CodexExecutable(exe, cand.FullName, cand.Version.ToString()));
    }

    private static CodexDiscoveryResult Fail(CodexDiscoveryStatus status) => new(status, null);
}
