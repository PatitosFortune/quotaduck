# QuotaDuck — project guidance

QuotaDuck is a tiny Windows 11 tray/widget that shows **Claude** and **ChatGPT/Codex** quota side by side, with an original pixel-pond/duck identity. It is for the owner's own use.

**Authoritative spec:** `docs/spec/QuotaDuck-Phase1-Audit.md`. Where its Phase 1b sections (§27–§38) conflict with earlier sections, **Phase 1b wins.** Step-0 outcomes are recorded in `docs/spikes/STEP0-RESULTS.md` and, once approved, override the spec where they differ.

## Hard constraints (do not relax without the owner's explicit approval)

**Independence**
- Independent clean implementation. `pixelsncodes/tokenfish` is **reference-only**: never copy its source, tests, assets, text, names, constants or project structure. It has no licence.

**Stack**
- C# / .NET 10 + WPF (unless a spike disproves it). Claude status-line bridge is a **Native AOT** console exe.
- **No runtime third-party NuGet packages.** BCL/WPF only. Dev-only: test framework. Build-time Microsoft toolchain packages (e.g. ILCompiler) are not runtime dependencies.
- Spike/prototype code lives only under `spikes/` and is never referenced by production code. Promote code only after review.

**Privacy / safety**
- No telemetry, no analytics, no updater in v1.
- **No QuotaDuck-owned network code** (no HttpClient/sockets/listeners). Grep-denylist in CI later.
- No credentials, cookies, browser storage, or auth-file access (`~/.codex/auth.json`, Claude credentials, etc.).
- **No model calls for monitoring**: no prompts, no turns, no dummy pings, ever.
- No admin rights, no services, no autostart in v1, no persisted provider payloads.
- **Never edit the user's Claude/Codex settings automatically.** Show snippets; the user applies them.

**Codex**
- Spawn `codex app-server` only by an **absolute, validated path** (MSIX package-family lookup → canonicalise → exists → Authenticode valid → signer matches). Never launch by bare name; never persist a versioned MSIX path as authoritative — re-resolve per launch.
- The client is **structurally restricted** to exactly three messages: `initialize`, `initialized`, `account/rateLimits/read`. No other method can be emitted. Never use `command/exec`, thread/turn methods, `proxy`, `daemon`, `--listen`, or `--analytics-default-enabled`.
- Write child stdin as **UTF-8 without BOM, LF-terminated** (Windows PowerShell 5.1 prepends a BOM; Codex silently skips a bad first line). Handshake needs a timeout and a specific error.
- Parse responses with an allow-list (limit id/name, windows, reached-type). Discard `accountId`, plan, credits, reset-credit grants, upsell. Never log raw lines/stderr.

**Claude**
- Source is the documented status-line hook only (`rate_limits.five_hour`/`seven_day`). The bridge is a separate tiny exe the user wires up manually; it must also support a silent "tap" mode.
- Freshness model = Phase 1b **§34**: keep `receivedAt` (bridge ran), `changedAt` (provider data changed), `resetsAt` separately; only a recent `changedAt` is "fresh"; **expire windows at `resetsAt`** (show "waiting for next report", never 0% or the old value); same-epoch usage is monotonic; no account identity stored.

**Claude surface coverage — v1 baseline limitation (Step-0 Spike 1; state it plainly in the product)**
- Terminal Claude Code (CLI, incl. VS Code's integrated terminal): status-line hook **works**.
- VS Code Claude Code panel (`claude-vscode`): tested; **does not fire** the status-line hook.
- Claude Desktop Code tab: **unsupported (observed)** — one clean retest, session verified at `cwd = quotaduck-app` with the probe config present, produced zero status-line events (see `docs/spikes/STEP0-RESULTS.md`). Residual uncertainty: the folder was moved mid-session.
- **Bridge state path is fixed and not redirectable:** `<real LocalAppData from the process-user token>\QuotaDuck\bridge\claude-v1.json`. No environment variable (not `LOCALAPPDATA`, not `USERPROFILE`), no command-line argument, no `#if` in production source. Tests use an injected directory or the separate test-seam executable.
- Therefore Claude quota is observable **only while a supported Claude Code terminal session reports**. Usage generated elsewhere (panel, Desktop, claude.ai web) may stay invisible until a terminal session reports again.
- **Never present missing Claude observations as 0%.** No data ⇒ "Waiting for Claude Code in a terminal"; elapsed window ⇒ "waiting for next report"; always show ages.

**Distribution (owner decision):** framework-dependent for v1 (requires the official .NET 10 Desktop Runtime); keep the app footprint tiny.

**Product**
- **One duck per provider pond.** Provider truth over visual symmetry: show the windows each provider really reports; never invent symmetry; never fake freshness; numbers are always visible; colour is never the only channel; honour reduced-motion.
- Do not build polished UI/art until the owner approves that step.

## Layout and commands
- `src/QuotaDuck.Core` — pure logic (models, §34 freshness/reset derivation, Claude merge, Codex read → observations). No I/O, no clock reads (`now` is always a parameter), BCL only, AOT-compatible. State table: `docs/core/FRESHNESS-STATE-TABLE.md`.
- `src/QuotaDuck.Bridge` — the Claude status-line bridge (`quotaduck-statusline.exe`, Native AOT): stdin JSON → Core parser/merge → atomic user-only state file; `--tap` prints nothing. Design/schema/permissions: `docs/bridge/CLAUDE-BRIDGE.md`. Verify: `powershell -File scripts\verify-bridge-aot.ps1`.
- `src/QuotaDuck.Codex` — the Codex quota runner (on-demand `CodexQuotaReader.ReadAsync`; no scheduling): MSIX discovery + signature/signer validation, the single process launcher, the three-message restricted client. Design/contract: `docs/codex/CODEX-RUNNER.md`. Parsing lives in Core (`CodexResponseParser`). Tests run against `tests/QuotaDuck.Codex.FakeServer` (never the real Codex).
- `src/QuotaDuck.Coordinator` — application layer (no UI): watches the bridge state file (passive, never refreshes Claude), schedules Codex pull reads (visible poll, reset read, backoff, single-flight, manual refresh cooldown), exposes `AppSnapshot`. Injected clock/timers. Design: `docs/coordinator/COORDINATOR.md`.
- `src/QuotaDuck.App` — the WPF shell + tray (`QuotaDuck.exe`): a thin consumer of `AppSnapshot` (pure `Presenter`, `ShellController`, placeholder flyout). The ONLY project that references WPF/WinForms; references Core + Coordinator only. Includes the first-run Claude setup guide, tray anchoring (`Shell/FlyoutPlacement.cs`, pure; `Shell/NativeWindows.cs` is the only native-interop file) and the coordinator's read-only `ClaudeSetupProbe` (user-level `statusLine` existence only). Design: `docs/app/APP-SHELL.md`; measure: `powershell -File scripts\measure-app.ps1`.
- `src/QuotaDuck.Art` — the pixel pond/duck art, generated deterministically in pure BCL code (palette, procedural duck sprites, pond/gauge scenes, PNG/ICO encoders). No WPF, no I/O. The tray icon is the generated `src/QuotaDuck.App/Assets/quotaduck.ico` (regenerate with `tools/QuotaDuck.AssetGen`); it must NEVER be rendered through WPF (pulls Direct3D in). Design: `docs/art/PIXEL-ART.md`.
- `tests/QuotaDuck.Core.Tests`, `tests/QuotaDuck.Bridge.Tests`, `tests/QuotaDuck.Codex.Tests`, `tests/QuotaDuck.Coordinator.Tests`, `tests/QuotaDuck.App.Tests`, `tests/QuotaDuck.Art.Tests` — xunit (dev-only); fixtures are strictly synthetic (fictional 2030-01-01 epoch, canary strings in every non-allow-listed field). Never persist a real status-line payload.
- `spikes/` — throw-away; never referenced.
- Build/test: `powershell -File scripts\dotnet.ps1 test QuotaDuck.sln -c Release`; denylist: `powershell -File scripts\check-denylist.ps1`.

## Working rules
- Stop and ask before: downloading/installing anything not already approved, touching the owner's Claude/Codex/Windows configuration, any probe that talks to a provider beyond the approved quota-only handshake.
- Do not commit unless asked. Keep secrets and raw provider responses out of the repo and out of logs.
- Local .NET SDK (if used) is repo-local at `.tools/dotnet` (git-ignored); use `scripts/dotnet.ps1` to run it with telemetry off.
