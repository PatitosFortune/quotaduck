# WPF shell and tray (gate 5)

`src/QuotaDuck.App` (`QuotaDuck.exe`, WinExe, net10.0-windows, framework-dependent) is a **thin consumer of `AppSnapshot`**. It is the only
project that references WPF/WinForms. It references Core (types) and the Coordinator (service) only — not the Codex runner, not the bridge.

```
tray (WinForms NotifyIcon) ─┐
FlyoutWindow (WPF)          ├─ ShellController ── IQuotaService ── QuotaCoordinator (approved gate 4)
SystemEvents (resume/unlock/time) ┘      │
                              ShellViewModel ── Presenter (pure: AppSnapshot -> display models) ── IssueText (bounded enum -> safe text)
```

* `Presentation/` — pure and string-testable: `Presenter` (snapshot -> models + tray tooltip), `IssueText` (the only place a `ProviderIssue`
  becomes words), `TimeText`. No WPF types, no clock reads (the snapshot carries its instant).
* `Shell/` — `ShellController` (lifecycle glue only), `ShellViewModel`, abstractions (`IFlyout`, `ITray`, `IUiTicker`, `IUiDispatcher`,
  `IEnvironmentEvents`, `IAppLifetime`) and the platform implementations in `Platform.cs`.
* `Views/FlyoutWindow` — plain window + data templates, placeholder visuals (text, progress bars, bordered cards). No art, no animation.
* `Services/IQuotaService` — what the shell may ask of the coordinator: `Capture`, `Start`, `SetUiVisible`, `NotifyEnvironmentChanged`,
  `RefreshCodexAsync`, `Changed`, dispose. There is deliberately no "refresh Claude" and no raw provider access.

## Tray
Placeholder yellow-disc icon (rendered once into an in-memory ICO at 16/32 px). Left click toggles the flyout; context menu **Open** and
**Exit**; tooltip = compact status (`QuotaDuck | Claude 5h 42% wk 12% | Codex 5h 31% wk 35%`, <= 127 chars) updated **only when a
snapshot changes and the text differs** — never on a timer. A second launch asks the running instance to open its flyout (named event) and exits.

## Flyout
Created lazily on first open. Hides (never closes) on the close button and **Esc**; only Exit really closes it. Window visibility is the
single source of truth: every show/hide raises one event that calls `SetUiVisible(...)` on the coordinator and starts/stops the 1 Hz
countdown ticker (which exists **only while visible**). Bottom-right of the work area, not topmost, not in the taskbar, `SizeToContent=Height`,
layout rounding on, PerMonitorV2 DPI manifest. Tab cycles; the Refresh button is the only focusable control; automation names on the
window, progress bars and button. High Contrast switches tone colours to system text colours; every state is also spelled in words. No
animations exist; `ShellViewModel.ReducedMotion` carries `!SystemParameters.ClientAreaAnimation` for the art to come.

## Providers
* **Claude** — headline from the issue/reason ("Waiting for Claude Code in a terminal", "Reported by Claude Code", "No recent report…",
  "Waiting for the next Claude Code report"), the surface limitation footer, 5-hour and weekly rows (exactly the windows Core reports).
  **No button of any kind.** Elapsed windows show "Reset; new value unknown", never the old number, never 0%.
* **Codex** — "Not read yet" / "Reading…" / "Up to date" / bounded failure text, whatever windows it really reports (none are invented),
  last-successful-read age, and the **Refresh** button.

## Manual refresh button
`Refresh` (enabled) → `Reading…` (disabled, while a read is in flight) → `Refresh in N s` (disabled, N rounded **up**, from
`CodexRefreshAllowedAt`) → `Refresh`. A click only forwards `RefreshCodexAsync()`; the coordinator owns single-flight and the cooldown.
Results: cooldown → "Codex can be refreshed at most once a minute."; failure → "The refresh did not work[: <bounded reason>]." (shown 8 s).
An exception from the service is swallowed into the same failure text.

## Environment hooks
`SystemEventsEnvironment` maps Windows notifications (pure `EnvironmentMapping`): resume from sleep, session unlock (also console/remote
reconnect) and system-time change all call `NotifyEnvironmentChanged()` — nothing else, no scheduling in the UI. Subscriptions are
removed on exit (static events).

## Exit
Tray icon disposed (disappears at once) -> environment hooks removed -> flyout hidden (coordinator is told `SetUiVisible(false)`) -> coordinator
disposed (cancels an in-flight Codex read; the runner kills the process tree) -> `Application.Shutdown()` (in a `finally`).

## Known limits of this placeholder gate
WinForms `NotifyIcon` is used for the tray (adds the WinForms assembly; a direct `Shell_NotifyIcon` implementation would remove it — decide
with the art gate). The flyout is not anchored to the actual tray position. No settings UI (provider enable flags exist only in code).
