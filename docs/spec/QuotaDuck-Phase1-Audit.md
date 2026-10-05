# QuotaDuck — Design Specification (Phase 1 / 1b research record)

Date: 2026-10-04 · Status: research record that the implementation follows. **Phase 1b (verification pass) is at §27–§38 and supersedes anything above it that conflicts.** Items corrected by Phase 1b are marked **[1b]**. Later measured results are in `docs/spikes/STEP0-RESULTS.md`, which overrides this document where they differ.

Verdict scale: **SAFE** (safe / low concern) · **MITIGATE** (acceptable with mitigation) · **INVESTIGATE** (needs investigation) · **REJECT** (do not adopt). Anything not proven is marked **UNKNOWN**.

> **Independence note.** QuotaDuck is an independent implementation. Before designing it, an existing public project in the same space (TokenFish, `pixelsncodes/tokenfish`, which carries no licence) was studied as a **reference only**. **No code, tests, assets, documentation text, names or constants were copied** from it, and QuotaDuck's structure, naming, art, tests and constants are its own. A no-copy comparison of QuotaDuck against that project was run on its commit `71ab591947ebabf6b7e03e0344ceb084665477f7`; the method and result are summarised in §28. The design below is specified from the providers' own contracts (official documentation, public source and direct observation), not from that project.

---

## 0. Scope and method

This document records how the quota sources work, what QuotaDuck reads and writes, the threat model, and the architecture decisions that follow. It was produced read-only: provider documentation and public source were read; local probes were limited to a quota-only Codex handshake and a status-line probe, each run with explicit owner consent (§27). Nothing was installed and no provider setting was changed.

Where a statement is an expectation rather than a measurement it is marked **UNKNOWN** or "estimate". Measured results are in `docs/spikes/STEP0-RESULTS.md`.

---

## 1. Executive recommendation

**Yes, QuotaDuck is feasible as a tiny, local, effectively zero-token monitor, with one honest asymmetry between providers:**

| | Claude | ChatGPT / Codex |
|---|---|---|
| Mechanism | Claude Code **status-line** hook (documented, official) delivers `rate_limits.five_hour` / `seven_day` on stdin | Local `codex app-server` over stdio: `account/rateLimits/read` (official OpenAI source/schema, but **not in the public app-server docs page that could be fetched**) |
| Model cost | Zero | Zero (backend usage lookup, no inference) |
| Credentials needed by QuotaDuck | None | None (the Codex child process uses its own login) |
| Freshness model | **Push only, tied to your Claude Code activity.** A Refresh button cannot create a new reading. | **Pull.** A refresh genuinely re-queries the account-level usage. |
| Break risk | Low–moderate (documented contract) | Moderate (schema is versioned in source but evolving fast; docs gap) |

**Recommended path:** independent build, **C# / .NET 10 + WPF**, a tiny separate bridge exe for Claude, an on-demand spawned `codex app-server` for OpenAI, one duck per pond, no network code of its own, no updater, zero third-party NuGet packages.

Two design points matter for honesty (§9): (1) a Claude status-line *invocation* is not a fresh *observation*; (2) a window whose `resets_at` has passed must never be shown as its old number nor as "0%".

---

## 2. Design risks that shape the process and parsing code

The reference study (see the independence note) surfaced a short list of risks that any tool in this space must handle. They are stated here as QuotaDuck requirements; the labels D1–D5 are referenced elsewhere.

- **D1 — Bare executable name.** Launching `codex.exe` by bare name lets Windows' `CreateProcess` search order pick a planted file (the application directory and, depending on the runtime, other locations are searched before `PATH`). QuotaDuck resolves and validates an **absolute** path and never launches by bare name (§33; measured in Step-0 Spike 2).
- **D2 — Login-shell launch modes** (for example `bash -lc "exec codex …"`) execute a user's whole shell profile on every refresh. Out of scope for v1 (no WSL mode).
- **D3 — Option-like argument values.** A user-supplied name beginning with `-` can be parsed as an option by the launched tool. v1 passes only compile-time constant arguments, so this cannot occur.
- **D4 — Environment inheritance.** A child inherits the parent's whole environment, including unrelated keys. QuotaDuck passes an explicit minimal environment block (§33; measured in Step-0 Spike 3: `SystemRoot` only).
- **D5 — The app-server is a far larger control surface than quota.** It also exposes methods such as `command/exec`, thread/turn start (inference), and a reset-credit consume call in its backend client. QuotaDuck's method allow-list is **structural**: a client class that can only emit `initialize`, `initialized` and `account/rateLimits/read`, never a general JSON-RPC pipe.

Other lessons that became requirements: parse provider payloads with a skipping allow-list parser that returns a closed type (§16, §35 N2); stamp freshness from provider information, not from "the bridge ran" (§9, §34); never keep a window after its reset time (§34); create state files with current-user-only permissions and bounded size (§11, §15).

---

## 3. Licence and independence

- The reference project has no licence file, so its code, tests, art, text, names and constants are **not reusable**; QuotaDuck contains none of them (§28 records the comparison, §37 the conclusion). Under default copyright law, public does not mean reusable.
- Provider tool licences relevant to us: `openai/codex` is **Apache-2.0** (QuotaDuck only talks to the binary; schema knowledge from public source is fine). Claude Code is proprietary but its status-line contract is publicly documented for exactly this purpose.
- Generic ideas — stdio JSON-RPC to a local tool, a status-line bridge, temp-file plus atomic replace — are dictated by the providers' contracts or are standard practice.
- QuotaDuck itself is released under the MIT licence (see `LICENSE`; third-party notices and the artwork statement are in `NOTICE`).

---

## 4. Claude integration / data flow (from source + official docs)

```
Claude Code (running) ──stdin JSON per status-line update──▶ bridge.exe ──atomic write──▶ %LOCALAPPDATA%\QuotaDuck\bridge\claude-v1.json
                                                                         ▲
QuotaDuck app ───────────────────── reads file on refresh ───────────────┘
```

- User manually adds `statusLine: {type:"command", command:"…bridge.exe"}` to Claude Code settings (QuotaDuck never edits it).
- Claude Code (official doc): pipes one JSON payload to the command's stdin; shows its stdout. Updates are **event-driven** (debounced 300 ms), plus optional `refreshInterval`, plus a re-run when a rate-limit window reaches its `resets_at`. A newer trigger cancels an in-flight run.
- `rate_limits` is present **only for Pro/Max subscribers (or gateway spend limits), only after the first API response of the session**; each window may be independently absent; **Claude Code drops a window once its `resets_at` passes.**
- Fields: `rate_limits.five_hour.{used_percentage 0–100, resets_at epoch-s}`, `rate_limits.seven_day.{…}`; gateway-only `spend_limit`. **No per-model weekly limits, no observation timestamp.**
- QuotaDuck bridge: bounded stdin read, strict `Utf8JsonReader` skip-parser (unknown properties are skipped, not rejected), range-checks 0–100, per-window merge, mutex + temp file + atomic replace, state file ≤ 4 KB. Freshness is derived from provider information, never from the bridge run time alone (§9, §34).
- The bridge process **does receive the whole payload** (cwd, workspace dirs, `transcript_path`, model, cost, session id). It discards it, but the process boundary is where that exposure lives.

---

## 5. ChatGPT / Codex integration / data flow

```
QuotaDuck ──spawn──▶ codex app-server --stdio ──HTTPS (Codex's own auth)──▶ OpenAI backend usage endpoint
          ◀── JSON-RPC lines ◀──┘
 initialize → initialized → account/rateLimits/read → (optional) account/usage/read → dispose (close stdin, 2 s, kill tree)
```

From `openai/codex` source (`app-server-protocol/…/account.rs`, `backend-client`):
- `account/rateLimits/read` → `{ rateLimits: RateLimitSnapshot, rateLimitsByLimitId?: {id → snapshot}, ordinaryUsageAllowed?, accountId?, rateLimitResetCredits?, rateLimitUpsell? }`.
- `RateLimitSnapshot`: `limitId?`, `limitName?`, `primary?`, `secondary?` (each `{usedPercent:int, windowDurationMins?, resetsAt?}`), `credits?{hasCredits, unlimited, balance?}`, `planType?`, `rateLimitReachedType?`, `individualLimit?`, `spendControlReached?`.
- `account/rateLimits/updated` notification: sparse push, only meaningful when connected to the app-server that is actually running turns.
- `account/usage/read`: token-activity buckets (not quota).
- Backend fetch: usage endpoint under `https://chatgpt.com/backend-api/wham/usage` (ChatGPT-auth style) or `/api/codex/usage` (Codex-API style), with Codex-attached bearer + `ChatGPT-Account-Id`. **No model inference** in that client code. HTTP verb and full startup network behaviour of the app-server: **UNKNOWN** (method body not visible in the public source reviewed).
- **Doc gap:** OpenAI's public app-server docs page (as fetched/summarised) does not list these methods. They are in source and schema files, i.e. real but not a promised stable public API.
- Windows are labelled only when `windowDurationMins` is present (upstream maps 300→5h, 10080→weekly, etc.); otherwise "primary/secondary" are positional, **not semantic**.

**ChatGPT vs Codex:** this interface exposes *Codex* limit buckets (and plan/credits) for the signed-in ChatGPT account. It does **not** expose general ChatGPT chat-message caps. So the pond is **"Codex (ChatGPT plan)"**, driven by whatever buckets appear, not a fake mirror of Claude's two bars. API-key (pay-per-token) logins likely have no plan windows: UNKNOWN.

---

## 6. Lessons that shaped the design

The reference study (§2, §28) is condensed here into the design rules it produced; none of these is specific to any one project.

| # | Pitfall | QuotaDuck rule |
|---|---|---|
| 1 | "No network" claims are easy to over-state when a spawned child talks to the provider | State it precisely: QuotaDuck's own process opens no connections; the Codex child it spawns does (§32) |
| 2 | Documented validation behaviour can diverge from what the parser really does (rejecting versus skipping unknown fields) | Docs say what the parser does: skip unknown fields, keep only allow-listed ones |
| 3 | "Current-user-only permissions" asserted without ACL code | Explicit current-user-only DACL on the state directory (§11) |
| 4 | Boilerplate describing components that do not exist (databases) | Document only what exists |
| 5 | Write-through on every status-line run | Skip the write when nothing changed; coalesce `receivedAt` (§34) |
| 6 | Freshness stamped from the bridge run time: a status-line re-run can re-emit **old** `rate_limits`, so a run is not a new provider reading | Separate `receivedAt` / `changedAt` / `resetsAt` (§34) |
| 7 | A window kept after the host stopped sending it can linger past its reset | Expire windows at `resetsAt`; never show an old number or 0 % (§34) |
| 8 | Parsing a whole response into a document before filtering retains account data in memory | Skip-parse straight into a closed type (§16, §35 N2) |
| 9 | Unused packaged-app capabilities and preview build tooling | Unpackaged app, no unused capabilities, BCL-only runtime (§23) |

---

## 7. Claude quota / token-cost analysis

| Question | Answer |
|---|---|
| Passive monitoring → inference? | **No.** Status-line command is a local process; data piggybacks on responses the user's own work already triggered |
| Tokens / quota consumed by monitoring? | **None** from QuotaDuck. (Status-line output is UI text; nothing indicates it is sent to the model — UNKNOWN only in the sense of not testing it) |
| Manual refresh consumes? | No — and it **cannot** obtain anything new; it can only reread the file |
| Background refresh? | File watching only; zero provider activity |
| Dummy prompts ever needed? | **Never. Forbidden by design.** (`claude -p "ping"` or equivalent would burn quota) |
| Quota without a model call? | Yes, via status-line `rate_limits` — only while Claude Code is making API calls |
| Provider API cost? | None. Rejected alternative: undocumented OAuth usage endpoint with the token from Claude's credentials file — needs credential access, undocumented, and violates the project's no-credential-access rule → **REJECT** |
| Verdict | **SAFE** |

Caveat: Claude plan quota is, per Anthropic's plan docs (not re-verified here), shared across claude.ai web/desktop and Claude Code. Usage in the web app will **not** appear until Claude Code next reports. This is a primary cause of staleness and must be explained in the UI.

## 8. ChatGPT / Codex quota / token-cost analysis

| Question | Answer |
|---|---|
| Inference? | No (usage lookup only) |
| Tokens consumed? | None |
| Rate-limit allowance consumed? | None known. Whether the usage endpoint is itself throttled: UNKNOWN (so keep intervals long) |
| Manual refresh consumes? | Triggers a real backend lookup; no model tokens |
| Background refresh? | Same; keep it infrequent (§11) |
| Dummy prompts? | **Never.** `thread/start`/`turn/start` are structurally unreachable (§2 D5) |
| Monetary cost | None on a ChatGPT plan. API-key billing: UNKNOWN/not applicable |
| Verdict | **SAFE**, subject to API stability (MITIGATE) |

---

## 9. Freshness / update semantics

| | Claude | Codex |
|---|---|---|
| What yields a fresh observation | A Claude Code API response whose status-line run includes `rate_limits` | A successful `account/rateLimits/read` |
| Refresh button | Cannot force. Rereads file; UI says so ("no new data since 12 min ago — Claude Code reports when it runs") | **Can** force (spawn → read → exit) |
| Provider closed | File persists; age grows; **sleeping duck** | Independent of Codex UI; works if `codex` + login exist; fails clearly if not signed in |
| Stale causes | Idle Claude Code; usage via claude.ai web; window rolled over | Spawn failure, signed out, offline, endpoint change |
| Age source | `lastSeenAt` (last bridge run) **and** `valueChangedAt` (last time numbers differed) | `observedAt` = successful read time |
| "Real time" realistically | Updates only as often as you use Claude Code (seconds–minutes during work, none when idle). Countdown to `resets_at` is local arithmetic | Poll-driven: as fresh as the last poll (minutes) |

**Rules (never fake freshness):**
1. Show two ages for Claude: *"Claude Code last reported 3 min ago"* and, if different, *"numbers last changed 41 min ago."* Only a **changed** value or a status-line run that actually carried `rate_limits` counts as "seen"; a run with absent `rate_limits` updates nothing.
2. UI animation/countdown ticks are not observations; they never touch `observedAt`.
3. **Window rolled over:** if `resetsAt` < now, state = `Elapsed`: show "Reset at 14:00 — waiting for first reading", dim refilled pond with hatching; do **not** show the old % and do **not** show 0%.
4. Failed refresh never refreshes the age; it adds an error chip.
5. Each provider's state is computed independently.

Freshness thresholds (tunable): Codex fresh ≤ 2× poll interval, stale beyond; Claude "live" ≤ 5 min since last seen, "quiet" 5–30 min (age shown, no alarm), "sleeping" > 30 min or any `Elapsed` window.

---

## 10. What QuotaDuck needs to **read**

1. Claude bridge: stdin payload (**bridge process only**, streamed, only 4 numeric fields kept).
2. Its own state file(s) and settings.
3. `codex app-server` stdout lines (≤ 1 MiB line cap; only `id`-matched responses; notifications ignored).
4. OS UI signals: animations enabled, theme, high contrast, DPI, monitor work area, session lock/suspend.
5. Never: `~/.codex/*`, `~/.claude/*`, transcripts, history, cookies, browser storage, repos, directory scans, process lists.

## 11. What QuotaDuck needs to **write**

| File | Content | Needed? |
|---|---|---|
| `%LOCALAPPDATA%\QuotaDuck\settings.json` | theme, widget corner/monitor anchor, always-on-top, compact/expanded, startup pref (v1: none), poll interval, Codex runtime mode + validated path | Yes |
| `%LOCALAPPDATA%\QuotaDuck\bridge\claude-v1.json` | Per window: usedPercent, resetsAtUtc, lastSeenAtUtc, valueChangedAtUtc. ≤ 4 KB. **Required** because the bridge runs while the app may not | Yes |
| Codex cache | — | **No** (re-query on launch takes seconds) |
| Logs | Off by default; if enabled: coarse enum states only, no args/paths/payloads | Optional |
| Registry / Run key / scheduled tasks / services | — | **None in v1** |
Explicit ACL: current-user-only DACL on the directory; write temp + replace; skip the write when nothing changed (cuts disk churn).

## 12. Processes / services / interfaces touched

- `claude` → (user-configured) → `quotaduck-bridge.exe` (spawned by Claude Code, not by us).
- `codex app-server --stdio` (spawned by QuotaDuck, short-lived), optionally via `wsl.exe --exec …`.
- Windows: Shell_NotifyIcon (tray), UISettings/SystemParameters, DPI APIs. No services, no COM servers, no named pipes in v1.

## 13. Network endpoints

- **QuotaDuck itself: none.** Zero `HttpClient`, sockets, listeners (enforceable by a CI grep and by a Windows Firewall check in Phase 2).
- Indirect, outside our control: the Codex child contacts OpenAI (usage endpoint `chatgpt.com/backend-api/wham/usage` or `/api/codex/usage`; plus whatever app-server does at startup: token refresh, its own telemetry per the user's Codex config — **UNKNOWN**, to be observed once in Phase 2 with user consent). Claude Code's own traffic is unchanged.

## 14. Credentials / authentication exposure

| Item | Exposure |
|---|---|
| Claude OAuth/credentials file | Never touched |
| ChatGPT `auth.json` / tokens | Never touched by us; Codex child reads it. **Residual risk:** a compromised QuotaDuck controls a process that holds those tokens → hence the structural method allowlist, absolute-path validation, minimal env |
| API keys | Not collected |
| Cookies / browser storage | Not touched |
| Status-line payload | Contains paths/session identity; handled only in the bridge, never persisted/logged |
| Verdict | **MITIGATE** (Codex child trust boundary), otherwise SAFE |

---

## 15. Threat / privacy model

| Risk | Mitigation |
|---|---|
| Compromised local process (same user) | Can't be fully prevented; keep nothing secret to steal; state files hold only percentages; DACL current-user-only |
| Malicious data into the bridge / state file | Bounded stdin (≤ 256 KB), strict allowlist parser, range checks (0–100, epoch within ±2 y), state file ≤ 4 KB, schema version, fall back to "unavailable" |
| Malformed provider payload | Same parser discipline; per-line cap, per-request deadline, discard session on any error |
| Command injection | argv arrays only, no `cmd /c`, no string-built commands; validated distro name; absolute `codex` path chosen by user/PATH lookup at setup and re-validated (exists, file, not in app dir/CWD, Authenticode info shown but not required) |
| Path injection / traversal | Only fixed filenames under one directory; no provider-supplied paths ever used |
| Accidental credential logging | No logging of stdout/stderr/args; stderr drained and discarded; unit test that fixtures with fake secrets never reach any sink |
| Leaking provider auth state | Not read; env block minimal |
| Exposing prompts/transcripts/repos | Never parsed; streaming parser skips; bridge never echoes input |
| Public network exposure | No listener exists |
| Stale shown as current | §9 rules; ages always visible; elapsed-window state; per-provider isolation |
| Dependency compromise | BCL only, no NuGet runtime deps; pinned SDK via `global.json`; Actions pinned by SHA; reproducible build notes |
| Unsafe updater | None in v1 |
| Privilege escalation | `asInvoker`, no admin, no services; bridge path in user-writable dir means same-user tampering is possible → document, show SHA-256, offer "verify bridge" button |
| Persistence/autostart | None in v1; later opt-in `HKCU\…\Run` only, user-toggled, removable |
| Bridge replaced by attacker (runs on every Claude update) | Same-user write = already game over for that user; mitigate by signing later and keeping bridge in a read-only-for-others dir |

---

## 16. Recommended clean-room architecture

```
┌───────────── QuotaDuck.App (WPF, per-monitor DPI v2) ─────────────┐
│ TrayHost ─ Flyout ─ Widget(optional) ─ Details                    │
│        ▲ ViewModel (immutable ProviderState snapshots)            │
│ FreshnessEngine (pure fn: observation + now → state)  ◀── clock   │
│ ClaudeSource: FileSystemWatcher + debounce + 60 s safety read     │
│ CodexSource : on-demand runner (spawn→read→exit), backoff         │
└───────────────────────────────────────────────────────────────────┘
QuotaDuck.Bridge (Native AOT console, ≈1–3 MB, start <30 ms): stdin → 4 fields → atomic file
QuotaDuck.Core (models, parsers, freshness) — pure, 100 % unit-testable with synthetic fixtures
```

Key decisions:
- **Bridge is Native AOT, not a self-contained .NET exe** — it runs on *every* Claude status-line update, so startup cost and size matter.
- **Codex: spawn on demand, don't hold a resident child.** Default poll 5 min while widget visible, 15 min tray-only, 60 s minimum between manual refreshes, paused on lock/suspend/offline; exponential backoff on failure. No long-lived Rust process idling.
- **Claude: no polling at all.** Watch a directory (not the file), debounce 250 ms, plus a slow safety read.
- **Parsers return a closed type** (4 numbers for Claude; for Codex only `limitId`, `limitName`, windows, `planType` enum, `rateLimitReachedType` enum) — `accountId`, credits balance, upsell blob are dropped at the parser and never held.
- **Elapsed-window logic** and **two-age Claude freshness** (§9).
- Single instance via a named mutex; no IPC needed in v1 (settings reload on restart).

**Verdict: MITIGATE** (feasible; risks in §25).

## 17. Recommended technology stack

**C# / .NET 10 + WPF**, Native AOT bridge, xUnit tests, zero runtime NuGet packages.

| Option | Verdict | Why |
|---|---|---|
| **WPF** | ✅ choose | In-box tray via `WinForms NotifyIcon` or ~100 lines of Shell_NotifyIcon; `RenderOptions.BitmapScalingMode=NearestNeighbor` gives true pixel art; PerMonitorV2; `SystemParameters.ClientAreaAnimation` for reduced motion; transparent/topmost borderless widget is trivial; no Windows App SDK; mature |
| WinUI 3 / WinAppSDK | ✗ | needs custom tray plumbing and bundles the Windows App SDK runtime; heavier, more moving parts, preview tooling; no gain for a 96 px widget |
| Avalonia | ✗ | Cross-platform runtime you don't need; larger |
| Electron | ✗ | ~150 MB+, multi-process |
| Tauri | ✗ (close second) | Small exe but WebView2 spawns several processes; more RAM than WPF at idle for this job |
| Raw Win32/Direct2D (C++/Rust) | ✗ for v1 | Smallest (~2 MB, <10 MB RAM) but highest build cost; consider only if idle RAM matters later |

Size/RAM figures here are typical expectations; measured numbers are in `docs/spikes/STEP0-RESULTS.md`. Distribution for personal use: framework-dependent (needs .NET 10 Desktop Runtime) → few MB; or self-contained ≈ tens of MB if you prefer no runtime install.

## 18. Minimal internal data model

```
ProviderId        = Claude | Codex
ProviderState
  id, connection: NotConfigured | Waiting | Active | Error(reason: enum)
  freshness (derived): Fresh | Quiet | Stale | Sleeping | Unavailable
  lastSeenAt?  (Claude: last bridge run carrying rate_limits / Codex: last successful read)
  windows: QuotaWindow[]
QuotaWindow
  key            "claude.five_hour" | "claude.seven_day" | "codex:{limitId}:primary|secondary"
  label          provider label if given; else derived from durationMins (300→"5-hour", 10080→"Weekly"); else "Primary"/"Secondary"
  usedPercent    decimal 0–100 (Codex int)    remainingPercent = 100 − used  (derived)
  resetsAt?      UTC
  durationMins?  int
  valueChangedAt UTC
  rolledOver     derived: resetsAt < now
Codex-only extras (optional): planType (enum), rateLimitReached (enum)
```
Not stored: account ids, emails, credits balance, workspace paths, raw JSON, history.

---

## 19. Pixel-pond visual / interaction proposal

- **Canvas:** one pond = 64×40 logical pixels, drawn at integer scale (2×/3×/4× by DPI), nearest-neighbour, 8-colour limited palette per theme.
- **Water = remaining.** Water line steps in discrete rows (≈ every 5 %); below ~25 % reeds/stones emerge on the shore, below ~8 % the duck stands in a puddle. **Always** printed: big `38% left`, small `62% used`, window name, `resets in 2h 10m`, and age.
- **Refill:** on rollover, 6-frame rain/refill (≤ 1.2 s) once; static refilled frame if reduced motion.
- **Stale/sleeping:** pond desaturated, duck asleep with `z`; text "Last report 41 min ago". Colour is never the only channel: pattern (hatching = elapsed/unknown), icons, text.
- **Idle animation:** 2-frame ripple at ≤ 4 fps only while visible; zero timers when hidden/minimised/reduced-motion.
- Original palette (blue-green water, sand, reed green, warm yellow duck) — no fish, pellets, rails or mouths.

## 20. One duck or two?

**Recommend Option B: one duck per pond.** A single roaming duck (A) can only be asleep or awake once, which collides with the rule that one provider's staleness must not affect the other; it also makes you wait for a duck to find out a number. Per-pond ducks give instant, independent state: awake / sleeping / puddle / error ("?" bubble). Cost: slightly more art (≈6 duck frames, reused).

## 21. Tray / widget behaviour

| Surface | Content |
|---|---|
| Tray icon | Static duck + small state pip (none/sleep/alert). No per-second redraw |
| Tray tooltip (≤127 chars) | `Claude 5h 38% left · seen 3m · Codex 62% left · 1m` |
| Flyout (click) | Two ponds, % left/used, windows, countdown, ages, Refresh (with honest per-provider result text), Details, Settings |
| Widget (optional) | Two mini-ponds + number + age, corner-snapped, always-on-top off by default, click-through off |
| Compact vs Expanded | Compact: first/most-constrained window per provider. Expanded: all windows, plan type, last error code, raw ages |
| States distinguished | Current (green age), Last-observed (age in text), Reset countdown, Stale (sleep), Unavailable/NotConfigured (dashed pond + setup hint), Error (reason text) |

Keyboard: tray menu items, flyout fully tabbable, Esc closes, widget focusable with arrows to move corners, accessible names like "Claude five-hour window, 38 percent remaining, resets in 2 hours 10 minutes, last reported 3 minutes ago".

## 22. Accessibility

Reduced motion via `SystemParameters.ClientAreaAnimation` (+ live change notification) → all animation off, state shown by static frames. High contrast mode: drop pixel palette for system colours and heavy outlines. Per-monitor DPI with integer-scaled pixel art (no blur). Minimum 12 pt UI text, 4.5:1 contrast. Patterns/icons plus text for every state. UI Automation names/values on ponds. Screen-reader-friendly tooltip. Stale/error fully understandable with animation off.

## 23. Dependency strategy

Runtime: .NET BCL only (System.Text.Json, WPF). No WinAppSDK, no Newtonsoft, no logging frameworks, no telemetry SDKs. Dev: xUnit + coverlet only. Not needed: the Windows App SDK, `Microsoft.Windows.SDK.BuildTools`, MSIX tooling, ReadyToRun/trim configs. **Verdict: SAFE** if the zero-package rule holds.

## 24. Updater strategy

**None in v1.** Manual GitHub-releases download, SHA-256 published, optional code-signing later. "Check for updates" is not implemented (no network code). Revisit only if you want winget/MSIX distribution; avoid a custom updater. **Verdict: SAFE.**

## 25. Main technical risks and unknowns

1. Codex `account/rateLimits/read` is not in public app-server docs → may change; mitigate with schema-tolerant parser + clear "unsupported Codex version" state. **INVESTIGATE**
2. Exact Codex startup network/side effects, HTTP verb, API-key behaviour: UNKNOWN.
3. Claude status-line re-emission of old values (freshness) — behaviour of `rate_limits` between API responses UNKNOWN; design assumes the worst.
4. Single `statusLine` slot: QuotaDuck's bridge would **replace** any status line you already use. No chaining in v1 (chaining = running arbitrary commands). **INVESTIGATE** (resolved by the read-only setup check in §30).
5. Multiple Claude accounts / concurrent sessions share one state file (no identity stored by design).
6. Claude quota shared with web/desktop, so some staleness is unavoidable.
7. .NET `Process` search order for bare `codex.exe` (D1) untested.
8. Memory/size numbers are estimates.

## 26. Bounded Phase 2 plan (needs your go-ahead; each step has an exit gate)

0. *(Done)* Reference study and no-copy comparison (§28).
1. **Contract probes, with your explicit consent:** (a) run `codex --version` and one `account/rateLimits/read` against a throw-away harness, capture a *sanitised* schema sample, observe with a network monitor what the child contacts; (b) read-only look at whether you already have a `statusLine`. Gate: written contract + fixtures.
2. `QuotaDuck.Core` + tests (parsers, freshness engine, elapsed-window logic) from synthetic fixtures. Gate: all state-table cases pass.
3. Bridge (Native AOT) + atomic store + watcher. Gate: fuzz/oversize/malformed tests; size and start-time measured.
4. Codex runner: absolute-path resolution, method-allowlisted client, minimal env, timeouts, backoff. Gate: cannot emit any method but the allowlist (test), no leaks in logs (test).
5. WPF tray + flyout with placeholder rectangles, fake data driver. Gate: tooltips, keyboard, DPI, reduced-motion.
6. Pixel art + animation. Gate: static-frame parity with animations off.
7. Widget + settings + persistence. Gate: persistence audit lists only §11 files.
8. Hardening: firewall check (no sockets), CI grep denylist (`HttpClient`, `Socket`, `Registry`, `Process.Start` outside runner), SHA-pinned Actions, SBOM, manual release. Gate: security checklist signed off by you.

Stop conditions: any need for credentials/cookies, any network code, or any model call in a refresh path ends the plan and returns to you.

---

### Area verdict summary

| Area | Verdict |
|---|---|
| Reusing code or assets of the unlicensed reference project | **REJECT** (none used; §28) |
| Claude via status-line | SAFE (freshness caveats) |
| Codex via app-server | MITIGATE / INVESTIGATE (undocumented-in-docs, child trust) |
| Undocumented Claude/Anthropic usage API with token | **REJECT** |
| Zero-token requirement | SAFE |
| Network footprint of QuotaDuck | SAFE |
| Persistence design | SAFE |
| Updater (none) | SAFE |
| Stack: .NET 10 + WPF + AOT bridge | SAFE |
| Pixel pond / one duck per pond | SAFE |

---
---

# PHASE 1b — Read-only verification pass (2026-10-04)

## 27. Checks performed

| # | Check | Result |
|---|---|---|
| 1 | Read-only study of the public reference project, followed by a no-copy comparison | §28 |
| 2 | Licence and notice review of the reference project | §29 |
| 3 | Existence-only check for `statusLine` in the four Claude settings locations (no other values read or printed) | §30 |
| 4 | Codex binary location, signature, `--version`, `app-server --help`; `~/.codex` metadata snapshot (names and times only) before and after | §31 |
| 5 | `account/rateLimits/read` probe with passive socket and process observation | §31–§32 |
| 6 | Bare-name executable search-order experiment (harmless copies of a system executable in scratch directories) | §33 |
| 7 | Official Claude Code status-line documentation re-read for freshness and reset semantics | §34 |

**Lesson from the probe.** Windows PowerShell 5.1 prepends a UTF-8 byte-order mark to a child's stdin on a UTF-8 console. Codex's app-server logs a deserialisation error for that first line, skips it, and never answers — the symptom is a silent hang. The first harness runs failed this way; a raw-bytes launcher succeeded. This is why QuotaDuck writes child stdin as UTF-8 **without** BOM, LF-terminated, with a handshake timeout (§35 N1). No prompt, turn or model call was ever sent; Codex's own log and queue databases grew slightly (shared with the desktop app) and nothing else of its state changed.

## 28. Reference study (TokenFish) and no-copy comparison

The reference project (`pixelsncodes/tokenfish`) was read as a study of what a tool in this space must handle (§2). Independence was then **checked**, not assumed: a read-only comparison corpus was fetched (never built, run, or modified; hooks and symlinks disabled) and QuotaDuck's whole tree was compared against it.

- **Commit compared:** `71ab591947ebabf6b7e03e0344ceb084665477f7` (273 files).
- **Methods:** token 8-gram containment (comments stripped, case-folded); identifier-abstracted 14-gram containment (catches renamed copies); identical normalised code lines ≥ 35 characters; 8-word runs in prose; identical string literals ≥ 12 characters; shared numeric and colour constants; shared identifiers; shared file and folder names; byte-identical files; SHA-256 of image assets; and a head-to-head comparison of each key QuotaDuck file against its closest counterpart, including the longest identical token run.
- **Result:** no byte-identical files and no identical image assets. Production C# source overlap was 1.73 % (weighted, 58 files); the highest production C# file was 7 % (Win32 declarations). The closest counterpart pairs overlapped 0.0–2.4 % with a longest identical run of 7–20 tokens. Shared lines, strings, identifiers and constants were **generic, tool-generated or dictated by public contracts**: XML namespace URIs and the standard Windows manifest elements, solution-folder GUIDs generated by the .NET CLI itself (reproduced), standard test-package versions and attributes, P/Invoke signatures, process-redirection options, and the provider JSON field names (`five_hour`, `used_percentage`, `rateLimitsByLimitId`, `windowDurationMins`, …). **No overlap that looks like copied expression was found.**
- The comparison corpus was deleted afterwards. This section and the commit hash above are the record.

## 29. Licence follow-up

The reference project has no licence file and no explicit grant of rights exists in any location that could be inspected, so it remains **reference-only: DO NOT ADOPT** any of its code, tests, art, text, names or constants. If its owner later publishes a licence, that would not change the plan: QuotaDuck is independent either way.

## 30. Existing Claude status line

- Claude Code reads a single `statusLine` command from user, project, local or managed settings. At research time none was configured on the test machine, so no conflict existed; the bridge would be the only status-line command.
- Official documentation (re-read): the status line "runs locally and does not consume API tokens" (confirms §7).
- **Constraints that matter for coexistence:** (1) `statusLine` is a *single* command slot; (2) it can be set in **project** settings as well as user settings, so a repo-level `statusLine` silently shadows a user-level QuotaDuck bridge in that repo; (3) the documentation is silent on whether the **Claude desktop app and IDE surfaces** execute it — resolved by Step-0 Spike 1 (terminal works; VS Code panel and Desktop Code tab do not).
- **Coexistence design (no blind replacement):**
  1. *Default (nothing configured):* the bridge is the status line and prints a short line.
  2. *Existing status line:* **"tap" mode**: the user adds one line to their *own* script (`… | quotaduck-statusline --tap`), the bridge reads the same JSON, prints **nothing**, and exits 0. The user's output is untouched, QuotaDuck executes no foreign command, and it writes no Claude configuration.
  3. *Optional wrapper (not v1):* a wrapper that spawns the user's original command would let QuotaDuck run arbitrary stored commands → rejected for v1.
  4. *Detection without editing:* QuotaDuck performs one bounded, read-only check — existence of a top-level `statusLine` key in the user-level settings file, command text compared in place and never kept — so it can show the right setup instructions.
  5. Removal = delete the key / the tap line; QuotaDuck stores nothing in Claude configuration.

## 31. Codex probe — result

**Environment facts.** Codex on Windows is the **MSIX desktop app** (package family `OpenAI.Codex_2p2nqsd0c76g0`); the usable binary is `…\WindowsApps\OpenAI.Codex_<version>_x64__2p2nqsd0c76g0\app\resources\codex.exe`, **Authenticode valid, signer `OpenAI OpCo, LLC`**. **There is no `codex.exe` on PATH** (only aliases such as `codex-chrome-native-host.exe`). The desktop app was running during the probe; sockets were attributed only to the process tree the probe spawned.

**Protocol (successful run):** `initialize` answered in about 0.13 s (result keys: `userAgent`, `codexHome`, `platformFamily`, `platformOs` — values never recorded). Server notifications unprompted: `remoteControl/status/changed` and `account/updated`. `account/rateLimits/read` answered in about 0.62 s. Stderr empty. The child exited with code 0 within about 50 ms of stdin close.

**`account/rateLimits/read` — fields observed (no account values recorded):**

| Field | Presence / shape |
|---|---|
| `rateLimits` (legacy single bucket) **and** `rateLimitsByLimitId.codex` | identical content; `limitId = "codex"`, `limitName` null, `normalModelSlug` null |
| `primary` | `usedPercent` (0–100), `windowDurationMins 300`, `resetsAt` (epoch seconds) |
| `secondary` | `usedPercent`, `windowDurationMins 10080`, `resetsAt` |
| `rateLimitReachedType` | string when a limit is reached, otherwise null |
| top-level `ordinaryUsageAllowed` | boolean |
| `credits` | `hasCredits`, `unlimited`, `balance` |
| Present but irrelevant or sensitive — **QuotaDuck must discard**: | `accountId`, `planType`, `spendControlReached`, `individualLimit`, `rateLimitResetCredits{…}`, `rateLimitUpsell{…}` |

**Fresh data?** Sending the request opened new TCP connections within 0.06 s and the response arrived at about 0.62 s, i.e. a live server-side lookup, not a local cache read. (Inferred from network behaviour; a single read cannot show a changing value.)

## 32. Observed Codex network behaviour (passive)

- **Only process-tree members observed:** `codex.exe` and its console host. No grandchildren, **no listening sockets, no loopback sockets, no UDP sockets.**
- **Single remote endpoint family:** the OpenAI/ChatGPT edge on port 443. Per-connection hostname attribution is not possible without TLS-SNI or packet capture (needs elevation; not attempted). Public source says the usage call goes to a `…/backend-api/wham/usage` (ChatGPT-auth style) or `/api/codex/usage` endpoint.
- **Startup activity unrelated to the quota request: yes.** In every run, roughly 7–12 short-lived TLS connections open within about 0.1–0.6 s of process start, before any client message; one connection stays open for the session. Likely feature-flag, account or remote-control bootstrap — purpose UNKNOWN from passive data.
- **Quota request itself:** two more connections, about 0.5 s.
- **Cost model:** each spawn is about 12–14 TLS connections to the provider edge plus about 0.8 s wall time. That is provider activity QuotaDuck cannot avoid per spawn → keep spawns infrequent.
- `app-server --help` states that analytics are disabled by default for app-server unless `--analytics-default-enabled` or the user's `config.toml` opts in. QuotaDuck must never pass that flag.
- Visibility limits: sockets polled at about 25–100 ms (sub-100 ms flows can be missed); no payload, SNI, DNS-query or firewall log.
- **Not probed by design, but discovered:** `codex app-server proxy` (attach to a *running* server's control socket), `codex app-server daemon …` (durable background install), `--listen ws://…`. QuotaDuck must use none of them (attaching to a live server would expose its thread/turn traffic; `daemon bootstrap` is persistence).

## 33. Resolving `codex.exe` safely

**Findings**
1. A bare-name `codex.exe` launch would fail on a machine like the test machine (nothing named `codex.exe` is on PATH; the real binary is inside the versioned MSIX folder). The planting risk is real in principle, but the immediate effect is "Codex not found".
2. **Measured search order.** The Phase 1b experiment (a Python launcher) found: a copy only in the parent's *current directory* was **not** found; a copy in a PATH directory was; with copies in both, the **PATH directory won**. **Step-0 Spike 2 then measured .NET itself:** for `Process.Start` with `UseShellExecute=false`, the **application directory beats PATH**, and the current directory is never searched (`docs/spikes/STEP0-RESULTS.md`). So a writable portable app folder, or any user-writable directory early in PATH, is a plant vector.
3. The MSIX install path changes with every Codex update (`…_<version>_…`), is admin/TrustedInstaller-owned (a same-user attacker cannot plant in it), and the exe runs fine when started directly from outside the package.

**Requirement — never launch by bare name. Resolve → validate → launch absolute.**
1. *Discovery:* **MSIX package lookup** pinned to the package family name `OpenAI.Codex_2p2nqsd0c76g0` via the Windows package-manager API → `InstallLocation\app\resources\codex.exe`. The family name is derived from the publisher certificate, so it is not forgeable by a look-alike. A user-picked absolute `.exe` path is a later feature and goes through the same validation; never `.cmd` or `.ps1` shims; WSL is out of v1.
2. *Validation:* absolute and canonicalised path; file exists; **Authenticode valid and signer equals the pinned subject** (`OpenAI OpCo, LLC`; pin the subject, never a thumbprint — Step-0 Spike 5) — stop if it changes; only *then* run anything.
3. *Launch:* `CreateProcess` with the **absolute path as the application name** (no search), no window, redirected stdio, UTF-8-without-BOM and LF framing, a minimal environment block (§2 D4).
4. *Persist:* nothing authoritative for MSIX — re-resolve on every launch (the path moves on update).
5. *Never* change PATH, install anything, or use `daemon`/`proxy`/`--listen`.

Verdict: bare-name launch **DO NOT ADOPT**; resolve-validate-absolute **ACCEPTABLE WITH MITIGATION**.

## 34. Corrected Claude freshness design (provider-truthful)

A freshness model that labels a window "live" because the bridge's own clock ran recently is wrong: it performs no check of the window's reset time, and the official documentation lists status-line triggers that re-run the command *without new provider data* (`refreshInterval` timer, the `resets_at` event, UI state changes). So "the bridge ran" ≠ "Claude reported something new".

**Four distinct facts, kept separate** (stored per window; `T` = UTC):

| Fact | Meaning | What it proves |
|---|---|---|
| `receivedAt` | Last time the bridge got a payload that *contained that window* | Only that Claude Code ran the status line. **Never labelled fresh.** Coalesced writes (≥ 60 s granularity) |
| `changedAt` | Last time the tuple `(usedPercent, resetsAt)` differed from what was stored, or first sighting of this `resetsAt` | New provider information arrived (≈ the provider observation time, minus a small delay) |
| `resetsAt` | Provider-supplied end of window | Deterministic expiry time |
| *Genuinely fresh* | `changedAt` within the "Recent" threshold **and** window not elapsed | The only state that may be shown as "current" |

**Displayed state per window**
- **Recent** — `changedAt` ≤ 10 min ago (tunable): normal colours, awake duck. Text: "Reported 3 min ago".
- **Quiet** — older than that but not elapsed: still drawn at the last value, age always shown ("Last change reported 41 min ago · Claude Code last ran 2 min ago"). It means *unchanged or Claude Code idle*; it is **not** called stale-by-failure.
- **Sleeping** — `changedAt` > 2 h (tunable) or no data: desaturated pond, sleeping duck, copy: "Claude Code hasn't reported recently. Usage elsewhere (claude.ai) isn't visible until it does."
- **Elapsed** — `now ≥ resetsAt`: see below.
- **No data / Not configured** — dashed pond + setup hint.

Never promote a state because the bridge ran again, the UI repainted, Refresh was clicked, or a countdown ticked.

**Expiring a window at its reset time**
1. The app schedules a one-shot timer for `min(resetsAt)` (no polling), and re-evaluates on resume-from-sleep, session unlock and system-time change.
2. At `now ≥ resetsAt` the window becomes **Elapsed**: the numeric usage is replaced by "—" and "Reset at 19:11 · waiting for Claude Code's next report"; the pond is drawn **refilled but hatched** (meaning "unknown, presumably full"), never as 0 % and never as the old value. The old reading appears only in the expanded view as "was 62 % at 13:10" for context. This mirrors Claude Code itself, which stops sending a window after its `resets_at`.
3. A payload carrying a **new `resetsAt`** starts a new epoch (counts as new information even if the percentage is the same).
4. The bridge never deletes a window when `rate_limits` omits it; expiry is the app's job, so a missing window can't be misread as 0 %.
5. Plausibility guard: reject a `resetsAt` further in the future than the window length (5 h / 7 d) plus tolerance.
6. **Multiple sessions / accounts:** within one `resetsAt` epoch the used percentage is monotonic non-decreasing in practice, so a *lower* value with the same `resetsAt` is treated as another session's cached, older payload and ignored (accepted only if it repeats — Anthropic adjusting usage downward mid-window is UNKNOWN). No account identity is stored, so a Claude account switch inside one epoch can't be detected (documented limitation).
7. File schema (v1, ≤ 4 KB): per window `{usedPercent, resetsAtUtc, changedAtUtc, receivedAtUtc}` + `schema`. Writes only when the tuple changes or `receivedAt` is ≥ 60 s old.

**Same rule applied to Codex:** its `resetsAt` also expires windows; schedule **one** extra read at `resetsAt + ~5 s` so the pond refills on real data, and keep the displayed state `Elapsed` until a successful read returns the rolled window.

## 35. Security findings from Phase 1b

| # | Finding | Mitigation |
|---|---|---|
| N1 | Codex app-server stdio **silently skips** an unparsable line (a stray BOM → no response, no error) → a naïve client just hangs | Explicit UTF-8-no-BOM encoding, LF framing, handshake timeout with a specific "no initialize response" error, and a unit test that asserts the first byte is `{` |
| N2 | The `account/rateLimits/read` payload carries **account-specific, actionable data** (`accountId`, reset-credit grants that a separate backend endpoint can *consume*, referral/upsell blobs) | Utf8JsonReader skip-parser (never materialise the whole object), emit a closed type, structural impossibility of calling any method but `initialize`/`initialized`/`account/rateLimits/read` |
| N3 | Spawning app-server causes about 12–14 TLS connections to OpenAI and writes to Codex's own log/queue databases | Infrequent spawns (≥ 10 min default), never spawn when hidden unless a reset read is due, document the side effect |
| N4 | `analytics` is opt-in for app-server, but a user `config.toml` can enable it for our child too | Never pass the analytics flag; always pass the per-invocation override `-c analytics.enabled=false` (a command-line override, not a config edit) — see Step-0 Spike 3 |
| N5 | MSIX/WindowsApps install: no CLI on PATH; path moves per update | §33 resolve-validate-absolute |
| N6 | Bare-name `codex.exe` search-order hijack (measured) | §33 |
| N7 | `daemon`, `proxy`, `--listen ws://` exist | Never used; denylist in code review |
| N8 | Project-level `statusLine` can shadow the user-level bridge; the status line does not run in every surface | Setup guide with honest "Waiting for Claude Code in a terminal" state (Step-0 Spike 1) |

No critical vulnerability was found in the designed approach.

## 36. Phase 1 findings that changed

| Phase 1 statement | Phase 1b status |
|---|---|
| Codex parser may retain account data (unknown) | **Resolved by design**: allow-list by construction; drops account, plan and credit data |
| Native Codex = bare `codex.exe`, risk from "app dir / CWD" | **Changed**: no such exe on the test machine (MSIX); measured: PATH order wins in the first experiment (later refined by Spike 2: application directory first); new resolve-validate-absolute design |
| Codex startup might be slow (> 15 s) | **Retracted**: actual `initialize` ≈ 0.13 s; the hang was the BOM bug (§27) |
| Codex fresh on refresh (from source) | **Observed** live: new connections + 0.62 s response |
| Default Codex poll 5 min visible / 15 min hidden | **Changed → 10 min while the widget is visible, on-demand for user refresh (min 60 s gap), one scheduled read at `resetsAt`, none when hidden** (each spawn ≈ 12–14 TLS connections) |
| Claude status line is a single slot (risk) | **Resolved** by the tap-mode coexistence design; surface coverage resolved by Spike 1 |
| Claude freshness = bridge run time | **Replaced by the §34 model** |
| Status line consumes no tokens ("not tested") | **Confirmed by the official documentation** |
| Memory and launch-cost figures | measured in Step-0 Spike 4 |
| Codex quota "5-hour / weekly" labels | **Confirmed**: 300-min and 10080-min windows arrive with explicit `windowDurationMins` |
| §13 "network endpoints: UNKNOWN startup behaviour" | **Now characterised** (§32), hostnames still inferred |

Unchanged: stack recommendation, no updater, no telemetry, one duck per pond.

## 37. Independence conclusion

QuotaDuck is **independently authored** from provider-documented and observed contracts, with its own name, art, architecture, tests and constants. The reference project studied during Phase 1 is unlicensed and was used as a **reference only**; no code, assets, documentation text, names or constants were copied, and the no-copy comparison against its commit `71ab591947ebabf6b7e03e0344ceb084665477f7` found only generic, tool-generated or provider-contract-dictated overlap (§28). (Not legal advice.)

## 38. Readiness verdict for Phase 2

**READY — with five Phase 2 "step 0" spikes that are conditions, not blockers.**

What is proven: both quota sources work with zero model use and no credentials on our side; the Codex path returns real windows and resets; the Claude path is documented; we have a truthful freshness and expiry model; and the launch-safety design is clear.

Step-0 spikes (each with its own gate; results in `docs/spikes/STEP0-RESULTS.md`):
1. **Does the status line fire on each Claude surface?** (CLI vs desktop app vs IDE) — needs the owner to add and remove a `statusLine` in a controlled test; QuotaDuck never edits it. If it never fires outside the CLI, the Claude pond is "terminal-only".
2. **.NET `Process.Start` search order** confirmation (§33 item 2) in a scratch project.
3. **Minimal environment** the Codex child actually needs (and whether `-c analytics.enabled=false` is worthwhile).
4. **Memory, size and start-time measurements** for WPF vs the AOT bridge (replacing the Phase 1 estimates).
5. **MSIX discovery implementation check** (package-manager API from an unpackaged app; behaviour when Codex updates mid-run).
