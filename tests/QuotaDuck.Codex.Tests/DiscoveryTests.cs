using System.Diagnostics;

namespace QuotaDuck.Codex.Tests;

/// <summary>
/// Discovery/validation against a synthetic package layout. Real Windows APIs (canonical path, WinVerifyTrust, signer read) are used with a
/// copy of the Microsoft-signed dotnet host standing in for codex.exe, so the genuine verification code runs without the real Codex.
/// </summary>
public class DiscoveryTests
{
    private static readonly CodexSignerPolicy Microsoft = new(".NET", "Microsoft Corporation");
    private static readonly CodexSignerPolicy Wrong = new("OpenAI OpCo, LLC", "OpenAI OpCo, LLC");

    private sealed class FakeVerifier(int trust = 0, string? cn = "Fake CN", string? org = "Fake O", bool readable = true) : IAuthenticodeVerifier
    {
        public int Trust { get; set; } = trust;

        public string? Cn { get; set; } = cn;

        public string? Org { get; set; } = org;

        public bool Readable { get; set; } = readable;

        public int VerifyCalls { get; private set; }

        public int Verify(string path)
        {
            VerifyCalls++;
            return Trust;
        }

        public bool TryReadSigner(string path, out string? commonName, out string? organization)
        {
            commonName = Cn;
            organization = Org;
            return Readable;
        }
    }

    /// <summary>A package root with app\resources\codex.exe containing <paramref name="content"/>.</summary>
    private static string MakePackage(TempDir dir, string name, byte[]? content = null, bool withExe = true)
    {
        string root = dir.File(name);
        Directory.CreateDirectory(Path.Combine(root, "app", "resources"));
        if (withExe) File.WriteAllBytes(Path.Combine(root, "app", "resources", "codex.exe"), content ?? [1, 2, 3, 4]);
        return root;
    }

    private static CodexPackageCandidate Cand(string version, string root) =>
        new($"Fake.Pkg_{version}_x64__0000000000000", Version.Parse(version), root);

    private static CodexExecutableResolver R(IAuthenticodeVerifier v, CodexSignerPolicy p, params CodexPackageCandidate[] c) => new(new StaticPackageLocator(c), v, p);

    private static readonly CodexSignerPolicy Fake = new("Fake CN", "Fake O");

    // ---------------- happy path and each refusal ----------------

    [Fact]
    public void Valid_ReturnsTheCanonicalPath_VersionAndFullName()
    {
        using var d = new TempDir();
        string root = MakePackage(d, "p1");
        CodexDiscoveryResult r = R(new FakeVerifier(), Fake, Cand("1.2.3.4", root)).Resolve();
        Assert.Equal(CodexDiscoveryStatus.Ok, r.Status);
        Assert.Equal("1.2.3.4", r.Executable!.Version);
        Assert.EndsWith(Path.Combine("app", "resources", "codex.exe"), r.Executable.Path);
        Assert.True(Path.IsPathFullyQualified(r.Executable.Path));
        Assert.DoesNotContain("..", r.Executable.Path);
        Assert.Contains("Fake.Pkg_1.2.3.4", r.Executable.PackageFullName);
    }

    [Fact]
    public void NotInstalled_QueryFailure_MissingRoot_MissingExe()
    {
        using var d = new TempDir();
        Assert.Equal(CodexDiscoveryStatus.NotInstalled, R(new FakeVerifier(), Fake).Resolve().Status);
        Assert.Equal(CodexDiscoveryStatus.PackagePathMissing, R(new FakeVerifier(), Fake, Cand("1.0.0.0", d.File("gone"))).Resolve().Status);
        Assert.Equal(CodexDiscoveryStatus.ExecutableMissing, R(new FakeVerifier(), Fake, Cand("1.0.0.0", MakePackage(d, "noexe", withExe: false))).Resolve().Status);
        var throwing = new CodexExecutableResolver(new ThrowingLocator(), new FakeVerifier(), Fake);
        Assert.Equal(CodexDiscoveryStatus.PackageQueryFailed, throwing.Resolve().Status);
    }

    private sealed class ThrowingLocator : ICodexPackageLocator
    {
        public IReadOnlyList<CodexPackageCandidate> Find() => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void OnlyTheHighestVersionIsConsidered_NoFallbackToAnOlderCopy()
    {
        using var d = new TempDir();
        string older = MakePackage(d, "old");
        string newer = MakePackage(d, "new", withExe: false);
        CodexDiscoveryResult r = R(new FakeVerifier(), Fake, Cand("1.0.0.0", older), Cand("2.0.0.0", newer)).Resolve();
        Assert.Equal(CodexDiscoveryStatus.ExecutableMissing, r.Status);
        Assert.Null(r.Executable);

        string newerOk = MakePackage(d, "new2");
        CodexDiscoveryResult ok = R(new FakeVerifier(), Fake, Cand("1.0.0.0", older), Cand("10.0.0.0", newerOk), Cand("9.0.0.0", older)).Resolve();
        Assert.Equal("10.0.0.0", ok.Executable!.Version);                                     // numeric, not lexical, version order
    }

    [Fact]
    public void ADirectoryJunctionLeadingOutOfThePackage_IsRefused()
    {
        using var d = new TempDir();
        string outside = Path.Combine(d.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "codex.exe"), [9, 9, 9]);
        string root = d.File("pkg");
        Directory.CreateDirectory(Path.Combine(root, "app"));
        MakeJunction(Path.Combine(root, "app", "resources"), outside);

        var v = new FakeVerifier();
        CodexDiscoveryResult r = R(v, Fake, Cand("1.0.0.0", root)).Resolve();
        Assert.Equal(CodexDiscoveryStatus.PathEscapesPackage, r.Status);
        Assert.Null(r.Executable);
        Assert.Equal(0, v.VerifyCalls);                                                        // refused before any signature work
    }

    [Fact]
    public void AJunctionedPackageRoot_ResolvesToItsRealLocation_ForTheContainmentCheck()
    {
        using var d = new TempDir();
        string real = MakePackage(d, "real");
        string link = d.File("link");
        MakeJunction(link, real);
        CodexDiscoveryResult r = R(new FakeVerifier(), Fake, Cand("1.0.0.0", link)).Resolve();
        Assert.Equal(CodexDiscoveryStatus.Ok, r.Status);
        Assert.StartsWith(Native.TryGetFinalPath(real)!, r.Executable!.Path, StringComparison.OrdinalIgnoreCase);   // the REAL path is what would be launched
    }

    [Fact]
    public void InvalidSignature_SignerMismatch_UnreadableSigner_AreRefused_WithNoExecutable()
    {
        using var d = new TempDir();
        string root = MakePackage(d, "p");
        Assert.Equal(CodexDiscoveryStatus.SignatureInvalid, R(new FakeVerifier(trust: unchecked((int)0x800B0100)), Fake, Cand("1.0.0.0", root)).Resolve().Status);
        Assert.Equal(CodexDiscoveryStatus.SignerUnreadable, R(new FakeVerifier(readable: false), Fake, Cand("1.0.0.0", root)).Resolve().Status);
        foreach ((string? cn, string? org) in new (string?, string?)[] { ("Other", "Fake O"), ("Fake CN", "Other"), ("fake cn", "Fake O"), ("Fake CN", "fake o"), (null, "Fake O"), ("Fake CN", null), ("Fake CN ", "Fake O"), ("Fake CN, extra", "Fake O") })
        {
            CodexDiscoveryResult r = R(new FakeVerifier(cn: cn, org: org), Fake, Cand("1.0.0.0", root)).Resolve();
            Assert.Equal(CodexDiscoveryStatus.SignerMismatch, r.Status);
            Assert.Null(r.Executable);
        }
    }

    [Fact]
    public void DefaultPolicy_IsTheOpenAiSubject_AndFamilyName_AsDocumented()
    {
        Assert.Equal("OpenAI OpCo, LLC", CodexExecutableResolver.OpenAiPolicy.CommonName);
        Assert.Equal("OpenAI OpCo, LLC", CodexExecutableResolver.OpenAiPolicy.Organization);
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", CodexExecutableResolver.PackageFamilyName);
        Assert.Equal(["CommonName", "Organization"], typeof(CodexSignerPolicy).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());   // no thumbprint
    }

    // ---------------- identity-tuple cache ----------------

    [Fact]
    public void Cache_SkipsReVerification_OnlyWhileTheIdentityIsUnchanged()
    {
        using var d = new TempDir();
        string root = MakePackage(d, "p");
        string exe = Path.Combine(root, "app", "resources", "codex.exe");
        var v = new FakeVerifier();
        CodexExecutableResolver res = R(v, Fake, Cand("1.0.0.0", root));

        Assert.Equal(CodexDiscoveryStatus.Ok, res.Resolve().Status);
        Assert.Equal(CodexDiscoveryStatus.Ok, res.Resolve().Status);
        Assert.Equal(1, v.VerifyCalls);                                                        // second resolve used the cache

        File.WriteAllBytes(exe, [1, 2, 3, 4, 5]);                                              // size changed
        Assert.Equal(CodexDiscoveryStatus.Ok, res.Resolve().Status);
        Assert.Equal(2, v.VerifyCalls);

        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddDays(-3));                            // same size, new timestamp
        Assert.Equal(CodexDiscoveryStatus.Ok, res.Resolve().Status);
        Assert.Equal(3, v.VerifyCalls);

        Assert.Equal(CodexDiscoveryStatus.Ok, res.Resolve().Status);
        Assert.Equal(3, v.VerifyCalls);
    }

    [Fact]
    public void Cache_IsKeyedOnPathAndPackage_ANewVersionOrLocationIsReVerified()
    {
        using var d = new TempDir();
        string rootA = MakePackage(d, "a");
        string rootB = MakePackage(d, "b");
        var v = new FakeVerifier();
        var locator = new MutableLocator { Current = [Cand("1.0.0.0", rootA)] };
        var res = new CodexExecutableResolver(locator, v, Fake);
        res.Resolve();
        res.Resolve();
        Assert.Equal(1, v.VerifyCalls);

        locator.Current = [Cand("2.0.0.0", rootB)];                                            // update installed: new path, new package full name
        Assert.Equal("2.0.0.0", res.Resolve().Executable!.Version);
        Assert.Equal(2, v.VerifyCalls);

        locator.Current = [Cand("2.0.1.0", rootB)];                                            // same file, new package full name
        res.Resolve();
        Assert.Equal(3, v.VerifyCalls);
    }

    private sealed class MutableLocator : ICodexPackageLocator
    {
        public IReadOnlyList<CodexPackageCandidate> Current { get; set; } = [];

        public IReadOnlyList<CodexPackageCandidate> Find() => Current;
    }

    [Fact]
    public void Cache_NeverStoresAFailure_AndAFileThatStopsBeingValidIsRefused()
    {
        using var d = new TempDir();
        string root = MakePackage(d, "p");
        string exe = Path.Combine(root, "app", "resources", "codex.exe");
        var v = new FakeVerifier(trust: unchecked((int)0x800B0100));
        CodexExecutableResolver res = R(v, Fake, Cand("1.0.0.0", root));
        Assert.Equal(CodexDiscoveryStatus.SignatureInvalid, res.Resolve().Status);
        Assert.Equal(CodexDiscoveryStatus.SignatureInvalid, res.Resolve().Status);
        Assert.Equal(2, v.VerifyCalls);                                                        // a failure is re-checked every time

        v.Trust = 0;
        Assert.Equal(CodexDiscoveryStatus.Ok, res.Resolve().Status);                           // cached now
        v.Trust = unchecked((int)0x80096010);
        File.AppendAllText(exe, "tamper");                                                     // modified after validation => identity changes
        Assert.Equal(CodexDiscoveryStatus.SignatureInvalid, res.Resolve().Status);
        Assert.Equal(CodexDiscoveryStatus.SignatureInvalid, res.Resolve().Status);             // and the old success is gone, not resurrected
    }

    // ---------------- the real verification code ----------------

    [Fact]
    public void Real_UnknownPackageFamily_IsNotInstalled()
    {
        var res = new CodexExecutableResolver(new FamilyPackageLocator("QuotaDuck.DoesNotExist_0000000000000"), new WindowsAuthenticodeVerifier(), Wrong);
        Assert.Equal(CodexDiscoveryStatus.NotInstalled, res.Resolve().Status);
    }

    [Fact]
    public void Real_AValidMicrosoftSignedFile_IsAcceptedOnlyWhenTheSignerMatches()
    {
        using var d = new TempDir();
        string root = d.File("pkg");
        Directory.CreateDirectory(Path.Combine(root, "app", "resources"));
        File.Copy(FakeRig.HostPath(), Path.Combine(root, "app", "resources", "codex.exe"));

        var verifier = new WindowsAuthenticodeVerifier();
        string exe = Path.Combine(root, "app", "resources", "codex.exe");
        Assert.Equal(0, verifier.Verify(exe));
        Assert.True(verifier.TryReadSigner(exe, out string? cn, out string? org));
        Assert.Equal(".NET", cn);
        Assert.Equal("Microsoft Corporation", org);

        Assert.Equal(CodexDiscoveryStatus.Ok, new CodexExecutableResolver(new StaticPackageLocator(Cand("1.0.0.0", root)), verifier, Microsoft).Resolve().Status);
        CodexDiscoveryResult wrong = new CodexExecutableResolver(new StaticPackageLocator(Cand("1.0.0.0", root)), verifier, Wrong).Resolve();
        Assert.Equal(CodexDiscoveryStatus.SignerMismatch, wrong.Status);                       // genuine signature, wrong publisher
        Assert.Null(wrong.Executable);
    }

    [Fact]
    public void Real_UnsignedAndTamperedFiles_AreSignatureInvalid()
    {
        using var d = new TempDir();
        var verifier = new WindowsAuthenticodeVerifier();

        string unsigned = MakePackage(d, "unsigned", [0x4D, 0x5A, 0x90, 0x00, 1, 2, 3]);
        Assert.Equal(CodexDiscoveryStatus.SignatureInvalid, new CodexExecutableResolver(new StaticPackageLocator(Cand("1.0.0.0", unsigned)), verifier, Microsoft).Resolve().Status);

        string tampered = d.File("tampered");
        Directory.CreateDirectory(Path.Combine(tampered, "app", "resources"));
        byte[] bytes = File.ReadAllBytes(FakeRig.HostPath());
        bytes[bytes.Length / 3] ^= 0xFF;                                                       // inside the signed image
        File.WriteAllBytes(Path.Combine(tampered, "app", "resources", "codex.exe"), bytes);
        CodexDiscoveryResult r = new CodexExecutableResolver(new StaticPackageLocator(Cand("1.0.0.0", tampered)), verifier, Microsoft).Resolve();
        Assert.Equal(CodexDiscoveryStatus.SignatureInvalid, r.Status);
        Assert.Null(r.Executable);
    }

    [Fact]
    public void Real_FinalPath_RefusesUncAndDevicePaths_AndResolvesShortNames()
    {
        Assert.Null(Native.TryGetFinalPath(@"\\localhost\C$\Windows"));
        Assert.Null(Native.TryGetFinalPath(@"C:\this\does\not\exist"));
        string? sys = Native.TryGetFinalPath(Environment.SystemDirectory);
        Assert.NotNull(sys);
        Assert.Equal(Environment.SystemDirectory, sys, StringComparer.OrdinalIgnoreCase);
    }

    private static void MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link}' -Target '{target}' | Out-Null");
        using Process p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
