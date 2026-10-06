# QuotaDuck.Core — freshness / reset state table (as implemented)

Implements Phase 1b §34. Pure functions; time is always an explicit `now` argument. Every row below is pinned by a test in `tests/QuotaDuck.Core.Tests` (see "Test map").

## 1. Window state (`WindowDeriver.Derive`) — first matching rule wins

| # | Condition | State | Number shown | Notes |
|---|---|---|---|---|
| 1 | no observation, or `UsedPercent` is NaN / ±∞ / <0 / >100 | **NoData** | none | invalid also sets `InvalidObservation` |
| 2 | `ResetsAt` known and `now >= ResetsAt` | **Elapsed** | none (`LastKnownUsedPercent` kept for the expanded "was X% at …" view only) | overrides every age rule; `ElapsedSince = now − ResetsAt`; stays Elapsed until a new epoch is merged/read |
| 3 | evidence age `< FreshWithin` | **Fresh** | used / remaining | capped to **Quiet** if `ClockSkew` or `ImplausibleReset` |
| 4 | evidence age `< QuietWithin` | **Quiet** | used / remaining | older reading, still before reset; not an error |
| 5 | otherwise | **Sleeping** | used / remaining | hours old; de-emphasised, age prominent |

Boundaries are **exclusive on the old side**: Fresh while age < bound, and the next state begins *exactly* at `evidence + bound`; Elapsed begins exactly at `ResetsAt`. That makes `NextTransitionAt` an exact instant (tested one tick before/at).

| Source | Evidence time | FreshWithin | QuietWithin |
|---|---|---|---|
| **PushReported** (Claude) | `ChangedAt` — when (percent, reset) last *changed*, or a new epoch began. **Never** "bridge ran" | 10 min | 2 h |
| **PolledRead** (Codex) | time of the last *successful read* | 20 min (= 2 × 10-min poll) | 2 h |

Displayed numbers (`DisplayedUsedPercent`, `DisplayedRemainingPercent`) are non-null **only** in Fresh / Quiet / Sleeping. NoData and Elapsed never produce a number — never 0 %, never the obsolete value. A provider-reported 0 % *is* shown (Fresh/Quiet/Sleeping) and is distinct from "unknown".

Other per-window outputs: `ResetsIn` (displayable states with a reset), `EvidenceAge`, `ReceivedAge` (Claude: "Claude Code last ran … ago", never freshness), `LimitReached` (only while displayable), `NextTransitionAt` = earliest of `evidence+FreshWithin` (Fresh) / `evidence+QuietWithin` (Quiet) / `ResetsAt`; null for NoData, Elapsed, and for Sleeping/no-reset windows.

Anomalies (flags): `ClockSkew` (evidence > now + 2 min), `ImplausibleReset` (`ResetsAt > now + Duration(or 8 d) + 10 min`), `MissingResetTime` (window can age but never expire by time), `InvalidObservation`.

## 2. Provider roll-up and reason (`ProviderDeriver.Derive`)

**Per-window state is independent and authoritative.** A window's `WindowView` is derived from that window's own observation only (§1) — identical whether it is derived alone or next to any sibling (tested over a 30 × 30 grid of sibling pairs). A Fresh 5-hour window never makes a Sleeping weekly window look Fresh; an Elapsed window stays Elapsed (no number) however fresh its sibling is; no numeric value is ever aggregated at provider level (`ProviderView` has no percent member, reflection-tested).

**Roll-up is for a single tray/status indicator only.** Rollup = `NotConfigured` if not configured; else `NoData` if no window has data; else the **best** (lowest) window state. Windows of the other provider are ignored; duplicate keys: last wins; expected-but-unreported windows appear as NoData ("not reported yet", never 0 %).

| Rollup | Claude reason | Codex reason |
|---|---|---|
| NotConfigured | NotConfigured | NotConfigured |
| NoData | **WaitingForTerminalClaudeCode** | last attempt failed ? ReadFailedNoData : CodexNotReadYet |
| Fresh | None | failed ? LastReadFailed : None |
| Quiet | None | failed ? LastReadFailed : None |
| Sleeping | ClaudeNoRecentReport | failed ? LastReadFailed : CodexReadsPaused |
| Elapsed | AwaitingNextClaudeReport | failed ? LastReadFailed : AwaitingCodexRead |

A failed Codex attempt is an overlay only: it never refreshes (or ages) the older windows. `MostConstrained` = displayable window with the highest used % (ties: earlier reset, then key order); an Elapsed 90 % window never wins. Providers are derived from their own input only (independence is tested both ways).

## 3. Claude merge (`ClaudeWindowMerger.Merge`) — one window per call

| Incoming | Result | `ChangedAt` | `ReceivedAt` | Persist? |
|---|---|---|---|---|
| window absent | NoIncoming | – | – | no |
| percent missing / not finite / outside [0,100] | Rejected* (record kept by reference) | – | – | no |
| reset ≤ now − 2 min (Claude drops windows after reset) | RejectedResetInPast | – | – | no |
| reset > now + length(5 h / 7 d) + 10 min | RejectedImplausibleReset | – | – | no |
| nothing stored | FirstObservation | now | now | yes |
| reset later by > 2 min | **NewEpoch** (replaces; counts as new info even at same %) | now | now | yes |
| reset earlier by > 2 min | OlderEpochIgnored (stale session) | keep | now | coalesced |
| same epoch, same % | **Unchanged** | **keep** | now | coalesced (≥ 60 s), or immediately if reset learned / lower-streak cleared |
| same epoch, higher % | Advanced | now | now | yes |
| same epoch, lower % | LowerIgnored (streak++; stored value kept) | keep | now | yes (streak changed) |
| same lower % reported 3× in a row | AdjustedDown | now | now | yes |

**Lower-reading rule (`FreshnessPolicy.LowerAcceptCount`, default `DefaultLowerAcceptCount = 3`):** a conservative **v1 heuristic**. Anthropic's downward usage corrections inside a window are not yet characterised (UNKNOWN), so a same-epoch lower value is ignored (first and second identical report), accepted on the third identical consecutive report, restarted by any different lower value or any equal/higher report, and **bypassed entirely by a later `resetsAt` epoch** (a new window may legitimately start lower).

**Stored timestamps never go backwards.** If the system clock steps back (or another process's reading interleaves), new records are stamped with the previous `ReceivedAt` instead, so `ChangedAt ≤ ReceivedAt` always holds and the strict state decoder never rejects (and silently loses) a valid record. Validation (reset in past / implausible) still uses the true `now`.

Unknown incoming reset ⇒ assume stored epoch; unknown stored reset ⇒ adopt incoming. The persisted record is exactly `{UsedPercent, ResetsAt, ChangedAt, ReceivedAt, LowerCandidate, LowerStreak}` — no identity or payload field can be stored (reflection-tested).

## 4. Codex read (`CodexObservations.FromRead`)

One successful read replaces the whole Codex snapshot (absent windows vanish). Keys are `"{limitId}:{primary|secondary}"`; limit ids must match `[A-Za-z0-9][A-Za-z0-9._-]{0,47}` or the window is **skipped and counted**; invalid percentages skipped and counted; duplicates: last wins. Label: exact duration match (300 → 5-hour, 1 440 → Daily, 10 080 → Weekly, 43 200 → Monthly, 525 600 → Annual) else positional Primary/Secondary with `LabelOrigin.Positional` (no invented meaning).

## 5. Test map (what pins what)
`WindowStateTableTests` — rules 1–5, every boundary, anomalies, display rules · `InvariantTests` — 2 × 6 × 13 × 14 = 2 184-case grid, 6-hour time sweep (state only moves forward; transitions land exactly on the predicted instant), 4 000-step seeded fuzz of the merge · `ProviderDerivationTests` — every roll-up/reason cell, independence, expected windows, constrained-window choice · `ClaudeWindowMergerTests` — every decision, stale-session scenarios, "bridge keeps running but data never changes ⇒ Fresh→Quiet→Sleeping", reset → Elapsed → new epoch · `CodexObservationTests` · `LabelAndKeyTests` · `PolicyTests` · `ArchitectureGuardTests` (BCL-only references, no network/registry/process/file capability, explicit `now`, no raw-payload types in the public surface).
