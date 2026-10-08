using System.Text.Json;
using QuotaDuck.App.Presentation;
using QuotaDuck.App.Shell;
using QuotaDuck.Coordinator;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.App.Tests;

/// <summary>First-run Claude guidance: text, snippets, when it shows, and that the shell never edits or reads Claude settings.</summary>
public class SetupGuideTests
{
    private static readonly DateTimeOffset T = Snap.T0;

    private static SetupContext Ctx(ClaudeSetupState s, string? path = null, bool copied = false, bool failed = false) => new(s, path, copied, failed);

    // ---------------- snippets ----------------

    [Fact]
    public void TheStatusLineEntry_IsValidJson_WithTheExactShapeClaudeCodeExpects()
    {
        string snippet = SetupGuidePresenter.StatusLineEntry("C:/Tools/quotaduck-statusline.exe");
        using JsonDocument doc = JsonDocument.Parse(snippet);
        JsonElement entry = doc.RootElement.GetProperty("statusLine");
        Assert.Equal("command", entry.GetProperty("type").GetString());
        Assert.Equal("\"C:/Tools/quotaduck-statusline.exe\"", entry.GetProperty("command").GetString());     // always a quoted shell word
        Assert.Equal(2, entry.EnumerateObject().Count());                                 // nothing else (no wrapper, no extra options)
        Assert.Single(doc.RootElement.EnumerateObject());
    }

    [Fact]
    public void TheTapLine_FeedsTheSameJsonToTheBridge_AndPrintsNothing()
    {
        Assert.Equal("echo \"$input\" | \"C:/Tools/quotaduck-statusline.exe\" --tap", SetupGuidePresenter.TapLine("C:/Tools/quotaduck-statusline.exe"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\QuotaDuck\quotaduck-statusline.exe", "C:/Program Files/QuotaDuck/quotaduck-statusline.exe")]
    [InlineData(@"D:\a b\c.exe", "D:/a b/c.exe")]
    [InlineData(null, "C:/Path/To/quotaduck-statusline.exe")]
    [InlineData("", "C:/Path/To/quotaduck-statusline.exe")]
    [InlineData("  ", "C:/Path/To/quotaduck-statusline.exe")]
    public void ThePath_UsesForwardSlashes_OrAPlaceholder(string? path, string expected) => Assert.Equal(expected, SetupGuidePresenter.ExeText(path));

    [Fact]
    public void AQuoteInThePath_CanNeverBreakOutOfTheJsonString()
    {
        string snippet = SetupGuidePresenter.StatusLineEntry(SetupGuidePresenter.ExeText("C:\\x\"y\\quotaduck-statusline.exe"));
        using JsonDocument doc = JsonDocument.Parse(snippet);                              // still valid
        Assert.Equal("\"C:/xy/quotaduck-statusline.exe\"", doc.RootElement.GetProperty("statusLine").GetProperty("command").GetString());
    }

    // ---------------- the three variants ----------------

    [Theory]
    [InlineData(ClaudeSetupState.NotConfigured)]
    [InlineData(ClaudeSetupState.Unknown)]
    public void NoStatusLine_ShowsTheEntryToAdd_WithTheLimitationAndRemoval(ClaudeSetupState state)
    {
        SetupGuideModel g = SetupGuidePresenter.Build(Ctx(state, @"C:\Apps\QuotaDuck\quotaduck-statusline.exe"));
        Assert.Equal(SetupVariant.Connect, g.Variant);
        Assert.True(g.HasSnippet);
        Assert.Contains("C:/Apps/QuotaDuck/quotaduck-statusline.exe", g.Snippet);
        Assert.Contains("\"statusLine\"", g.Snippet);
        Assert.Equal(string.Empty, g.PathNote);                                           // a real path: nothing to replace
        Assert.Contains("Claude Code terminal sessions", g.Intro);
        Assert.Contains("VS Code Claude panel", g.Limitation);                             // the limitation is stated (in the details under the intro)
        Assert.Contains("Claude Desktop Code tab", g.Limitation);
        Assert.Contains("did not trigger the status line", g.Limitation);
        Assert.Contains("Only terminal sessions report", g.Limitation);
        Assert.Contains("%USERPROFILE%\\.claude\\settings.json", g.StepsBefore);
        Assert.Contains("Start Claude Code in a terminal", g.StepsAfter);
        Assert.Contains("never edits your Claude settings", g.RemoveText);
        Assert.Contains("delete the \"statusLine\" entry", g.RemoveText);                 // how to undo it
        Assert.Equal(state == ClaudeSetupState.Unknown, g.Intro.Contains("could not check", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExistingStatusLine_IsNeverReplaced_TheTapLineIsOffered()
    {
        SetupGuideModel g = SetupGuidePresenter.Build(Ctx(ClaudeSetupState.OtherStatusLine));
        Assert.Equal(SetupVariant.TapExisting, g.Variant);
        Assert.Contains("will not replace it", g.Intro);
        Assert.Contains("--tap", g.Snippet);
        Assert.DoesNotContain("\"statusLine\"", g.Snippet);                               // no replacement entry is offered
        Assert.Contains("C:/Path/To/quotaduck-statusline.exe", g.Snippet);
        Assert.Contains("Replace C:/Path/To/", g.PathNote);                               // the placeholder is explained
        Assert.Contains("output is unchanged", g.RemoveText);
        Assert.Contains("delete that line", g.RemoveText);
    }

    [Theory]
    [InlineData(ClaudeSetupState.NotConfigured)]
    [InlineData(ClaudeSetupState.Unknown)]
    [InlineData(ClaudeSetupState.OtherStatusLine)]
    [InlineData(ClaudeSetupState.BridgeConfigured)]
    public void EveryVariant_StatesThePanelAndDesktopLimitation(ClaudeSetupState s)
    {
        SetupGuideModel m = SetupGuidePresenter.Build(Ctx(s));
        Assert.Contains("VS Code Claude panel", m.Limitation);
        Assert.Contains("Claude Desktop Code tab", m.Limitation);
        Assert.Contains("terminal", m.Limitation);
        Assert.Contains("In our testing", m.Limitation);
    }

    [Fact]
    public void AlreadyWired_JustSaysWaiting_NoSnippetNoCopy()
    {
        SetupGuideModel g = SetupGuidePresenter.Build(Ctx(ClaudeSetupState.BridgeConfigured));
        Assert.Equal(SetupVariant.ConnectedWaiting, g.Variant);
        Assert.False(g.HasSnippet);
        Assert.Equal(string.Empty, g.Snippet);
        Assert.Contains("first Claude Code message", g.Title);
        Assert.Contains("delete the \"statusLine\" entry", g.RemoveText);
        Assert.Equal(string.Empty, SetupGuidePresenter.SnippetFor(Ctx(ClaudeSetupState.BridgeConfigured)));
    }

    [Theory]
    [InlineData(false, false, "Copy")]
    [InlineData(true, false, "Copied")]
    [InlineData(false, true, "Couldn't copy: select the text")]
    public void TheCopyLabel_ReflectsTheResult(bool copied, bool failed, string label) =>
        Assert.Equal(label, SetupGuidePresenter.Build(Ctx(ClaudeSetupState.NotConfigured, null, copied, failed)).CopyLabel);

    [Fact]
    public void EveryVariant_PromisesNoAutomaticEdits_AndHasAnAccessibleSummary()
    {
        foreach (ClaudeSetupState s in Enum.GetValues<ClaudeSetupState>())
        {
            SetupGuideModel g = SetupGuidePresenter.Build(Ctx(s));
            string all = string.Join(" ", g.Title, g.Intro, g.Limitation, g.StepsBefore, g.StepsAfter, g.RemoveText, g.PathNote);
            Assert.Contains("never edits your Claude settings", all);
            foreach (string bad in new[] { "we will edit", "will be modified", "automatically", "apply for you", "Exception", "C:\\Users" })
            {
                Assert.DoesNotContain(bad, all, StringComparison.OrdinalIgnoreCase);
            }

            Assert.False(string.IsNullOrWhiteSpace(g.AccessibleName));
            Assert.Contains(g.Title, g.AccessibleName);
            Assert.Contains(g.Limitation, g.AccessibleName);                               // a screen reader gets the limitation even while the details are collapsed
        }
    }

    // ---------------- when it shows ----------------

    [Fact]
    public void TheGuide_ShowsOnlyWhileClaudeHasNeverReported_AndReplacesTheEmptyRows()
    {
        ShellPresentation waiting = Presenter.Present(Snap.Make(T), RefreshFeedback.None, Snap.Utc, Ctx(ClaudeSetupState.NotConfigured));
        Assert.NotNull(waiting.Claude.Setup);
        Assert.False(waiting.Claude.ShowWindows);
        Assert.Equal("Waiting for Claude Code in a terminal", waiting.Claude.Headline);

        ClaudeState data = new(Snap.Rec(42, T.AddHours(3), T.AddMinutes(-3)), Snap.Rec(12, T.AddDays(4), T.AddMinutes(-3)));
        ShellPresentation live = Presenter.Present(Snap.Make(T, data), RefreshFeedback.None, Snap.Utc, Ctx(ClaudeSetupState.NotConfigured));
        Assert.Null(live.Claude.Setup);                                                   // data exists: no first-run guidance
        Assert.True(live.Claude.ShowWindows);

        Assert.Null(Presenter.Present(Snap.Make(T), RefreshFeedback.None, Snap.Utc).Claude.Setup);          // no context (never checked / hidden): none
        Assert.Null(waiting.Codex.Setup);                                                 // Codex never gets a Claude guide
        Assert.Null(Presenter.Present(Snap.Make(T, claudeEnabled: false), RefreshFeedback.None, Snap.Utc, Ctx(ClaudeSetupState.NotConfigured)).Claude.Setup);
    }

    [Fact]
    public void Claude_StillHasNoRefreshOrFetchAffordance_EvenWithTheGuide()
    {
        ProviderCardModel claude = Presenter.Present(Snap.Make(T), RefreshFeedback.None, Snap.Utc, Ctx(ClaudeSetupState.OtherStatusLine)).Claude;
        Assert.Null(claude.Refresh);
        string all = JsonSerializer.Serialize(claude.Setup);
        Assert.DoesNotContain("Refresh", all);
        Assert.DoesNotContain("fetch", all, StringComparison.OrdinalIgnoreCase);
    }
}

public class SetupGuideShellTests
{
    private static ShellRig Rig(ClaudeSetupState s = ClaudeSetupState.NotConfigured, string? exe = null)
    {
        var r = new ShellRig();
        r.SetupInfo.State = s;
        r.SetupInfo.BridgeExePath = exe;
        return r;
    }

    [Fact]
    public void NothingIsChecked_UntilTheFlyoutOpens_ThenOnceEachTime()
    {
        ShellRig r = Rig();
        Assert.Equal(0, r.SetupInfo.Checks);                                             // startup / tray-only: Claude settings are not touched
        r.Controller.Show();
        Assert.Equal(1, r.SetupInfo.Checks);
        Assert.NotNull(r.Vm.Current.Claude.Setup);
        r.Ticker.Tick();
        r.Ticker.Tick();
        r.Service.Raise(Snap.Make(Snap.T0));
        r.Dispatcher.Pump();
        Assert.Equal(1, r.SetupInfo.Checks);                                             // no polling while open
        r.Controller.Hide();
        Assert.Equal(1, r.SetupInfo.Checks);                                             // none while hidden
        r.SetupInfo.State = ClaudeSetupState.BridgeConfigured;                           // the user set it up meanwhile
        r.Controller.Show();
        Assert.Equal(2, r.SetupInfo.Checks);
        Assert.Equal(SetupVariant.ConnectedWaiting, r.Vm.Current.Claude.Setup!.Variant);
    }

    [Fact]
    public void NotChecked_WhenClaudeAlreadyHasData_OrWhenNoSetupInfoIsWired()
    {
        ShellRig r = Rig();
        r.Service.Current = Snap.Make(Snap.T0, new ClaudeState(Snap.Rec(42, Snap.T0.AddHours(3), Snap.T0.AddMinutes(-3)), null));
        r.Controller.Show();
        Assert.Equal(0, r.SetupInfo.Checks);
        Assert.Null(r.Vm.Current.Claude.Setup);

        var plain = new FakeService();
        var vm = new ShellViewModel(plain, new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc);
        vm.SetFlyoutVisible(true);
        Assert.Null(vm.Current.Claude.Setup);
    }

    [Fact]
    public void AProbeFailure_BecomesUnknown_NeverAnException()
    {
        ShellRig r = Rig();
        r.SetupInfo.Throw = true;
        r.Controller.Show();
        Assert.Equal(SetupVariant.Connect, r.Vm.Current.Claude.Setup!.Variant);
        Assert.Contains("could not check", r.Vm.Current.Claude.Setup.Intro);
    }

    [Fact]
    public void Copy_PutsTheExactSnippetOnTheClipboard_AndShowsCopiedBriefly()
    {
        ShellRig r = Rig(ClaudeSetupState.NotConfigured, @"C:\Apps\quotaduck-statusline.exe");
        r.Controller.Show();
        r.Flyout.ClickCopySetup();
        Assert.Equal(SetupGuidePresenter.StatusLineEntry("C:/Apps/quotaduck-statusline.exe"), Assert.Single(r.Clipboard.Texts));
        Assert.Equal("Copied", r.Vm.Current.Claude.Setup!.CopyLabel);
        r.Clock.Now = Snap.T0.AddSeconds(3);
        r.Ticker.Tick();
        Assert.Equal("Copy", r.Vm.Current.Claude.Setup!.CopyLabel);
    }

    [Fact]
    public void Copy_ForAnExistingStatusLine_CopiesTheTapLine()
    {
        ShellRig r = Rig(ClaudeSetupState.OtherStatusLine);
        r.Controller.Show();
        r.Vm.CopySetup();
        Assert.Equal("echo \"$input\" | \"C:/Path/To/quotaduck-statusline.exe\" --tap", Assert.Single(r.Clipboard.Texts));
    }

    [Fact]
    public void ACopyFailure_OrAClipboardException_IsReportedBriefly_NeverThrown()
    {
        ShellRig r = Rig();
        r.Controller.Show();
        r.Clipboard.Succeeds = false;
        r.Vm.CopySetup();
        Assert.Equal("Couldn't copy: select the text", r.Vm.Current.Claude.Setup!.CopyLabel);
        r.Clipboard.Throws = true;
        r.Vm.CopySetup();                                                                 // an exception from the OS clipboard is swallowed
        Assert.Equal("Couldn't copy: select the text", r.Vm.Current.Claude.Setup!.CopyLabel);
        r.Clock.Now = Snap.T0.AddSeconds(6);
        r.Ticker.Tick();
        Assert.Equal("Copy", r.Vm.Current.Claude.Setup!.CopyLabel);
    }

    [Fact]
    public void Copy_DoesNothing_ForTheAlreadyConnectedVariant_OrWithoutAGuide()
    {
        ShellRig r = Rig(ClaudeSetupState.BridgeConfigured);
        r.Controller.Show();
        r.Vm.CopySetup();
        Assert.Empty(r.Clipboard.Texts);
        ShellRig none = new();
        none.Vm.CopySetup();
        Assert.Empty(none.Clipboard.Texts);
    }
}
