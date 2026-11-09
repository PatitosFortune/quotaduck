using QuotaDuck.App.Presentation;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.App.Tests;

/// <summary>The three "copy usage as text" buttons through the real controller and view model with fakes: capture time, clipboard failures, no refresh.</summary>
public class CopyUsageTests
{
    private static readonly DateTimeOffset T = Snap.T0;

    private static ShellRig Rig()
    {
        var r = new ShellRig();
        r.Service.OnCapture = () => Snap.Make(r.Clock.Now,
            new ClaudeState(Snap.Rec(42, T.AddHours(3), T.AddMinutes(-3)), Snap.Rec(12, T.AddDays(4), T.AddMinutes(-3))),
            Snap.Codex(T.AddMinutes(-2)));
        r.Controller.Show();
        return r;
    }

    [Theory]
    [InlineData(ExportScope.Codex, "CODEX", "CLAUDE")]
    [InlineData(ExportScope.Claude, "CLAUDE", "CODEX")]
    public void SingleProviderButtons_CopyOnlyThatProvider(ExportScope scope, string present, string absent)
    {
        ShellRig r = Rig();
        r.Flyout.ClickCopyUsage(scope);
        string text = Assert.Single(r.Clipboard.Texts);
        Assert.Contains(present, text);
        Assert.DoesNotContain(absent, text);
    }

    [Fact]
    public void CopyBoth_CopiesBothProviders()
    {
        ShellRig r = Rig();
        r.Flyout.ClickCopyUsage(ExportScope.Both);
        string text = Assert.Single(r.Clipboard.Texts);
        Assert.Contains("CODEX", text);
        Assert.Contains("CLAUDE", text);
    }

    [Fact]
    public void CapturedTime_IsTheClickInstant_NotTheLastPublishedSnapshot()
    {
        ShellRig r = Rig();
        r.Service.Current = Snap.Make(T);                                                   // the last PUBLISHED snapshot is from 10:00
        r.Clock.Now = T.AddMinutes(7);                                                      // the click happens at 10:07
        r.Flyout.ClickCopyUsage(ExportScope.Both);
        string text = Assert.Single(r.Clipboard.Texts);
        Assert.Contains("Captured: 2030-01-01 10:07 +00:00", text);
        Assert.Contains("Last successful read: 2030-01-01 09:58 +00:00 (9 min ago)", text);   // ages are measured at the click, not at 10:00
    }

    [Fact]
    public void CopyingNeverStartsAProviderRead_OrAnyOtherServiceCall()
    {
        ShellRig r = Rig();
        int visibilityBefore = r.Service.Visibility.Count, envBefore = r.Service.EnvironmentCalls;
        foreach (ExportScope scope in Enum.GetValues<ExportScope>())
        {
            r.Flyout.ClickCopyUsage(scope);
        }

        Assert.Equal(0, r.Service.RefreshCalls);                                            // no Codex read, no cooldown touched
        Assert.Equal(visibilityBefore, r.Service.Visibility.Count);
        Assert.Equal(envBefore, r.Service.EnvironmentCalls);
        Assert.Equal(3, r.Clipboard.Texts.Count);
    }

    [Fact]
    public void Success_FlashesCopiedOnThatButtonOnly_AndAPoliteNotice_ThenReverts()
    {
        ShellRig r = Rig();
        r.Flyout.ClickCopyUsage(ExportScope.Codex);
        Assert.Equal("Copied", r.Vm.Compact.Codex.CopyText);
        Assert.Equal("Copy", r.Vm.Compact.Claude.CopyText);
        Assert.Equal("Copy both", r.Vm.Compact.CopyBothText);
        Assert.Equal("Copied Codex usage to the clipboard.", r.Vm.Compact.RefreshFeedback);

        r.Clock.Now = T.AddSeconds(3);
        r.Ticker.Tick();
        Assert.Equal("Copy", r.Vm.Compact.Codex.CopyText);
        Assert.Null(r.Vm.Compact.RefreshFeedback);

        r.Flyout.ClickCopyUsage(ExportScope.Both);
        Assert.Equal("Copied", r.Vm.Compact.CopyBothText);
        Assert.Equal("Copied Claude and Codex usage to the clipboard.", r.Vm.Compact.RefreshFeedback);
    }

    [Fact]
    public void ClipboardFailure_OrException_IsReportedBriefly_NeverThrown_AndNothingIsCopied()
    {
        ShellRig r = Rig();
        r.Clipboard.Succeeds = false;
        r.Flyout.ClickCopyUsage(ExportScope.Claude);
        Assert.Equal("Not copied", r.Vm.Compact.Claude.CopyText);
        Assert.Equal("Could not copy: the clipboard is busy. Try again.", r.Vm.Compact.RefreshFeedback);

        r.Clipboard.Throws = true;
        r.Flyout.ClickCopyUsage(ExportScope.Both);                                          // an exception from the OS clipboard is swallowed
        Assert.Equal("Not copied", r.Vm.Compact.CopyBothText);
        Assert.Empty(r.Clipboard.Texts);

        r.Clock.Now = T.AddSeconds(6);
        r.Ticker.Tick();
        Assert.Equal("Copy", r.Vm.Compact.Claude.CopyText);
        Assert.Equal("Copy both", r.Vm.Compact.CopyBothText);
        Assert.Null(r.Vm.Compact.RefreshFeedback);

        r.Clipboard.Throws = false;
        r.Clipboard.Succeeds = true;
        r.Flyout.ClickCopyUsage(ExportScope.Both);                                          // and it works again afterwards
        Assert.Single(r.Clipboard.Texts);
    }

    [Fact]
    public void AFailingSnapshotCapture_IsReportedAsNotCopied_NeverThrown()
    {
        ShellRig r = Rig();
        bool first = true;
        Func<AppSnapshot> normal = r.Service.OnCapture!;
        r.Service.OnCapture = () =>
        {
            if (first)
            {
                first = false;
                throw new InvalidOperationException("boom");                                // only the copy's own capture fails; the later repaint is fine
            }

            return normal();
        };
        r.Vm.CopyUsage(ExportScope.Both);
        Assert.Empty(r.Clipboard.Texts);
        Assert.Equal("Not copied", r.Vm.Compact.CopyBothText);
    }

    [Fact]
    public void ButtonNames_AreExplicit_ForScreenReaders()
    {
        ShellRig r = Rig();
        Assert.Equal("Copy Claude usage as text to the clipboard", r.Vm.Compact.Claude.CopyName);
        Assert.Equal("Copy Codex usage as text to the clipboard", r.Vm.Compact.Codex.CopyName);
    }

    [Fact]
    public void WithoutAClipboard_CopyDoesNothing()
    {
        var vm = new QuotaDuck.App.Shell.ShellViewModel(new FakeService(), new FakeTicker(), new FakeClock(T), Snap.Utc);
        vm.CopyUsage(ExportScope.Both);                                                     // no clipboard service wired: no-op, no throw
        Assert.Equal("Copy both", vm.Compact.CopyBothText);
    }
}
