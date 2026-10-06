using System.Diagnostics;

namespace QuotaDuck.Bridge.Tests;

/// <summary>Locates the built executables (managed apphosts by default; Native AOT builds via environment overrides).</summary>
internal static class Exes
{
    /// <summary>The test-seam executable (QD_BRIDGE_EXE overrides, e.g. with its Native AOT build).</summary>
    public static string TestSeam() =>
        Find("QD_BRIDGE_EXE", Path.Combine("tests", "QuotaDuck.Bridge.TestSeam"), "quotaduck-statusline-testseam.exe");

    /// <summary>The PRODUCTION executable (QD_PRODUCTION_EXE overrides, e.g. with its Native AOT build). Tests must only run it
    /// with inputs that cannot write state, because its state path is fixed to the user's real one.</summary>
    public static string Production() =>
        Find("QD_PRODUCTION_EXE", Path.Combine("src", "QuotaDuck.Bridge"), "quotaduck-statusline.exe");

    private static string Find(string overrideVar, string projectDir, string exeName)
    {
        string? overridePath = Environment.GetEnvironmentVariable(overrideVar);
        if (!string.IsNullOrEmpty(overridePath))
        {
            Assert.True(File.Exists(overridePath), $"{overrideVar} does not exist: {overridePath}");
            return overridePath;
        }

        string root = BridgeArchitectureTests.RepoRoot();
        string[] candidates = new[] { "Release", "Debug" }
            .Select(c => Path.Combine(root, projectDir, "bin", c, "net10.0", exeName))
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        Assert.True(candidates.Length > 0, $"built executable not found: {exeName}; build the solution first");
        return candidates[0];
    }

    /// <summary>Runs an executable with stdin bytes and an explicit extra environment; returns (exit code, stdout, stderr).</summary>
    public static (int Exit, byte[] Stdout, byte[] Stderr) Run(string exe, byte[] stdin, IDictionary<string, string> extraEnvironment, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach ((string k, string v) in extraEnvironment)
        {
            psi.Environment[k] = v;
        }

        using Process p = Process.Start(psi)!;
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        Task t1 = p.StandardOutput.BaseStream.CopyToAsync(stdout);
        Task t2 = p.StandardError.BaseStream.CopyToAsync(stderr);
        try
        {
            p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
            p.StandardInput.BaseStream.Flush();
        }
        catch (IOException)
        {
        }

        try
        {
            p.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        Assert.True(p.WaitForExit(60_000), "process did not exit");
        Task.WaitAll(t1, t2);
        return (p.ExitCode, stdout.ToArray(), stderr.ToArray());
    }
}
