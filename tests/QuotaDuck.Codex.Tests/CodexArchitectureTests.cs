using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace QuotaDuck.Codex.Tests;

/// <summary>Structural enforcement of the Codex runner's security constraints on the COMPILED assembly and the sources.</summary>
public class CodexArchitectureTests
{
    private static readonly Assembly Codex = typeof(CodexQuotaReader).Assembly;

    private static readonly string[] ForbiddenReferencePrefixes =
    [
        "System.Net",                     // no HTTP, sockets, WebSockets: the ONLY networking is the Codex child process itself
        "Microsoft.Win32.Registry",
        "System.Data",
        "System.Management",
        "System.Reflection.Emit",
        "System.ServiceProcess",
        "System.Diagnostics.EventLog",
        "System.Diagnostics.TraceSource",
        "System.IO.Pipes",
        "System.IO.MemoryMappedFiles",
        "System.Security.Cryptography.Algorithms",
    ];

    // lower-case fragments that must not occur in ANY string literal of the compiled assembly
    private static readonly string[] ForbiddenLiterals =
    [
        "http://", "https://", "ftp://", "ws://", "wss://", "hkey_", "software\\microsoft", "currentversion\\run",
        ".claude", ".codex", "auth.json", "credential", "cookie", "login data", "authorization", "bearer", "api_key", "apikey", "token",
        "anthropic", "powershell", "cmd.exe", "sqlite", "codex_home", "path\\", "%",
        // app-server surfaces QuotaDuck must never reach
        "--listen", "turn/", "thread/", "command/", "config/", "fs/", "--strict-config", "--yolo",
    ];

    // whole-word matches (so "CodexExecutable" is not mistaken for the "exec" command)
    private static readonly System.Text.RegularExpressions.Regex ForbiddenWords =
        new(@"(exec|proxy|daemon|login|logout|mcp)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly string[] AllowedExactLiterals =
    [
        "OpenAI.Codex_2p2nqsd0c76g0",            // the package family (identity, not a path)
        "OpenAI OpCo, LLC",                       // the pinned signer subject (CN and O)
    ];

    [Fact]
    public void CodexAssembly_ReferencesOnlyTheBaseClassLibraryAndCore_NothingNetworkRelated()
    {
        string[] refs = Codex.GetReferencedAssemblies().Select(r => r.Name!).ToArray();
        Assert.NotEmpty(refs);
        Assert.All(refs, n => Assert.True(
            n.StartsWith("System", StringComparison.Ordinal) || n is "netstandard" or "mscorlib" or "Microsoft.Win32.Primitives" or "QuotaDuck.Core",
            $"unexpected assembly reference {n}"));
        Assert.DoesNotContain(refs, r => ForbiddenReferencePrefixes.Any(p => r.StartsWith(p, StringComparison.Ordinal)));
        Assert.Contains("System.Diagnostics.Process", refs);                    // the one place a process is launched
    }

    [Fact]
    public void CompiledStringLiterals_ContainNoUrlsCredentialsConfigPathsOrForbiddenAppServerSurfaces()
    {
        string[] literals = UserStrings(Codex.Location).ToArray();
        Assert.NotEmpty(literals);
        foreach (string lit in literals)
        {
            if (AllowedExactLiterals.Contains(lit, StringComparer.Ordinal)) continue;
            string lower = lit.ToLowerInvariant();
            foreach (string bad in ForbiddenLiterals)
            {
                Assert.False(lower.Contains(bad, StringComparison.Ordinal), $"literal \"{lit}\" contains forbidden fragment \"{bad}\"");
            }

            Assert.False(ForbiddenWords.IsMatch(lit), $"literal \"{lit}\" contains a forbidden app-server surface word");
            Assert.DoesNotContain("openai", lower, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoMemberReference_ToEnvironmentReads_Network_Registry_OrShellExecution()
    {
        string[] bad = MemberRefs(Codex.Location)
            .Where(m =>
                m.StartsWith("System.Environment::GetEnvironmentVariable", StringComparison.Ordinal)
                || m.StartsWith("System.Environment::GetEnvironmentVariables", StringComparison.Ordinal)
                || m.StartsWith("System.Environment::ExpandEnvironmentVariables", StringComparison.Ordinal)
                || m.StartsWith("System.Environment::get_CommandLine", StringComparison.Ordinal)
                || m.StartsWith("System.Environment::GetCommandLineArgs", StringComparison.Ordinal)
                || m.StartsWith("System.Net.", StringComparison.Ordinal)
                || m.StartsWith("Microsoft.Win32.Registry", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(bad);

        // exactly one Process.Start call site, inside the launcher
        string src = Path.Combine(FakeRig.RepoRoot(), "src", "QuotaDuck.Codex");
        string[] startSites = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && File.ReadAllText(f).Contains("Process.Start(", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToArray()!;
        Assert.Equal(["CodexProcess.cs"], startSites);
    }

    [Fact]
    public void LaunchInfo_UsesShellExecuteFalse_ArgumentList_AndNeverACommandLineString()
    {
        string text = File.ReadAllText(Path.Combine(FakeRig.RepoRoot(), "src", "QuotaDuck.Codex", "CodexProcess.cs"));
        Assert.Contains("UseShellExecute = false", text);
        Assert.Contains("ArgumentList.Add", text);
        Assert.DoesNotContain(".Arguments =", text);
        Assert.DoesNotContain("UseShellExecute = true", text);
        Assert.DoesNotContain("Verb", text);
        Assert.Contains("Environment.Clear()", text);
        Assert.Contains("IsPathFullyQualified", text);
    }

    [Fact]
    public void PublicSurface_IsOnlyTheReaderAndItsResultTypes()
    {
        string[] exported = Codex.GetExportedTypes().Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["CodexQuotaReader", "CodexReadFailure", "CodexReadOptions", "CodexReadOutcome"], exported);
        // the only way to read is ReadAsync(CancellationToken): no way to inject a message, a path, an argument or an environment
        MethodInfo[] methods = typeof(CodexQuotaReader).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Equal(["ReadAsync", "get_ProcessesStarted"], methods.Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal([typeof(CancellationToken)], methods.Single(m => m.Name == "ReadAsync").GetParameters().Select(p => p.ParameterType).ToArray());
        ConstructorInfo[] ctors = typeof(CodexQuotaReader).GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        Assert.Equal([typeof(CodexReadOptions), typeof(TimeProvider)], Assert.Single(ctors).GetParameters().Select(p => p.ParameterType).ToArray());
    }

    [Fact]
    public void ProductionSources_PassTheDenylistScript()
    {
        string script = Path.Combine(FakeRig.RepoRoot(), "scripts", "check-denylist.ps1");
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000);
        Assert.True(p.ExitCode == 0, output);
        Assert.Contains("denylist OK", output);
    }

    [Fact]
    public void TheClock_IsInjected_TimeProviderSystemAppearsOnlyInThePublicConstructor()
    {
        string src = Path.Combine(FakeRig.RepoRoot(), "src", "QuotaDuck.Codex");
        foreach (string f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")))
        {
            string text = File.ReadAllText(f);
            Assert.DoesNotContain("DateTime.UtcNow", text);
            Assert.DoesNotContain("DateTimeOffset.UtcNow", text);
            Assert.DoesNotContain("DateTime.Now", text);
            Assert.DoesNotContain("DateTimeOffset.Now", text);
            if (Path.GetFileName(f) != "CodexQuotaReader.cs") Assert.DoesNotContain("TimeProvider.System", text);
        }
    }

    [Fact]
    public void NoFileIsWrittenOrRead_ByTheRunner_ExceptReadingTheExecutableMetadata()
    {
        string src = Path.Combine(FakeRig.RepoRoot(), "src", "QuotaDuck.Codex");
        foreach (string f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")))
        {
            string text = File.ReadAllText(f);
            foreach (string api in new[] { "File.Write", "File.Append", "File.Create", "FileStream", "StreamWriter", "File.ReadAll", "File.Open", "Directory.Create", "File.Delete", "File.Copy", "File.Move" })
            {
                Assert.False(text.Contains(api, StringComparison.Ordinal), $"{Path.GetFileName(f)} uses {api}: the runner must not persist or read files");
            }
        }
    }

    private static IEnumerable<string> UserStrings(string dll)
    {
        using FileStream fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();
        UserStringHandle h = MetadataTokens.UserStringHandle(0);
        while (true)
        {
            h = md.GetNextHandle(h);
            if (h.IsNil) yield break;
            yield return md.GetUserString(h);
        }
    }

    private static IEnumerable<string> MemberRefs(string dll)
    {
        using FileStream fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();
        foreach (MemberReferenceHandle h in md.MemberReferences)
        {
            MemberReference mr = md.GetMemberReference(h);
            string name = md.GetString(mr.Name);
            string parent = mr.Parent.Kind switch
            {
                HandleKind.TypeReference => TypeName(md, (TypeReferenceHandle)mr.Parent),
                _ => "?",
            };
            yield return parent + "::" + name;
        }
    }

    private static string TypeName(MetadataReader md, TypeReferenceHandle h)
    {
        TypeReference tr = md.GetTypeReference(h);
        string ns = md.GetString(tr.Namespace);
        string n = md.GetString(tr.Name);
        return ns.Length == 0 ? n : ns + "." + n;
    }
}
