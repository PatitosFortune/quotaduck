using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using QuotaDuck.Core;

namespace QuotaDuck.Bridge.Tests;

/// <summary>Structural enforcement of the bridge's security constraints on the COMPILED assemblies and the sources.</summary>
public class BridgeArchitectureTests
{
    private static readonly Assembly Bridge = typeof(BridgeRunner).Assembly;
    private static readonly Assembly Core = typeof(WindowKey).Assembly;

    private static readonly string[] ForbiddenReferencePrefixes =
    [
        "System.Net",                     // no HTTP, sockets, WebSockets, NetworkInformation
        "Microsoft.Win32.Registry",
        "System.Diagnostics.Process",     // no process spawning
        "System.Security.Cryptography",
        "System.Data",
        "System.Management",
        "System.Reflection.Emit",
        "System.ServiceProcess",
        "System.Diagnostics.EventLog",
        "System.Diagnostics.TraceSource",
        "System.IO.Pipes",
        "System.IO.MemoryMappedFiles",
    ];

    // lower-case fragments that must not occur in ANY string literal of the compiled assemblies
    private static readonly string[] ForbiddenLiterals =
    [
        "http://", "https://", "ftp://", "ws://", "wss://", "hkey_", "software\\microsoft", "currentversion\\run",
        "\\.claude", "/.claude", ".claude\\", ".claude/", "settings.json", "settings.local.json", "\\.codex", "/.codex", "auth.json", "credential", "cookie", "login data",
        "authorization", "bearer", "api_key", "apikey", "anthropic", "openai", "powershell", "cmd.exe", "sqlite",
        // field names the parser deliberately never mentions (it skips them by default)
        "transcript", "session_id", "\"cwd\"", "workspace", "total_cost", "prompt", "model",
    ];

    [Fact]
    public void BridgeAndCore_ReferenceOnlyTheBaseClassLibrary_NothingForbidden()
    {
        foreach (Assembly a in new[] { Bridge, Core })
        {
            string[] refs = a.GetReferencedAssemblies().Select(r => r.Name!).ToArray();
            Assert.NotEmpty(refs);
            Assert.All(refs, n => Assert.True(
                n.StartsWith("System", StringComparison.Ordinal) || n is "netstandard" or "mscorlib" or "Microsoft.Win32.Primitives" or "QuotaDuck.Core",
                $"{a.GetName().Name} references unexpected assembly {n}"));
            foreach (string r in refs)
            {
                Assert.DoesNotContain(ForbiddenReferencePrefixes, p => r.StartsWith(p, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void CompiledStringLiterals_ContainNoUrlsRegistryCredentialsClaudeSettingsOrSkippedFieldNames()
    {
        foreach (Assembly a in new[] { Bridge, Core })
        {
            string[] literals = UserStrings(a.Location).ToArray();
            Assert.NotEmpty(literals);
            foreach (string lit in literals)
            {
                string lower = lit.ToLowerInvariant();
                foreach (string bad in ForbiddenLiterals)
                {
                    Assert.False(lower.Contains(bad, StringComparison.Ordinal), $"{a.GetName().Name}: literal \"{lit}\" contains forbidden fragment \"{bad}\"");
                }
            }
        }
    }

    [Fact]
    public void ParsedWindow_CanHoldOnlyTheAllowlistedData()
    {
        // JSON key literals are UTF-8 literals ("..."u8) that live in the data section, not the #US heap, so the literal scan above
        // cannot see them; behaviour is covered by ClaudeStatusLineParserTests (decoys + canaries). Here: the result type can hold
        // nothing but the percentage, the reset instant and flags.
        string[] expected = ["Present", "UsedPercent", "ResetsAt", "Issues", "Usable"];
        Assert.Equal(expected.Order(), typeof(Core.Claude.ClaudeParsedWindow).GetProperties().Select(p => p.Name).Order());
    }

    [Fact]
    public void BridgeAssembly_HasNoPublicSurface()
    {
        Assert.Empty(Bridge.GetExportedTypes());                                // everything is internal; tests use InternalsVisibleTo
    }

    [Fact]
    public void TheClock_IsInjected_OnlyProgramCsReadsIt()
    {
        var run = typeof(BridgeRunner).GetMethod(nameof(BridgeRunner.Run), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        Assert.Contains(run.GetParameters(), p => p.ParameterType == typeof(Func<DateTimeOffset>));

        string src = Path.Combine(RepoRoot(), "src");
        foreach (string f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")))
        {
            if (Path.GetFileName(f) == "Program.cs")
            {
                continue;
            }

            string text = File.ReadAllText(f);
            Assert.DoesNotContain("UtcNow", text);
            Assert.DoesNotContain("DateTime.Now", text);
            Assert.DoesNotContain("DateTimeOffset.Now", text);
            Assert.DoesNotContain("TimeProvider.System", text);
        }
    }

    [Fact]
    public void ProductionSources_PassTheDenylistScript()
    {
        string root = RepoRoot();
        string script = Path.Combine(root, "scripts", "check-denylist.ps1");
        Assert.True(File.Exists(script), script);
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

    internal static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "QuotaDuck.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
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
            if (h.IsNil)
            {
                yield break;
            }

            yield return md.GetUserString(h);
        }
    }
}
