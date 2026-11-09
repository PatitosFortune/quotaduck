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

## v1 polish gate

### First-run Claude setup guide
Shown on the Claude card only while Claude has never reported (`ProviderIssue.WaitingForTerminalClaudeCode`), and only the flyout is involved: when it opens in that state the shell asks the coordinator's `ClaudeSetupProbe` for a four-value answer (`Unknown`, `NotConfigured`, `BridgeConfigured`, `OtherStatusLine`). The probe is the one narrow exception to "no Claude config access": it reads `%USERPROFILE%\.claude\settings.json` (real profile from the process token), streamed once, bounded to 256 KiB, every property except top-level `statusLine` skipped unread, the command compared in place for `quotaduck-statusline` and never stored. Project-level settings are never looked at. Nothing is ever written; QuotaDuck never edits Claude settings.
Variants: *Connect* (no status line: paste-this `statusLine` entry), *TapExisting* (another status line exists: never replaced; add the silent `--tap` line to the user's own script), *ConnectedWaiting* (bridge already wired: waiting for the first reply). Every variant states the observed limitation (VS Code Claude panel and Claude Desktop Code tab did not trigger the status line; only terminal sessions report) and how to undo. The Copy button puts the snippet on the clipboard; the user pastes it. The bridge path is filled in when `quotaduck-statusline.exe` sits beside the app, otherwise a visible placeholder with an instruction.

### Tray anchoring
`Shell/FlyoutPlacement.cs` is pure. Inputs: monitors (bounds, work area, per-monitor DPI), the screen position of the tray click, flyout size. The taskbar edge is inferred from work area vs bounds (bottom/top/left/right/auto-hidden); the flyout sits next to the click, a DPI-scaled gap off the taskbar, clamped inside that monitor's work area, never larger than it (taller content scrolls). A stale anchor picks the nearest monitor; no anchor or any native failure falls back to the bottom-right corner of the primary work area. Only documented APIs (`Screen.AllScreens`, `GetDpiForMonitor`, `SetWindowPos`; `Shell/NativeWindows.cs` is the only file with native interop). No shell hooks, no undocumented taskbar APIs.

### Runtime prerequisite
Framework-dependent for v1. No custom bootstrapper: the .NET apphost itself shows a dialog when the .NET Desktop Runtime is missing (names it and links to the official Microsoft download page), and nothing is installed silently. `ArchitectureTests` pins the framework-dependent settings (`UseAppHost`, no self-contained, `WindowsDesktop` runtimeconfig framework).

### Real-desktop checks (bounded)
Done on the owner's Windows 11 desktop by UI Automation, with synthetic nothing: tray right-click (menu created lazily on first right-click), Open, Exit, Esc, toggle hide/show, Tab navigation, Claude "waiting" guide, Codex refresh (real quota-only reads; cooldown disables the button). Flyout anchored ~8 px above the taskbar centred on the icon. A guide plus a populated Codex card is ~841 DIP tall: capped to the work area and scrollable.
Not toggled by the assistant (system settings): high contrast, reduced motion, DPI scaling, extra monitors. Covered by unit/placement tests only. **Owner checklist:** (1) Settings > Accessibility > Contrast themes: open the flyout, text readable, numbers visible. (2) Settings > Accessibility > Visual effects > Animation effects off: open the flyout, duck frames static. (3) Display scale 125%/150%: flyout next to the icon, fully on screen. (4) A second monitor with the taskbar there, if available.

## Compact two-pond layout (responsive hybrid)

The flyout is two provider cards, Claude on the left and Codex on the right (`Views/CompactFlyoutContent.xaml`, bound to `ShellViewModel.Compact`; `Presentation/CompactLayout.cs` reshapes the existing card models, no new provider logic). Each card: name, pond + duck, one headline, then a 5-hour and a weekly row (label, **% left** as the only visible percentage — "% used" stays in the accessible name —, gauge, compact reset line), a subordinate freshness line, and for Codex the Refresh button / cooldown. Claude has no refresh button (see "Copy usage as text" below for the Copy buttons both cards now have). Ducks keep facing the centre. Normal fully-configured operation is about 648 x 297 DIP of content (window ~664 x 336 incl. frame) and never scrolls; the old vertical layout was 380 x 607.

**Responsive policy** (`CompactLayoutPolicy`, pure, tested): side by side when the work area of the monitor the tray was clicked on is at least 700 DIP wide; otherwise the same cards stack in one 332-DIP column. Display scale alone never stacks: the pond uses an integer art scale that lands at ~288 DIP at 100/125/150/200%, so the width in DIPs is stable; 150% stays side by side wherever the work area allows.
**Text size (accessibility):** the policy has a `textScale` input but nothing feeds it. Windows' documented signal (WinRT `UISettings.TextScaleFactor`) needs a Windows-SDK target framework and a WinRT load, and WPF does not scale its own text with it, so wiring it would change nothing visible unless the content were also scaled. Documented limitation, not wired; follow-up if wanted: scale the content by that factor and stack when it exceeds 1.25.

**First-run Claude setup:** the cards stay side by side; Claude's card shows "Waiting for terminal setup" and a **Set up** button. The full guide opens below both cards (button becomes **Hide**) and collapses again without any provider state change. The expanded state lives in the view model (`SetupExpanded`) and clears itself once Claude has reported. The guide texts and the setup probe are unchanged.

**High contrast:** card borders are 2 DIP (not hairlines) so side-by-side cards stay distinct; the pond/gauge use the high-contrast palette with hatching, so state does not depend on colour.
**Placement:** unchanged (`FlyoutPlacement`); the window now sizes to content (`SizeToContent=WidthAndHeight`), the mode is chosen before placement, and the wider window is clamped to the work area like before (tests: right/left edges, left taskbar, 150%, two monitors). The window still caps at the work-area height and scrolls only for tall content (expanded guide on a small screen, stacked mode on a short screen).

## Copy usage as text

Three buttons put a plain-text usage report on the clipboard, for pasting into an AI assistant, notes or an issue: **Copy** in the Claude card, **Copy** in the Codex card, and **Copy both** under the cards. The button briefly reads "Copied" (or "Not copied" if the clipboard was busy) and the same words are announced through the polite live region used for refresh messages.

**Where it lives.** `Presentation/UsageSnapshotText.cs` (pure: `AppSnapshot` + scope + zone -> string; no clock, no I/O) and `ShellViewModel.CopyUsage(ExportScope)`. The report is built from the same `Presenter` status text and Core-derived window views the flyout uses; there is no second collector. The clipboard is the existing `IClipboard` (`WpfClipboard`, plain text).

**What the click does (and does not).** `CopyUsage` calls `IQuotaService.Capture()` at the click, which derives a snapshot at that instant without reading any provider, so `Captured:` is the real copy time and every age is measured at it (not at the last published snapshot). It never starts a Codex read, never touches the Codex cooldown, and never reads any file or setting. A failing clipboard or capture is reported briefly and never thrown.

**Format** (illustrative numbers; ASCII, culture-invariant; built with LF and put on the clipboard with Windows CRLF line endings):

```text
QuotaDuck usage snapshot
Captured: 2030-01-01 10:00 +00:00 (when this text was copied; each provider's own data time is listed below)
Percentages are usage REMAINING; the percentage used is in brackets.

CODEX
Status: Up to date
5-hour session: 69% remaining (31% used); resets 2030-01-01 12:58 +00:00 (in 2 h 58 m)
Weekly: 65% remaining (35% used); resets 2030-01-06 09:58 +00:00 (in 4 d 23 h)
Last successful read: 2030-01-01 09:58 +00:00 (2 min ago)

CLAUDE
Status: Reported by Claude Code
5-hour session: 58% remaining (42% used); resets 2030-01-01 13:00 +00:00 (in 3 h)
Weekly: 88% remaining (12% used); resets 2030-01-05 10:00 +00:00 (in 4 d)
Numbers last changed: 2030-01-01 09:57 +00:00 (3 min ago)
Status line last ran: 2030-01-01 09:59 +00:00 (about 1 min ago)
Note: Claude reports only from terminal Claude Code sessions; usage elsewhere (VS Code panel, Claude Desktop, claude.ai) may not be reflected.
```

**Rules.** Remaining is the headline number with used in brackets. Times are local with their UTC offset for that instant (daylight-saving-correct) and always carry the date. *Captured* (when copied) is separate from each provider's own time: Codex's *Last successful read* (a failed attempt never moves it) and Claude's *Numbers last changed* and *Status line last ran*. The last two differ on purpose: Claude Code can keep running the status line while the percentages stay the same. Per Claude window that is not Fresh: if the status line ran within 10 minutes the line says `unchanged since <time> (<age> ago); status line ran <age> ago` (the hook is running and reported the same value, so it is not called "old"); otherwise it says `older reading` / `OLD reading` with `last changed <age> ago`. When every window is old and the status line is also not recent, the status is "No recent report from Claude Code"; when it is recent, "Claude Code is still reporting; the numbers have not changed for a while". An expired window is always `no value`, whatever the status line is doing. Core's freshness states and ages are unchanged. "Status line last ran" is approximate (the bridge records it at most about once a minute).

| Window state | Text |
|---|---|
| Fresh | numbers, reset time |
| Quiet / Sleeping | numbers marked `older reading` / `OLD reading` with their age; for Claude with a recent status line: `unchanged since ...` instead |
| Elapsed (reset passed) | `no value (the reset time, ..., has passed; the new value has not been reported yet)`; no number |
| No data / never reported | every window whose identity is known is listed as `no data` (Claude: `5-hour session` and `Weekly`); never 0%. Codex's windows are only known from a read, so before one it prints `Windows: none known yet (...)` instead of inventing any |
| Provider turned off | `Status: Turned off`; nothing else |
| Provider error | the bounded `IssueText` sentence; last good values (if any) stay marked with their age |

**Privacy.** Only enums, percentages, window labels and timestamps can reach the text: no paths, account or plan data, credentials or raw provider output. A provider-supplied limit name is reduced to letters, digits, space, `-`, `_`, `.` and capped at 40 characters; Codex's default bucket name ("codex") is not repeated. Note that Windows clipboard history or cloud clipboard, if the user has enabled them, may retain the copied text; it contains quota percentages and times only.

**Buttons and tests.** The flyout XAML has five `<Button>` elements (Refresh, Set up, the setup guide's Copy, the per-card Copy that appears in both cards, Copy both); `ArchitectureTests` pins that number so adding a button is a reviewed decision. Tests: `UsageSnapshotTextTests` (exact strings, states, time zones incl. the spring-forward transition, nothing-but-allowed-content), `CopyUsageTests` (capture time, no refresh, clipboard failures), `FlyoutWindowTests` (the three real buttons).

**Limitations.** The report states what QuotaDuck knows; it cannot refresh Claude (only terminal Claude Code sessions report) and does not trigger a Codex read, so a stale value is exported as stale with its age. Copy is per click; there is no hotkey or auto-copy.
