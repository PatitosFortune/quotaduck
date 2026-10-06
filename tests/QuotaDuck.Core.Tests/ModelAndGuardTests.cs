using System.Reflection;

namespace QuotaDuck.Core.Tests;

public class LabelAndKeyTests
{
    [Theory]
    [InlineData(300, "5-hour")]
    [InlineData(1_440, "Daily")]
    [InlineData(10_080, "Weekly")]
    [InlineData(43_200, "Monthly")]
    [InlineData(525_600, "Annual")]
    public void KnownDurations_MapExactly(double minutes, string expected)
    {
        WindowLabel l = WindowLabels.ForCodex(CodexSlot.Secondary, TimeSpan.FromMinutes(minutes));
        Assert.Equal((expected, LabelOrigin.DerivedFromDuration), (l.Text, l.Origin));
    }

    [Theory]
    [InlineData(299.9)]
    [InlineData(301)]
    [InlineData(10_079)]
    [InlineData(0)]
    [InlineData(-300)]
    public void NearMissDurations_AreNotGuessed(double minutes)
    {
        Assert.Equal(LabelOrigin.Positional, WindowLabels.ForCodex(CodexSlot.Primary, TimeSpan.FromMinutes(minutes)).Origin);
    }

    [Fact]
    public void PositionalLabels_FollowSlot_ClaudeLabelsAreFixed()
    {
        Assert.Equal("Primary", WindowLabels.ForCodex(CodexSlot.Primary, null).Text);
        Assert.Equal("Secondary", WindowLabels.ForCodex(CodexSlot.Secondary, null).Text);
        Assert.Equal(LabelOrigin.Fixed, WindowLabels.ClaudeFiveHour.Origin);
        Assert.Equal("Weekly", WindowLabels.ClaudeSevenDay.Text);
    }

    [Fact]
    public void Keys_OrderIsDisplayOrder_AndKeysAreProviderScoped()
    {
        Assert.True(WindowKey.TryCreateCodex("c", CodexSlot.Primary, out WindowKey p));
        Assert.True(WindowKey.TryCreateCodex("c", CodexSlot.Secondary, out WindowKey s));
        Assert.True(string.CompareOrdinal(p.Id, s.Id) < 0);
        Assert.True(string.CompareOrdinal(WindowKey.ClaudeFiveHour.Id, WindowKey.ClaudeSevenDay.Id) < 0);
        Assert.NotEqual(WindowKey.ClaudeFiveHour, WindowKey.ClaudeSevenDay);
        Assert.Equal(ProviderId.Codex, p.Provider);
        Assert.False(WindowKey.TryCreateCodex("c", (CodexSlot)7, out _));
    }

    [Fact]
    public void DefaultKey_IsSafeToPrint()
    {
        Assert.Equal("(default)", default(WindowKey).ToString());
    }
}

public class PolicyTests
{
    [Fact]
    public void Defaults_MatchTheApprovedSpec_AndAreValid()
    {
        FreshnessPolicy p = FreshnessPolicy.Default;
        Assert.Equal(TimeSpan.FromMinutes(10), p.PushFreshWithin);
        Assert.Equal(TimeSpan.FromHours(2), p.PushQuietWithin);
        Assert.Equal(TimeSpan.FromMinutes(20), p.PolledFreshWithin);
        Assert.Equal(TimeSpan.FromHours(2), p.PolledQuietWithin);
        Assert.Equal(TimeSpan.FromSeconds(60), p.PersistReceivedAtEvery);
        Assert.Equal(3, p.LowerAcceptCount);
        Assert.Same(p, p.Validated());
    }

    [Fact]
    public void Invalid_Policies_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (FreshnessPolicy.Default with { PushFreshWithin = TimeSpan.Zero }).Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => (FreshnessPolicy.Default with { FutureTolerance = TimeSpan.FromSeconds(-1) }).Validated());
        Assert.Throws<ArgumentException>(() => (FreshnessPolicy.Default with { PushFreshWithin = TimeSpan.FromHours(3) }).Validated());
        Assert.Throws<ArgumentException>(() => (FreshnessPolicy.Default with { PolledFreshWithin = TimeSpan.FromHours(2) }).Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => (FreshnessPolicy.Default with { LowerAcceptCount = 1 }).Validated());
    }

    [Fact]
    public void ForCodexPollInterval_IsTwoIntervals_AndRejectsTooLong()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), FreshnessPolicy.ForCodexPollInterval(TimeSpan.FromMinutes(15)).PolledFreshWithin);
        Assert.Throws<ArgumentException>(() => FreshnessPolicy.ForCodexPollInterval(TimeSpan.FromHours(1)));   // 2 h fresh == quiet bound
    }
}

/// <summary>Fixture hygiene + structural enforcement of CLAUDE.md constraints on the Core assembly.</summary>
public class ArchitectureGuardTests
{
    private static readonly Assembly Core = typeof(WindowKey).Assembly;

    [Fact]
    public void Fixtures_AreAnchoredToTheFictionalEpoch()
    {
        Assert.Equal(2030, Fx.T0.Year);
        Assert.Equal(TimeSpan.Zero, Fx.T0.Offset);
    }

    [Fact]
    public void Core_ReferencesOnlyTheBaseClassLibrary()
    {
        string[] refs = Core.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.NotEmpty(refs);
        Assert.All(refs, n => Assert.True(
            n.StartsWith("System", StringComparison.Ordinal) || n is "netstandard" or "mscorlib" or "Microsoft.CSharp",
            $"unexpected reference: {n}"));
    }

    [Fact]
    public void Core_HasNoNetworkRegistryProcessOrFileAccessCapability()
    {
        string[] forbiddenPrefixes =
        [
            "System.Net", "Microsoft.Win32", "System.Diagnostics.Process", "System.IO.FileSystem", "System.IO.Pipes",
            "System.IO.MemoryMappedFiles", "System.Data", "System.Reflection.Emit", "System.Security.Cryptography",
            "System.Runtime.InteropServices.JavaScript", "System.Management",
        ];
        string[] refs = Core.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        foreach (string r in refs)
        {
            Assert.DoesNotContain(forbiddenPrefixes, f => r.StartsWith(f, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EveryDeriveAndMergeEntryPoint_TakesNowExplicitly()
    {
        // Public-surface contract only (not an IL scan): every deriving/merging entry point must accept 'now',
        // so time is always injected and the state table is deterministic to test.
        var derivers = new[] { typeof(WindowDeriver), typeof(ProviderDeriver), typeof(QuotaDeriver), typeof(Claude.ClaudeWindowMerger) };
        foreach (Type t in derivers)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name is "Derive" or "Merge"))
            {
                Assert.Contains(m.GetParameters(), p => p.Name == "now");
            }
        }
    }

    [Fact]
    public void PublicSurface_ExposesNoRawProviderPayloadTypes()
    {
        // No JsonElement/JsonDocument/Stream/byte[] in any public signature of Core: raw payloads cannot cross into Core.
        // byte[] is exempt ONLY for ClaudeStateCodec, which encodes/decodes QuotaDuck's own persisted schema (never a provider
        // payload). The provider-payload entry point takes ReadOnlySpan<byte> and returns numbers/flags only (tested separately).
        Type[] banned = [typeof(System.Text.Json.JsonElement), typeof(System.Text.Json.JsonDocument), typeof(Stream), typeof(byte[])];
        foreach (Type t in Core.GetExportedTypes().Where(t => t != typeof(Claude.ClaudeStateCodec)))
        {
            foreach (MemberInfo mem in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                IEnumerable<Type> used = mem switch
                {
                    PropertyInfo p => [p.PropertyType],
                    MethodInfo m => m.GetParameters().Select(x => x.ParameterType).Append(m.ReturnType),
                    ConstructorInfo c => c.GetParameters().Select(x => x.ParameterType),
                    FieldInfo f => [f.FieldType],
                    _ => [],
                };
                Assert.DoesNotContain(used, u => banned.Contains(u));
            }
        }
    }
}
