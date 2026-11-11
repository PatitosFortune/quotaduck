# rc.2 manual acceptance and the mixed-DPI flyout limitation

Recorded 2026-11-11 during `1.0.0-rc.2` preparation. "Owner-observed" means the owner reported it; nothing here is video evidence. This page records results and open questions; it does not authorize any change.

## Environment

Laptop 1920x1080 at 125% (primary); two external 1920x1080 monitors at 100% through a USB-C hub. Other Windows applications move normally between these displays, and the hub is not assumed to be a factor (the behaviour below also occurs in `1.0.0-rc.1`).

## Manual acceptance results (owner-observed unless stated)

| Check | Result |
|---|---|
| Launch; Copy Claude / Codex / both and Notepad paste (plain text, percentages, reset times, ages; no raw JSON or account data) | Passed |
| Layout at 125% on the laptop display | Passed |
| Live Claude Code terminal integration (temporary `--settings` file; CLI from `cmd`, not the VS Code panel) | Passed. Verified from files: the normal Claude `settings.json` hash was unchanged; only `bridge\claude-v1.json` (381 bytes, allow-listed fields) existed under `%LOCALAPPDATA%\QuotaDuck`. Observed: 5-hour 100% remaining, weekly 78%. |
| Stale to fresh recovery without restarting the app | Passed |
| Moving the flyout within the laptop display | Passed |
| Dragging the flyout between displays of different scale | **Known limitation** (below) |
| Win+Shift+Left / Right | No movement (not analysed) |
| Keyboard: Tab, Shift+Tab, Enter, Space (Copy buttons and Codex Refresh), Escape hides and the app stays in the tray | Passed |
| Codex Refresh: Reading... state, data age update, cooldown blocks an immediate second read | Passed. Verified: no `codex.exe` left over (the process list equalled the pre-test baseline and none had QuotaDuck as parent). |
| Second launch: the existing window opens, one tray icon | Passed. Verified: exactly one `QuotaDuck.exe` process remained. |
| Memory | Passed: 82.2 MB private working set (Task Manager "Memory"; committed 124-128 MB), measured with the flyout hidden |
| CPU while the flyout is hidden | **Accepted for rc.2, not proof of zero idle CPU**: about 1.3% of one core (0.11-0.17 CPU-seconds per 10 s over 60 s, measured), cause not investigated, not compared with rc.1 |
| Clean exit (tray Exit) | Passed. Verified: no `QuotaDuck.exe`, no bridge process and no child process left; the state folder held only `bridge\claude-v1.json` |
| Narrator or NVDA, high contrast, reduced motion, 150% / 200% scaling, 100% external-monitor visuals, stale/expired/missing labelling in Copy output, Win+V clipboard history, explicit full Tab order | **Not tested** (optional) |

## Known limitation: cross-monitor flicker (pre-existing, open)

Dragging the flyout from the 125% laptop display onto a 100% external monitor flickers rapidly once it is fully or nearly fully on the external monitor, and it eventually returns to its tray-corner position. **Owner-observed.** It happens in the published `1.0.0-rc.1` too, so it is not an rc.2 regression. Quota readings are not affected.

### Confirmed code facts

- The flyout is a `SizeToContent=WidthAndHeight` tool window in a PerMonitorV2 process.
- The only programmatic move is `Win32Monitors.MoveTo` (`SetWindowPos`), called from `FlyoutWindow.ApplyPlacement`, which always positions from the tray anchor, never from the window's current position. It runs on show and on `SizeChanged` while visible.
- The window's pixel size depends on DPI (by the layout formula: 676 px wide at 125%, 656 px at 100%), and the pixel art is rebuilt on a DPI change.
- There is no `DpiChanged`, `LocationChanged` or `WM_DPICHANGED` handling.

### Hypotheses (none confirmed)

- H1: the DPI-dependent window size makes WPF and the OS re-layout the window repeatedly at a DPI boundary.
- H2: the tray-anchored re-placement pulls a dragged window back to the tray corner.
- H3: an interaction between the OS move loop and a mid-drag size change.

### Tried and reverted

An attempt based on H2 (suppress the tray re-placement after `WM_ENTERSIZEMOVE`, commit `78eacd8`) did not change the behaviour in manual testing, and its window-message hook was never verified to receive the real OS message. It was reverted (normal `git revert`, history preserved). One observation is unreconciled: with that attempt in place the window still returned to the tray corner.

### If this is picked up

Start with a throwaway diagnostic build (not committed, not released, log written only to a scratch folder) that records `WM_ENTERSIZEMOVE` / `WM_EXITSIZEMOVE`, `WM_DPICHANGED` (suggested rectangle), `WM_WINDOWPOSCHANGING` / `WM_WINDOWPOSCHANGED`, `SizeChanged` / `LocationChanged` / `DpiChanged`, and each `ApplyPlacement` / `MoveTo` call; reproduce with one drag; then choose between DPI-stable window sizing, handling `WM_DPICHANGED`, or a non-draggable frame. Workaround for users: reopen the flyout from the tray icon.
