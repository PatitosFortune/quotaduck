using System.Text;
using QuotaDuck.Core.Codex;

namespace QuotaDuck.Codex.Tests;

/// <summary>The exact launch contract: arguments, working directory, environment, framing — proven through a real child process.</summary>
public class LaunchTests
{
    [Fact]
    public void ProductionSpec_IsExactlyTheDocumentedOne()
    {
        var exe = new CodexExecutable(@"C:\Program Files\WindowsApps\OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0\app\resources\codex.exe", "P", "1.0.0.0");
        CodexLaunchSpec spec = CodexLaunch.Production(exe);
        Assert.Equal(exe.Path, spec.ExecutablePath);
        Assert.Equal(["app-server", "--stdio", "-c", "analytics.enabled=false"], spec.Arguments);
        Assert.Equal(Environment.SystemDirectory, spec.WorkingDirectory);
        Assert.Equal(["SystemRoot"], spec.Environment.Keys.ToArray());
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows), spec.Environment["SystemRoot"]);
        Assert.True(Path.IsPathFullyQualified(spec.WorkingDirectory));
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("listen", StringComparison.OrdinalIgnoreCase) || a.Contains("proxy", StringComparison.OrdinalIgnoreCase)
            || a.Contains("daemon", StringComparison.OrdinalIgnoreCase) || a.Contains("exec", StringComparison.OrdinalIgnoreCase) || a.Contains("strict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Launcher_PassesExactlyTheSpec_AndNothingInheritedFromAHostileParentEnvironment()
    {
        string[] hostile =
        [
            "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "CODEX_HOME", "CODEX_API_KEY", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NODE_OPTIONS",
            "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "PATHEXT", "COMSPEC", "QD_CANARY",
        ];
        var saved = hostile.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (string k in new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "CODEX_HOME", "CODEX_API_KEY", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NODE_OPTIONS", "HOME", "QD_CANARY" })
            {
                Environment.SetEnvironmentVariable(k, "CANARY_" + k);
            }

            using var rig = new FakeRig { Scenario = "echo-env" };
            CodexReadOutcome o = await rig.Reader().ReadAsync();
            Assert.True(o.Success, o.Failure?.ToString());

            string[] lines = rig.RecordLines().ToArray();
            string[] names = lines.Where(l => l.StartsWith("ENV:", StringComparison.Ordinal)).Select(l => l[4..]).ToArray();
            Assert.Equal(["SystemRoot"], names, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("SYSROOT:" + Environment.GetFolderPath(Environment.SpecialFolder.Windows), lines.Single(l => l.StartsWith("SYSROOT:", StringComparison.Ordinal)));
            Assert.Equal("CWD:" + Environment.SystemDirectory, lines.Single(l => l.StartsWith("CWD:", StringComparison.Ordinal)), StringComparer.OrdinalIgnoreCase);

            string[] args = lines.Where(l => l.StartsWith("ARG:", StringComparison.Ordinal)).Select(l => l[4..]).ToArray();
            Assert.Equal(CodexLaunch.ProductionArguments, args.Take(4).ToArray());
            Assert.DoesNotContain("CANARY", string.Join("\n", lines), StringComparison.Ordinal);
        }
        finally
        {
            foreach ((string k, string? v) in saved)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    [Fact]
    public void Launcher_RefusesARelativeOrBarePath()
    {
        var l = new ProcessAppServerLauncher();
        CodexLaunchSpec Spec(string exe) => new(exe, [], Environment.SystemDirectory, new Dictionary<string, string>());
        Assert.Throws<AppServerStartException>(() => l.Start(Spec("codex.exe")));
        Assert.Throws<AppServerStartException>(() => l.Start(Spec(@"..\codex.exe")));
        Assert.Throws<AppServerStartException>(() => l.Start(Spec(@"app\resources\codex.exe")));
    }

    [Fact]
    public void Launcher_NeverUsesTheCurrentDirectoryOrPath_ForAnAbsoluteMissingExe()
    {
        using var dir = new TempDir();
        // a decoy with the right name in a directory we make current must not be found through a bare name
        File.Copy(Environment.ProcessPath!, dir.File("codex.exe"));
        string old = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir.Path;
            var l = new ProcessAppServerLauncher();
            Assert.Throws<AppServerStartException>(() => l.Start(new CodexLaunchSpec("codex.exe", [], Environment.SystemDirectory, new Dictionary<string, string>())));
            Directory.CreateDirectory(dir.File("sub"));
            Environment.CurrentDirectory = dir.File("sub");
            Assert.Throws<AppServerStartException>(() => l.Start(new CodexLaunchSpec(@"..\codex.exe", [], Environment.SystemDirectory, new Dictionary<string, string>())));   // would resolve against the cwd
        }
        finally
        {
            Environment.CurrentDirectory = old;
        }
    }

    [Fact]
    public void Protocol_HasExactlyThreeMessages_EachConstant_NoBom_Lf_StartsWithBrace()
    {
        Assert.Equal(["Initialize", "Initialized", "ReadRateLimits"], Enum.GetNames<OutboundMessage>());
        string[] expected =
        [
            "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"quotaduck\",\"title\":\"QuotaDuck\",\"version\":\"0.1.0\"}}}\n",
            "{\"method\":\"initialized\"}\n",
            "{\"id\":2,\"method\":\"account/rateLimits/read\"}\n",
        ];
        foreach (OutboundMessage m in Enum.GetValues<OutboundMessage>())
        {
            byte[] b = CodexProtocol.Bytes(m).ToArray();
            Assert.Equal(expected[(int)m - 1], Encoding.UTF8.GetString(b));
            Assert.Equal((byte)'{', b[0]);
            Assert.Equal((byte)'\n', b[^1]);
            Assert.DoesNotContain((byte)'\r', b);
            Assert.DoesNotContain(b, x => x >= 0x80);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => CodexProtocol.Bytes((OutboundMessage)99).ToArray());
    }

    [Fact]
    public async Task Input_CanOnlySendTheFixedMessages()
    {
        // The channel's only transmit method takes the enum; there is no overload taking text, bytes, or a stream.
        var methods = typeof(AppServerInput).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.Equal(["Close", "SendAsync"], methods.Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal([typeof(OutboundMessage), typeof(CancellationToken)], methods.Single(m => m.Name == "SendAsync").GetParameters().Select(p => p.ParameterType).ToArray());

        var ms = new MemoryStream();
        await new AppServerInput(ms).SendAsync(OutboundMessage.Initialized, CancellationToken.None);
        Assert.Equal("{\"method\":\"initialized\"}\n", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task LineReader_HandlesFragments_CrLf_BlankLines_AndOversize()
    {
        static BoundedLineReader R(params string[] chunks) => new(new ChunkedStream(chunks.Select(Encoding.UTF8.GetBytes).ToArray()), 100);
        BoundedLineReader a = R("{\"a\"", ":1}\r\n", "\n{\"b\":2}", "\n");
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString((await a.ReadLineAsync(default)).Line.Span));
        Assert.Equal(0, (await a.ReadLineAsync(default)).Line.Length);
        Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString((await a.ReadLineAsync(default)).Line.Span));
        Assert.Equal(LineKind.Eof, (await a.ReadLineAsync(default)).Kind);

        BoundedLineReader partial = R("{\"never\":\"finished\"");
        Assert.Equal(LineKind.Eof, (await partial.ReadLineAsync(default)).Kind);                 // trailing partial line is discarded

        BoundedLineReader big = R(new string('x', 500));
        Assert.Equal(LineKind.TooLong, (await big.ReadLineAsync(default)).Kind);
        BoundedLineReader bigWithNewline = R(new string('x', 500) + "\n");
        Assert.Equal(LineKind.TooLong, (await bigWithNewline.ReadLineAsync(default)).Kind);
        BoundedLineReader exact = R(new string('x', 100) + "\n");
        Assert.Equal(LineKind.Line, (await exact.ReadLineAsync(default)).Kind);
    }

    private sealed class ChunkedStream(byte[][] chunks) : Stream
    {
        private int _i;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_i >= chunks.Length) return ValueTask.FromResult(0);
            byte[] c = chunks[_i++];
            c.CopyTo(buffer);
            return ValueTask.FromResult(c.Length);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
