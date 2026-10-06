# Quota coordinator (gate 4)

`src/QuotaDuck.Coordinator` is the application layer between the two provider sources and the (later) WPF shell. It has **no UI, no
process launching, no networking, no persistence**. Everything time-related is injected (`TimeProvider`, `ITimerScheduler`).

```
IClaudeStateWatcher ─┐                                   ┌─ AppSnapshot (Changed event / Capture())
IClaudeStateSource ──┼─► QuotaCoordinator ─ QuotaDeriver ┤   ├ Claude : ProviderSnapshot
ICodexQuotaSource ───┤        │ (Core §34 derivation)     │   ├ Codex  : ProviderSnapshot
TimeProvider, ITimerScheduler ┘        └ CodexSchedule (pure pull policy)   └ refresh/next-action info
```

## Claude — passive

* **No refresh operation exists.** Nothing here can make Claude report; `RereadClaudeState()` only rereads the local file.
* Source: the bridge's fixed file (`<token-derived LocalAppData>\QuotaDuck\bridge\claude-v1.json`; same derivation as the bridge,
  asserted equal by a test; not redirectable by any environment variable). Read-only, bounded to 4097 bytes, `FileShare.ReadWrite|Delete`.
* Watching: one `FileSystemWatcher` on the bridge directory filtered to the exact file name (the bridge's temp files are ignored; the
  atomic replace arrives as Renamed/Changed). No directory scanning. Bursts coalesce into one reread (150 ms debounce).
* Missed events: a safety reread every 5 minutes (which also re-verifies the watcher). Overflow/error or a vanished directory detaches the
  watcher and forces a reread; while detached it re-attaches every 30 s (one `Directory.Exists`).
* States: file absent/empty → `WaitingForTerminalClaudeCode` (never 0%; a previously shown number is cleared because the source of truth is
  gone); corrupt/unreadable/intermediate → **last good state kept**, issue `ClaudeStateUnreadable`, retry after 1, 2, 4… s; newer schema →
  `ClaudeStateUnsupportedVersion`, last good kept; start after the window elapsed → Core says `Elapsed` (no number).
* **A reread is not an observation.** Core derives freshness from the stored `ChangedAt`; rereading an unchanged file never makes it
  fresher. Fresh→Quiet→Sleeping and Elapsed happen at the exact Core transition instants because the coordinator arms its timer at
  `NextTransitionAt` — no file event is needed.

## Codex — pull (pure policy in `CodexSchedule`)

| Situation | Behaviour |
|---|---|
| UI visible, never read | read immediately (`InitialVisible`) |
| UI visible | next read 10 min after the previous read **completed** (`VisiblePoll`) |
| UI hidden | **no polling** |
| Known reset (`resetsAt` of the last successful read) | one read at `resetsAt + 30 s` (`ResetRead`), also while hidden; satisfied once any read has *started* at/after that moment; a failed hidden reset read is not retried until the UI is visible |
| Any trigger | at least 60 s between read **starts** |
| Failure (UI visible) | retry after 60, 120, 240, 480, 900 s (cap) from the failed read's completion; success resets the streak |
| Manual refresh | allowed outside the 60 s cooldown (counted from the last read start, successful or not); inside it nothing starts and `NextAllowedAt` is returned; during backoff it is allowed (cooldown permitting) |
| Resume / unlock / wall-clock change | `NotifyEnvironmentChanged()`: Claude reread + watcher re-verify, everything re-derived from the clock; overdue Codex work runs **once** |
| Clock moved backwards | stored read times are rebased to "now" so nothing is postponed; Core already treats future evidence as a clock anomaly |

**Single flight:** one read at a time. Manual refresh while a read is in flight *joins* it (`JoinedInFlight`); timer/visibility/resume
triggers during a read are ignored and re-evaluated at completion (so a reset that passed during a read gets its follow-up read).
Hiding/showing the UI during a read does not cancel or duplicate it. Shutdown cancels the in-flight read (the runner kills the process
tree), records **no** failure, and releases waiting callers.

**Failure semantics:** a failed read records an *attempt* only. Observations and `LastObservationAt` are untouched, so nothing becomes
fresher and nothing is hidden; Core ages the old data and reports `LastReadFailed`.

## Snapshot API (for the WPF shell)

`AppSnapshot` = `{ Version, At, UiVisible, Claude, Codex, CodexRefreshAllowed, CodexRefreshAllowedAt, CodexNextRead, ClaudeNextLocalReread }`
with `ProviderSnapshot` = `{ View (Core ProviderView: windows, rollup, reason, ages, next transition), Issue (bounded enum), Activity (Idle/Reading),
LastObservationAt }`. `Current` is the last published snapshot, `Changed` fires on content changes (including transition instants and the
instant the manual button re-enables), `Capture()` derives a snapshot *now* without publishing. `ProviderIssue` is the bounded error model:
`None, NotConfigured, WaitingForTerminalClaudeCode, NoData, ClaudeStateUnreadable, ClaudeStateUnsupportedVersion, CodexNotInstalled,
CodexNotTrusted, CodexRequestRejected, CodexProtocolUnsupported, ProviderUnavailable, TemporaryProviderFailure`. No strings, paths,
exceptions, processes or protocol content are exposed.

## Findings

* On Windows an atomic rename-over can be refused (access denied / sharing violation) while **any** handle to the target is open, even
  with `FILE_SHARE_DELETE`. The coordinator's read handle lives for microseconds, and the bridge now retries ONLY its final replace (up to
  3 retries after 5/10/20 ms = at most 35 ms; sharing violation, lock violation, or access-denied on an existing non-read-only destination).
  Everything else is permanent and not retried; on exhaustion the old state file is untouched and the temp file removed.
* `CodexRequestRejected` is *not* mapped to "not signed in": no real error code for that case has been observed (and a logout probe is out
  of scope), so it is reported honestly as a rejected request.
