using System.Text;

namespace QuotaDuck.Coordinator.Tests;

/// <summary>The narrow, read-only statusLine look: four outcomes, nothing else escapes, nothing is written.</summary>
public class ClaudeSetupProbeTests
{
    private static ClaudeSetupState Check(string json)
    {
        using var t = new Temp();
        string path = Path.Combine(t.Path, "settings.json");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
        return new ClaudeSetupProbe(path).Check();
    }

    [Fact]
    public void MissingFile_OrDirectory_IsNotConfigured_NullPathIsUnknown()
    {
        using var t = new Temp();
        Assert.Equal(ClaudeSetupState.NotConfigured, new ClaudeSetupProbe(Path.Combine(t.Path, "settings.json")).Check());
        Assert.Equal(ClaudeSetupState.NotConfigured, new ClaudeSetupProbe(Path.Combine(t.Path, "nope", "settings.json")).Check());
        Assert.Equal(ClaudeSetupState.Unknown, new ClaudeSetupProbe(null).Check());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"model\":\"x\",\"permissions\":{\"allow\":[\"Bash\"]}}")]
    [InlineData("{\"env\":{\"statusLine\":{\"command\":\"quotaduck-statusline.exe\"}}}")]              // nested look-alike: not a top-level entry
    [InlineData("{\"hooks\":[{\"statusLine\":\"x\"}],\"other\":{\"statusLine\":{}}}")]
    public void NoTopLevelStatusLine_IsNotConfigured(string json) => Assert.Equal(ClaudeSetupState.NotConfigured, Check(json));

    [Theory]
    [InlineData("{\"statusLine\":{\"type\":\"command\",\"command\":\"C:/Tools/quotaduck-statusline.exe\"}}")]
    [InlineData("{\"statusLine\":{\"type\":\"command\",\"command\":\"C:\\\\Tools\\\\QuotaDuck-StatusLine.EXE\"}}")]          // escaped backslashes, any case
    [InlineData("{\"statusLine\":{\"command\":\"\\\"C:/Program Files/QuotaDuck/quotaduck-statusline.exe\\\" --tap\"}}")]
    [InlineData("{\"a\":1,\"statusLine\":{\"padding\":0,\"command\":\"quotaduck-statusline\",\"type\":\"command\"},\"b\":[1,2]}")]
    public void TheBridgeCommand_IsRecognised(string json) => Assert.Equal(ClaudeSetupState.BridgeConfigured, Check(json));

    [Theory]
    [InlineData("{\"statusLine\":{\"type\":\"command\",\"command\":\"~/.claude/my-status.sh\"}}")]
    [InlineData("{\"statusLine\":{\"type\":\"command\",\"command\":\"npx ccusage statusline\"}}")]
    [InlineData("{\"statusLine\":\"~/.claude/my-status.sh\"}")]
    [InlineData("{\"statusLine\":{\"type\":\"command\"}}")]
    [InlineData("{\"statusLine\":{\"command\":42}}")]
    [InlineData("{\"statusLine\":null}")]
    public void AnythingElse_IsAnExistingStatusLine_NeverOverwritten(string json) => Assert.Equal(ClaudeSetupState.OtherStatusLine, Check(json));

    [Fact]
    public void LastStatusLineWins_LikeAnyJsonRead()
    {
        Assert.Equal(ClaudeSetupState.BridgeConfigured, Check("{\"statusLine\":{\"command\":\"x.sh\"},\"statusLine\":{\"command\":\"quotaduck-statusline.exe\"}}"));
        Assert.Equal(ClaudeSetupState.OtherStatusLine, Check("{\"statusLine\":{\"command\":\"quotaduck-statusline.exe\"},\"statusLine\":{\"command\":\"x.sh\"}}"));
    }

    [Fact]
    public void BomCommentsAndTrailingCommas_AreTolerated_Garbage_IsUnknown()
    {
        using var t = new Temp();
        string path = Path.Combine(t.Path, "settings.json");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"statusLine\":{\"command\":\"quotaduck-statusline.exe\"}}")]);
        Assert.Equal(ClaudeSetupState.BridgeConfigured, new ClaudeSetupProbe(path).Check());
        Assert.Equal(ClaudeSetupState.BridgeConfigured, Check("{ // note\n \"statusLine\": { \"command\": \"quotaduck-statusline.exe\", }, }"));
        Assert.Equal(ClaudeSetupState.Unknown, Check("{ not json"));
        Assert.Equal(ClaudeSetupState.Unknown, Check("[1,2,3]"));
        Assert.Equal(ClaudeSetupState.Unknown, Check("{\"statusLine\":{\"command\":\"quotaduck-statusline.exe\""));          // truncated
        Assert.Equal(ClaudeSetupState.Unknown, Check(new string('[', 200)));                                                  // absurd nesting
    }

    [Fact]
    public void AFileOverTheCap_IsUnknown_NeverReadFurther()
    {
        string big = "{\"padding\":\"" + new string('x', ClaudeSetupProbe.MaxBytes) + "\",\"statusLine\":{\"command\":\"quotaduck-statusline.exe\"}}";
        Assert.Equal(ClaudeSetupState.Unknown, Check(big));
        string ok = "{\"padding\":\"" + new string('x', ClaudeSetupProbe.MaxBytes - 200) + "\",\"statusLine\":{\"command\":\"quotaduck-statusline.exe\"}}";
        Assert.Equal(ClaudeSetupState.BridgeConfigured, Check(ok));
    }

    [Fact]
    public void ALockedFile_IsUnknown_AnotherReaderOrWriterDoesNotBlockUs_AndNothingIsWritten()
    {
        using var t = new Temp();
        string path = Path.Combine(t.Path, "settings.json");
        byte[] original = Encoding.UTF8.GetBytes("{\"apiKeyHelper\":\"CANARY_SECRET\",\"statusLine\":{\"command\":\"x.sh\"}}");
        File.WriteAllBytes(path, original);
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal(ClaudeSetupState.OtherStatusLine, new ClaudeSetupProbe(path).Check());           // an editor holding it open (shared) is fine
        }

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(ClaudeSetupState.Unknown, new ClaudeSetupProbe(path).Check());                   // exclusively locked: no claim either way
        }

        Assert.Equal(original, File.ReadAllBytes(path));                                                 // byte-identical
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal("settings.json", Assert.Single(Directory.GetFiles(t.Path).Select(f => Path.GetFileName(f))));   // nothing created beside it
    }

    [Fact]
    public void TheResultTypeCarriesNothingButTheEnum()
    {
        Assert.Equal("BridgeConfigured,NotConfigured,OtherStatusLine,Unknown", string.Join(",", Enum.GetNames<ClaudeSetupState>().Order(StringComparer.Ordinal)));
        Assert.Equal(typeof(ClaudeSetupState), typeof(IClaudeSetupProbe).GetMethod("Check")!.ReturnType);
        Assert.Single(typeof(IClaudeSetupProbe).GetMethods());
    }

    [Fact]
    public void TheDefaultPath_IsUnderTheTokenProfile_NotTheEnvironment()
    {
        string? profile = ClaudeStateLocation.UserProfileDirectory();
        Assert.NotNull(profile);
        string? path = ClaudeSetupProbe.DefaultPath();
        Assert.Equal(Path.Combine(profile!, ".claude", "settings.json"), path);
        string saved = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
        using var evil = new Temp();
        try
        {
            Environment.SetEnvironmentVariable("USERPROFILE", evil.Path);
            Assert.Equal(path, ClaudeSetupProbe.DefaultPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERPROFILE", saved);
        }
    }
}
