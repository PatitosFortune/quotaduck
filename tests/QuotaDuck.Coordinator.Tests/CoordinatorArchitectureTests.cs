using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using QuotaDuck.Core;

namespace QuotaDuck.Coordinator.Tests;

/// <summary>Structural enforcement of the coordinator's security and independence constraints.</summary>
public class CoordinatorArchitectureTests
{
    private static readonly Assembly Coord = typeof(QuotaCoordinator).Assembly;

    private static string Root()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "QuotaDuck.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"));

    [Fact]
    public void CoordinatorReferences_BclCoreAndCodexRunnerOnly_NoNetworkNoProcessNoRegistry()
    {
        string[] refs = Coord.GetReferencedAssemblies().Select(r => r.Name!).ToArray();
        Assert.All(refs, n => Assert.True(
            n.StartsWith("System", StringComparison.Ordinal) || n is "netstandard" or "mscorlib" or "Microsoft.Win32.Primitives" or "QuotaDuck.Core" or "QuotaDuck.Codex",
            $"unexpected assembly reference {n}"));
        string[] forbidden =
        [
            "System.Net", "System.Diagnostics.Process", "Microsoft.Win32.Registry", "System.Data", "System.Management", "System.Reflection.Emit",
            "System.ServiceProcess", "System.IO.Pipes", "System.Security.Cryptography", "PresentationFramework", "WindowsBase", "System.Windows",
        ];
        Assert.DoesNotContain(refs, r => forbidden.Any(f => r.StartsWith(f, StringComparison.Ordinal)));
    }

    [Fact]
    public void ProcessStart_ExistsInExactlyOneProductionFile_TheCodexRunnersLauncher()
    {
        string[] sites = new[] { "QuotaDuck.Core", "QuotaDuck.Bridge", "QuotaDuck.Codex", "QuotaDuck.Coordinator" }
            .SelectMany(Sources)
            .Where(f => File.ReadAllText(f).Contains("Process.Start", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Root(), f).Replace('\\', '/'))
            .ToArray();
        Assert.Equal(["src/QuotaDuck.Codex/CodexProcess.cs"], sites);
        Assert.DoesNotContain(MemberRefs(Coord.Location), m => m.Contains("System.Diagnostics.Process", StringComparison.Ordinal));
    }

    [Fact]
    public void Coordinator_NeverWrites_DeletesOrLocksAnything_TheBridgeIsTheOnlyWriterOfClaudeState()
    {
        foreach (string f in Sources("QuotaDuck.Coordinator"))
        {
            string text = File.ReadAllText(f);
            foreach (string api in new[]
                     {
                         "File.Write", "File.Append", "File.Create", "File.Delete", "File.Move", "File.Copy", "File.Replace", "File.Open(",
                         "FileMode.Create", "FileMode.Append", "FileMode.OpenOrCreate", "FileMode.CreateNew", "FileMode.Truncate", "FileAccess.Write", "FileAccess.ReadWrite",
                         "StreamWriter", "Directory.Create", "Directory.Delete", "Mutex", "FileShare.None",
                     })
            {
                Assert.False(text.Contains(api, StringComparison.Ordinal), $"{Path.GetFileName(f)} uses {api}");
            }
        }
    }

    [Fact]
    public void NoEnvironmentReads_NoCommandLine_NoPathsFromSettings()
    {
        string[] bad = MemberRefs(Coord.Location)
            .Where(m => m.StartsWith("System.Environment::GetEnvironmentVariable", StringComparison.Ordinal)
                        || m.StartsWith("System.Environment::ExpandEnvironmentVariables", StringComparison.Ordinal)
                        || m.StartsWith("System.Environment::GetCommandLineArgs", StringComparison.Ordinal)
                        || m.StartsWith("System.Environment::get_CommandLine", StringComparison.Ordinal)
                        || m.StartsWith("System.Net.", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(bad);
    }

    [Fact]
    public void CompiledStringLiterals_ContainNoUrlsCredentialsConfigPathsOrProviderSecrets()
    {
        string[] forbidden =
        [
            "http://", "https://", "ws://", "hkey_", "currentversion\\run", ".claude", ".codex", "settings.json", "auth.json", "credential", "cookie",
            "authorization", "bearer", "api_key", "apikey", "token", "password", "anthropic", "openai", "powershell", "cmd.exe", "codex.exe", "account/", "--listen",
        ];
        foreach (string lit in UserStrings(Coord.Location))
        {
            string lower = lit.ToLowerInvariant();
            foreach (string bad in forbidden)
            {
                Assert.False(lower.Contains(bad, StringComparison.Ordinal), $"literal \"{lit}\" contains \"{bad}\"");
            }
        }
    }

    [Fact]
    public void PublicSurface_ExposesOnlyBoundedValues_NoStringsExceptionsPathsStreamsOrProcesses()
    {
        Type[] allowedLeaf =
        [
            typeof(bool), typeof(int), typeof(long), typeof(double), typeof(DateTimeOffset), typeof(TimeSpan), typeof(ProviderView),
        ];
        foreach (Type t in Coord.GetExportedTypes().Where(t => t.IsClass && t.Name is "AppSnapshot" or "ProviderSnapshot" or "ScheduledCodexRead" or "CodexRefreshResult"))
        {
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                Type pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                bool ok = allowedLeaf.Contains(pt) || pt.IsEnum || pt.Name is "ProviderSnapshot" or "ScheduledCodexRead";
                Assert.True(ok, $"{t.Name}.{p.Name} exposes {pt.FullName}");
            }
        }

        Assert.DoesNotContain(Coord.GetExportedTypes(), t => typeof(Exception).IsAssignableFrom(t));
    }

    [Fact]
    public void TheClock_IsInjected_NoDirectWallClockReads()
    {
        foreach (string f in Sources("QuotaDuck.Coordinator"))
        {
            string text = File.ReadAllText(f);
            Assert.DoesNotContain("DateTime.Now", text);
            Assert.DoesNotContain("DateTime.UtcNow", text);
            Assert.DoesNotContain("DateTimeOffset.Now", text);
            Assert.DoesNotContain("DateTimeOffset.UtcNow", text);
            Assert.DoesNotContain("Stopwatch", text);
            Assert.DoesNotContain("Task.Delay", text);
            Assert.DoesNotContain("Thread.Sleep", text);
            if (Path.GetFileName(f) != "Production.cs")
            {
                Assert.DoesNotContain("TimeProvider.System", text);
                Assert.DoesNotContain("new Timer(", text);
            }
        }
    }

    [Fact]
    public void ProviderDerivations_AreIndependent_EachFromItsOwnInputOnly()
    {
        // the coordinator derives the two providers through QuotaDeriver (one input per provider); there must be no code that
        // feeds one provider's windows/attempt into the other's input
        string text = File.ReadAllText(Path.Combine(Root(), "src", "QuotaDuck.Coordinator", "QuotaCoordinator.cs"));
        Assert.Contains("ClaudeObservations.ToProviderInput(_claudeState.FiveHour, _claudeState.SevenDay, _options.ClaudeEnabled)", text);
        Assert.Contains("CodexObservations.ToProviderInput(_codexSet, _codexAttempt, _options.CodexEnabled)", text);
        Assert.DoesNotContain("_claudeState", text[text.IndexOf("RunReadAsync(CancellationToken token)", StringComparison.Ordinal)..text.IndexOf("internal static ProviderIssue MapFailure", StringComparison.Ordinal)]);
        string claudeRead = text[text.IndexOf("private void ReadClaudeLocked", StringComparison.Ordinal)..text.IndexOf("private ScheduledCodexRead? NextCodexLocked", StringComparison.Ordinal)];
        Assert.DoesNotContain("_codex", claudeRead.Replace("_codexSet", "").Replace("_codexNext", ""));
    }

    [Fact]
    public void ProductionSources_PassTheDenylistScript()
    {
        string script = Path.Combine(Root(), "scripts", "check-denylist.ps1");
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

    private static IEnumerable<string> MemberRefs(string dll)
    {
        using FileStream fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();
        foreach (MemberReferenceHandle h in md.MemberReferences)
        {
            MemberReference mr = md.GetMemberReference(h);
            string parent = mr.Parent.Kind == HandleKind.TypeReference ? TypeName(md, (TypeReferenceHandle)mr.Parent) : "?";
            yield return parent + "::" + md.GetString(mr.Name);
        }
    }

    private static string TypeName(MetadataReader md, TypeReferenceHandle h)
    {
        TypeReference tr = md.GetTypeReference(h);
        string ns = md.GetString(tr.Namespace);
        return ns.Length == 0 ? md.GetString(tr.Name) : ns + "." + md.GetString(tr.Name);
    }
}
