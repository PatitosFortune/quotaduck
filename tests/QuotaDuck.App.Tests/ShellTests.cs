using System.Text.Json;
using Microsoft.Win32;
using QuotaDuck.App.Presentation;
using QuotaDuck.App.Shell;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;

namespace QuotaDuck.App.Tests;

/// <summary>The shell lifecycle against fakes: visibility signalling, environment forwarding, refresh behaviour, exit.</summary>
public class ShellTests
{
    // ---------------- visibility signalling and the hide/show lifecycle ----------------

    [Fact]
    public void Start_StartsTheCoordinator_Once_AndSetsTheInitialTooltip_WithoutShowingAnything()
    {
        var r = new ShellRig(start: false);
        r.Controller.Start();
        r.Controller.Start();
        Assert.Equal(1, r.Service.StartCalls);
        Assert.Equal("QuotaDuck | Claude waiting | Codex –", r.Tray.Tooltips.Single());
        Assert.False(r.Flyout.IsVisible);
        Assert.Empty(r.Service.Visibility);                                              // hidden at start: no visibility signal, so no Codex read
        Assert.False(r.Ticker.IsRunning);
    }

    [Fact]
    public void ShowingAndHidingTheFlyout_SignalsTheCoordinator_AndRunsTheTickerOnlyWhileVisible()
    {
        var r = new ShellRig();
        r.Tray.LeftClick();                                                              // toggle: show
        Assert.True(r.Flyout.IsVisible);
        Assert.Equal([true], r.Service.Visibility);
        Assert.True(r.Ticker.IsRunning);
        Assert.True(r.Vm.FlyoutVisible);

        r.Tray.LeftClick();                                                              // toggle: hide
        Assert.False(r.Flyout.IsVisible);
        Assert.Equal([true, false], r.Service.Visibility);
        Assert.False(r.Ticker.IsRunning);                                                // nothing ticks while hidden
        Assert.False(r.Vm.FlyoutVisible);

        r.Tray.MenuOpen();
        Assert.Equal([true, false, true], r.Service.Visibility);
        Assert.Equal(2, r.Ticker.Starts);
    }

    [Fact]
    public void HidingByAnyRoute_EscCloseButton_IsSignalled_BecauseTheWindowIsTheSingleSourceOfTruth()
    {
        var r = new ShellRig();
        r.Controller.Show();
        r.Flyout.Hide();                                                                 // what Esc / the close button do (window hides itself)
        Assert.Equal([true, false], r.Service.Visibility);
        Assert.False(r.Ticker.IsRunning);
    }

    [Fact]
    public void TickerTicks_RefreshThePresentation_CountdownsAdvance()
    {
        var r = new ShellRig();
        r.Service.Current = Snap.Make(Snap.T0, allowed: false, allowedAt: Snap.T0.AddSeconds(30));
        r.Controller.Show();
        Assert.Equal("Refresh in 30 s", r.Vm.Current.Codex.Refresh!.Text);
        r.Service.Current = Snap.Make(Snap.T0.AddSeconds(10), allowed: false, allowedAt: Snap.T0.AddSeconds(30));
        r.Ticker.Tick();
        Assert.Equal("Refresh in 20 s", r.Vm.Current.Codex.Refresh!.Text);
        r.Service.Current = Snap.Make(Snap.T0.AddSeconds(30), allowed: true);
        r.Ticker.Tick();
        Assert.True(r.Vm.Current.Codex.Refresh!.Enabled);
    }

    // ---------------- snapshots: marshalled to the UI thread, tooltip only on change ----------------

    [Fact]
    public void SnapshotChanges_AreMarshalledToTheUiThread_AndUpdateTheTooltipOnlyWhenItChanges()
    {
        var r = new ShellRig();
        int tooltips = r.Tray.Tooltips.Count;
        AppSnapshot s = Snap.Make(Snap.T0, codex: Snap.Codex(Snap.T0));
        r.Service.Raise(s);
        Assert.Equal(tooltips, r.Tray.Tooltips.Count);                                   // nothing happens on the background thread
        r.Dispatcher.Pump();
        Assert.Equal(tooltips + 1, r.Tray.Tooltips.Count);
        Assert.Equal("QuotaDuck | Claude waiting | Codex 5h 31% wk 35%", r.Tray.Tooltips[^1]);
        Assert.Equal("31% used", r.Vm.Current.Codex.Windows[0].PercentText);

        r.Service.Raise(s);                                                              // identical content again
        r.Dispatcher.Pump();
        Assert.Equal(tooltips + 1, r.Tray.Tooltips.Count);                               // the tray text is not touched when unchanged
    }

    // ---------------- environment forwarding ----------------

    [Fact]
    public void ResumeUnlockAndTimeChange_AreForwardedToTheCoordinatorsEntryPoint()
    {
        var r = new ShellRig();
        r.Environment.Resume();
        Assert.Equal(1, r.Service.EnvironmentCalls);
        r.Environment.Unlock();
        Assert.Equal(2, r.Service.EnvironmentCalls);
        r.Environment.Time();
        Assert.Equal(3, r.Service.EnvironmentCalls);
        Assert.Equal(0, r.Service.RefreshCalls);                                         // never a provider refresh, never any scheduling in the UI
    }

    [Theory]
    [InlineData(PowerModes.Resume, true)]
    [InlineData(PowerModes.Suspend, false)]
    [InlineData(PowerModes.StatusChange, false)]
    public void PowerMapping(PowerModes mode, bool resume) => Assert.Equal(resume, EnvironmentMapping.IsResume(mode));

    [Theory]
    [InlineData(SessionSwitchReason.SessionUnlock, true)]
    [InlineData(SessionSwitchReason.ConsoleConnect, true)]
    [InlineData(SessionSwitchReason.RemoteConnect, true)]
    [InlineData(SessionSwitchReason.SessionLock, false)]
    [InlineData(SessionSwitchReason.SessionLogon, false)]
    [InlineData(SessionSwitchReason.ConsoleDisconnect, false)]
    public void SessionMapping(SessionSwitchReason reason, bool unlock) => Assert.Equal(unlock, EnvironmentMapping.IsUnlock(reason));

    // ---------------- manual refresh ----------------

    [Fact]
    public async Task RefreshButton_CallsTheCoordinatorOnce_AndSuccessShowsNoFeedback()
    {
        var r = new ShellRig();
        r.Controller.Show();
        await r.Vm.RefreshCodexAsync();
        Assert.Equal(1, r.Service.RefreshCalls);
        Assert.Null(r.Vm.Current.RefreshFeedback);
    }

    [Fact]
    public void ClickingTheFlyoutsRefresh_IsRoutedToTheViewModel()
    {
        var r = new ShellRig();
        r.Controller.Show();
        r.Flyout.ClickRefresh();
        Assert.Equal(1, r.Service.RefreshCalls);
    }

    [Fact]
    public async Task RefreshInsideTheCooldown_ShowsATooSoonMessage_ThatExpiresAfterEightSeconds()
    {
        var r = new ShellRig();
        r.Service.OnRefresh = () => Task.FromResult(new CodexRefreshResult(CodexRefreshStatus.CooldownActive, false, Snap.T0.AddSeconds(30)));
        r.Controller.Show();
        await r.Vm.RefreshCodexAsync();
        Assert.Equal("Codex can be refreshed at most once a minute.", r.Vm.Current.RefreshFeedback);
        r.Clock.Now = Snap.T0.AddSeconds(7.9);
        r.Vm.Refresh();
        Assert.NotNull(r.Vm.Current.RefreshFeedback);
        r.Clock.Now = Snap.T0.AddSeconds(8);
        r.Vm.Refresh();
        Assert.Null(r.Vm.Current.RefreshFeedback);
    }

    [Fact]
    public async Task FailedRefresh_ShowsASafeMessage_NeverTheException()
    {
        var r = new ShellRig();
        r.Service.Current = Snap.Make(Snap.T0, codexIssue: ProviderIssue.TemporaryProviderFailure);
        r.Service.OnRefresh = () => Task.FromResult(new CodexRefreshResult(CodexRefreshStatus.Failed, false, null));
        r.Controller.Show();
        await r.Vm.RefreshCodexAsync();
        Assert.Equal("The refresh did not work: Codex did not answer in time; will try again.", r.Vm.Current.RefreshFeedback);
    }

    [Fact]
    public async Task ACoordinatorException_IsNeverShown_AndNeverCrashesTheShell()
    {
        var r = new ShellRig();
        r.Service.OnRefresh = () => throw new InvalidOperationException("CANARY_SECRET C:\\Users\\victim\\.codex\\auth.json accountId=abc");
        r.Controller.Show();
        await r.Vm.RefreshCodexAsync();
        string everything = JsonSerializer.Serialize(r.Vm.Current) + string.Join("|", r.Tray.Tooltips);
        Assert.DoesNotContain("CANARY", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("auth.json", everything, StringComparison.Ordinal);
        Assert.Equal("The refresh did not work.", r.Vm.Current.RefreshFeedback);
    }

    [Fact]
    public async Task RefreshWhileReading_IsJoinedByTheCoordinator_TheShellDoesNotDuplicateTheSingleFlightLogic()
    {
        var r = new ShellRig();
        var gate = new TaskCompletionSource<CodexRefreshResult>();
        r.Service.OnRefresh = () => gate.Task;
        Task a = r.Vm.RefreshCodexAsync();
        Task b = r.Vm.RefreshCodexAsync();
        Assert.Equal(2, r.Service.RefreshCalls);                                         // both are simply forwarded; the coordinator joins them
        gate.SetResult(new CodexRefreshResult(CodexRefreshStatus.Succeeded, false, null));
        await Task.WhenAll(a, b);
    }

    // ---------------- exit ----------------

    [Fact]
    public async Task Exit_HidesFlyout_DisposesTray_And_StopsTheCoordinator_BeforeShutdown()
    {
        var r = new ShellRig();
        r.Controller.Show();
        r.Lifetime.OnShutdown = () => r.Service.Order.Add("shutdown");
        r.Tray.MenuExit();
        await r.Controller.ExitAsync();                                                  // joins the in-progress exit
        Assert.True(r.Tray.Disposed);
        Assert.True(r.Environment.Disposed);
        Assert.False(r.Flyout.IsVisible);
        Assert.Equal(1, r.Service.DisposeCalls);
        Assert.Equal(1, r.Lifetime.ShutdownCalls);
        // the coordinator is told the UI is hidden BEFORE it is disposed, and the app only shuts down after the coordinator stopped
        Assert.Equal(["start", "visible:True", "visible:False", "dispose", "shutdown"], r.Service.Order);
    }

    [Fact]
    public async Task Exit_Twice_StopsOnce_AndStillShutsDownIfDisposeThrows()
    {
        var r = new ShellRig();
        r.Service.OnDispose = () => throw new InvalidOperationException("boom");
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Controller.ExitAsync());
        await r.Controller.ExitAsync();
        Assert.Equal(1, r.Service.DisposeCalls);
        Assert.Equal(1, r.Lifetime.ShutdownCalls);                                       // shutdown is in a finally: an exception never leaves the app running
    }

    [Fact]
    public async Task AfterExit_LateSnapshotsAndFlyoutEvents_DoNothing()
    {
        var r = new ShellRig();
        await r.Controller.ExitAsync();
        int tooltips = r.Tray.Tooltips.Count;
        int vis = r.Service.Visibility.Count;
        r.Service.Raise(Snap.Make(Snap.T0, codex: Snap.Codex(Snap.T0)));
        r.Dispatcher.Pump();
        r.Controller.Show();                                                              // e.g. a late single-instance "open" message
        Assert.Equal(tooltips, r.Tray.Tooltips.Count);
        Assert.Equal(vis, r.Service.Visibility.Count);
    }

    [Fact]
    public void Shell_HasNoClaudeRefreshOperation_AnywhereInItsServiceSurface()
    {
        string[] methods = typeof(QuotaDuck.App.Services.IQuotaService).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
        string[] expected = ["Capture", "NotifyEnvironmentChanged", "RefreshCodexAsync", "SetUiVisible", "Start", "add_Changed", "remove_Changed"];
        Assert.Equal(expected.Order(StringComparer.Ordinal), methods);
        Assert.DoesNotContain(methods, m => m.Contains("Claude", StringComparison.OrdinalIgnoreCase));
    }
}
