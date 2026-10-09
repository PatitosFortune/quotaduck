using System.Text.RegularExpressions;
using System.Windows.Automation.Peers;
using QuotaDuck.App.Presentation;
using QuotaDuck.App.Shell;
using QuotaDuck.App.Views;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.App.Tests;

/// <summary>
/// The accessibility tree that screen readers consume (built from the real window with the real view model): every quota row must expose
/// a meaningful sentence as its name, and no element may expose an internal type dump. WPF names a generic list item by its
/// <c>ToString()</c>, which for the row records is a raw "CompactRowModel { ... }" dump unless the item container carries an explicit name.
/// </summary>
public class AccessibilityTreeTests
{
    private static readonly string[] ForbiddenTokens =
    [
        "CompactRowModel", "CompactCardModel", "CompactPresentation", "GaugeScene", "PondScene", "WindowRowModel", "ProviderCardModel",
        "SetupGuideModel", "Palette", "QuotaDuck.App", "QuotaDuck.Art", "QuotaDuck.Core", "QuotaDuck.Coordinator", "System.Collections",
    ];

    // "TypeName { Property = value, ..." : the shape of a C# record's ToString()
    private static readonly Regex RecordDump = new(@"\b[A-Z]\w*\s*\{\s*\w+\s*=", RegexOptions.Compiled);

    private static IEnumerable<AutomationPeer> Walk(AutomationPeer root)
    {
        yield return root;
        IList<AutomationPeer>? kids = root.GetChildren();
        if (kids is null)
        {
            yield break;
        }

        foreach (AutomationPeer k in kids)
        {
            foreach (AutomationPeer d in Walk(k))
            {
                yield return d;
            }
        }
    }

    private static List<AutomationPeer> Tree(FlyoutWindow w) => Walk(UIElementAutomationPeer.CreatePeerForElement(w)).ToList();

    private static FlyoutWindow Show(AppSnapshot snapshot, ClaudeSetupState? setup = null, bool expandGuide = false)
    {
        var service = new FakeService { Current = snapshot };
        ShellViewModel vm = setup is { } st
            ? new ShellViewModel(service, new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc, null, null, new FakeSetupInfo { State = st }, new FakeClipboard())
            : new ShellViewModel(service, new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc);
        var w = new FlyoutWindow(vm);
        vm.SetFlyoutVisible(true);
        ((IFlyout)w).Show();
        if (expandGuide)
        {
            vm.ToggleSetup();
        }

        Sta.Flush();
        Sta.Flush();
        return w;
    }

    private static AppSnapshot BothFresh() => Snap.Make(
        Snap.T0,
        new ClaudeState(Snap.Rec(42, Snap.T0.AddHours(3), Snap.T0.AddMinutes(-3)), Snap.Rec(12, Snap.T0.AddDays(4), Snap.T0.AddMinutes(-3))),
        Snap.Codex(Snap.T0.AddMinutes(-1)));

    private static void AssertNothingExposesTypeText(IEnumerable<AutomationPeer> peers)
    {
        foreach (AutomationPeer p in peers)
        {
            foreach ((string what, string text) in new[] { ("name", p.GetName()), ("help text", p.GetHelpText()), ("item status", p.GetItemStatus()) })
            {
                foreach (string bad in ForbiddenTokens)
                {
                    Assert.False(text.Contains(bad, StringComparison.Ordinal), $"{p.GetClassName()} exposes internal type text '{bad}' in its {what}: {text}");
                }

                Assert.False(RecordDump.IsMatch(text), $"{p.GetClassName()} exposes a record dump in its {what}: {text}");
            }
        }
    }

    private static List<(string Name, CompactRowModel Row)> RowPeers(IEnumerable<AutomationPeer> peers) =>
        peers.OfType<ItemAutomationPeer>().Where(p => p.Item is CompactRowModel).Select(p => (p.GetName(), (CompactRowModel)p.Item)).ToList();

    [Theory]
    [InlineData(0, "Claude 5-hour: 42% used, 58% left")]
    [InlineData(1, "Claude Weekly: 12% used, 88% left")]
    [InlineData(2, "Codex 5-hour: 31% used, 69% left")]
    [InlineData(3, "Codex Weekly: 35% used, 65% left")]
    public void EachQuotaRow_ExposesItsAccessibleSentence_AsItsName(int index, string startsWith)
    {
        Sta.Run(() =>
        {
            FlyoutWindow w = Show(BothFresh());
            List<(string Name, CompactRowModel Row)> rows = RowPeers(Tree(w));
            Assert.Equal(4, rows.Count);                                                         // Claude 5-hour, Claude weekly, Codex 5-hour, Codex weekly
            (string name, CompactRowModel row) = rows[index];
            Assert.Equal(row.AccessibleName, name);                                              // the explicit name, not the record's ToString()
            Assert.StartsWith(startsWith, name, StringComparison.Ordinal);
            Assert.Contains("Fresh", name, StringComparison.Ordinal);
            Assert.Contains("Resets in", name, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(name));
        });
    }

    [Fact]
    public void NoAccessibilityElement_ExposesRecordOrTypeText_WhenBothProvidersHaveData()
    {
        Sta.Run(() =>
        {
            FlyoutWindow w = Show(BothFresh());
            List<AutomationPeer> peers = Tree(w);
            Assert.True(peers.Count > 20, "the accessibility tree should have been walked");
            AssertNothingExposesTypeText(peers);
        });
    }

    [Fact]
    public void NoAccessibilityElement_ExposesRecordOrTypeText_ForSleepingElapsedAndFailedStates()
    {
        Sta.Run(() =>
        {
            ClaudeState elapsed = new(Snap.Rec(80, Snap.T0.AddMinutes(-20), Snap.T0.AddHours(-4)), Snap.Rec(55, Snap.T0.AddDays(3), Snap.T0.AddHours(-3)));
            FlyoutWindow w = Show(Snap.Make(Snap.T0, elapsed, Snap.Codex(Snap.T0.AddMinutes(-15)), codexIssue: ProviderIssue.TemporaryProviderFailure,
                attempt: new ProviderAttempt(Snap.T0.AddMinutes(-1), ProviderErrorKind.Timeout)));
            List<AutomationPeer> peers = Tree(w);
            List<(string Name, CompactRowModel Row)> rows = RowPeers(peers);
            Assert.Equal(4, rows.Count);
            Assert.All(rows, r => Assert.Equal(r.Row.AccessibleName, r.Name));
            AssertNothingExposesTypeText(peers);
        });
    }

    [Fact]
    public void NoAccessibilityElement_ExposesRecordOrTypeText_WithTheFirstRunGuideOpen()
    {
        Sta.Run(() =>
        {
            FlyoutWindow w = Show(Snap.Make(Snap.T0, null, Snap.Codex(Snap.T0.AddMinutes(-1))), ClaudeSetupState.NotConfigured, expandGuide: true);
            List<AutomationPeer> peers = Tree(w);
            // Claude's two placeholder rows exist in the tree but are collapsed (the guide replaces them): they are not exposed as content,
            // Codex's two rows are; and every row, exposed or not, carries a proper name rather than a record dump
            List<ItemAutomationPeer> rowPeers = peers.OfType<ItemAutomationPeer>().Where(p => p.Item is CompactRowModel).ToList();
            Assert.Equal(4, rowPeers.Count);
            Assert.All(rowPeers, p => Assert.Equal(((CompactRowModel)p.Item).AccessibleName, p.GetName()));
            List<ItemAutomationPeer> exposed = rowPeers.Where(p => p.IsContentElement()).ToList();
            Assert.Equal(2, exposed.Count);
            Assert.All(exposed, p => Assert.StartsWith("Codex ", p.GetName(), StringComparison.Ordinal));
            AssertNothingExposesTypeText(peers);
        });
    }
}
