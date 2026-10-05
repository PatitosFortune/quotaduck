# Step 0 — spike results (2026-10-04 / 05)

Environment: Windows 11, .NET SDK 10.0.401 (runtime 10.0.12), Visual Studio Build Tools (MSVC 14.51), Codex MSIX desktop app. All spike code is under `spikes/` and is throw-away: it is never referenced by production code.
Status: **all five spikes complete** (Spike 1: terminal CLI works, VS Code panel does not, Desktop Code tab unsupported as observed). The outcomes below override `docs/spec/QuotaDuck-Phase1-Audit.md` where they differ.

## Spike 1 — Claude status-line surface coverage

Method: a temporary **project-local** `.claude/settings.local.json` (applied and removed by hand by the owner); the probe records only presence flags and surface markers, no payload content. Evidence = probe events cross-checked against each session's metadata and its `entrypoint` label (no conversation content read).

| Surface | Session run in the test project? | Status line fired? | Evidence |
|---|---|---|---|
| **Terminal Claude Code CLI** (`entrypoint=cli`, including VS Code's *integrated terminal*) | yes | **YES — 3 runs**: 1 before the first reply (no `rate_limits`), 2 after it (`rate_limits` present) | `five_hour` and `seven_day` both present with `used_percentage` and `resets_at` |
| **VS Code Claude Code panel** (`entrypoint=claude-vscode`) | yes (a substantial session) | **NO — zero events** despite the config being in place | the session exists and is labelled `claude-vscode`; no probe line came from it |
| **Claude desktop app, Code tab** (`entrypoint=claude-desktop`) | see the re-test below | **NO** (see re-test) | |

Also learned: Claude Code runs the status line through **Git Bash** on Windows (so the command string is shell-evaluated: forward slashes, and the executable path must be a quoted word); the payload contains `cwd`, `transcript_path`, `session_id`, `model`, `cost`, `workspace`, … (the bridge process sees sensitive fields and must discard them); the first status-line run of a session lacks `rate_limits`.
Not separately tested: a standalone Windows Terminal run (same `claude.exe`, expected identical).

**Gate result / product implication:** the Claude pond is fed **only by Claude Code running in a terminal** (CLI, including VS Code's integrated terminal). The VS Code panel does not run the status line, and the Desktop Code tab is unsupported. This is a visible product limitation: when no terminal report has ever arrived, the pond shows "Waiting for Claude Code in a terminal" (never 0 %), with a one-line explanation that panel and desktop sessions don't report quota. Usage in the panel, the Desktop app or claude.ai is invisible until the next terminal report.

### Desktop Code tab re-test — result: **unsupported (observed)**
- Attempt A (via the Desktop UI) created its session with the working folder of an unrelated research project, so it was not a valid test.
- Attempt B (clean): a Desktop session was run with its working folder set to the test project, the identical project-local probe config in place and the baseline commit present, and a full turn with tool calls exchanged. **No events file was ever created** — the status-line hook did not fire. (For contrast, the terminal CLI produced an event at session start.)
- Residual uncertainty: the working folder was changed mid-session, so a settings reload cannot be 100 % excluded. For planning, the Desktop tab is **unsupported**; the product must not depend on it. The probe config was removed afterwards and the user-level `statusLine` key was confirmed absent.

## Spike 2 — .NET `Process.Start` resolution

.NET 10.0.12 on Windows 11, `UseShellExecute=false`, a harmless scratch child that prints its own path.

| Scenario | Ran copy from |
|---|---|
| bare `qdchild.exe`, copy only in parent CWD | **not found** |
| bare, copy only in PATH dir | PATH dir |
| bare, copies in CWD + PATH | PATH dir (CWD ignored) |
| bare, copy only in **application dir** (launching exe's folder) | application dir |
| bare, copies in app dir + PATH | **application dir wins over PATH** |
| bare, app dir + CWD + PATH | application dir |
| bare, copy only in `psi.WorkingDirectory` | not found |
| bare without extension (`qdchild`) | resolved via PATH (extension appended) |
| relative `.\qdchild.exe` | depends on the **parent's** CWD; `psi.WorkingDirectory` ignored |
| **absolute path**, decoys in app dir / CWD / PATH / WorkingDirectory | **exactly the named file** |
| absolute path containing a space with a decoy `<dir>\sp.exe` | exactly the named file (quoted correctly) |
| absolute `.cmd` with arg `x&echo.>marker` | **command injection occurred** (cmd interprets `&`) |

**Final safe launch rule:** `FileName` must be a fully-qualified, canonical, drive-letter `.exe` path (reject relative, rooted-relative, UNC, `\\?\`-odd forms, `.cmd/.bat/.ps1`); `UseShellExecute=false`; arguments only from compile-time constants via `ArgumentList`; explicit `WorkingDirectory` to a QuotaDuck-owned scratch directory. This removes all PATH / app-dir / CWD ambiguity. The bare-name risk is larger than first assumed: the **application directory beats PATH**, so a writable portable app folder is itself a plant vector.

## Spike 3 — minimal Codex child environment

Launched the signed MSIX `codex.exe app-server --stdio` (path from the Spike 5 routine), `Environment.Clear()` + explicit variables, BOM-free UTF-8/LF stdin, scratch working directory, 3 messages only (`initialize`, `initialized`, `account/rateLimits/read`). 18 spawns (control 1, bottom-up 7, leave-one-out 7, analytics 3). Only booleans and timings recorded.

| Result | |
|---|---|
| Full inherited environment (control) | OK — init ≈ 110 ms, read ≈ 650–730 ms, 2 windows |
| **Empty environment** | FAIL (`-32603` on the read) |
| **`SystemRoot` alone** | **OK** |
| Leave-one-out from {SystemRoot, USERPROFILE, LOCALAPPDATA, APPDATA, TEMP, TMP, PATH} | **only dropping `SystemRoot` fails**; every other variable is individually unnecessary |
| `config.toml` / `auth.json` (size and modification time only) | unchanged |

**Minimal environment specification:** *required:* `SystemRoot`. *Nothing else was needed on the test machine* (Codex finds its own home and login through Windows APIs). *Forward only if present and only these:* `CODEX_HOME` (non-default Codex home), proxy/cert variables (`HTTPS_PROXY`, `HTTP_PROXY`, `ALL_PROXY`, `NO_PROXY`, `SSL_CERT_FILE`, `SSL_CERT_DIR`, both cases) — their effect is untested. *Never forward:* `ANTHROPIC_*`, `OPENAI_*`, anything matching `*KEY*|*TOKEN*|*SECRET*|*PASSWORD*`, the parent `PATH`. The child keeps using its own existing authentication.

**Analytics:** `-c analytics.enabled=false` is accepted; under `--strict-config` it passes, while a bogus key is rejected with exit code 1 ("unknown configuration field … in -c/--config override") → the key is genuinely recognised by this version, and the override is per-invocation (`config.toml` untouched). App-server analytics are already off by default, so the override only matters if the user's config opts in. **Recommendation:** always pass `-c analytics.enabled=false`; **never** pass `--strict-config` (a future key rename must not break us); treat it as best-effort. The network effect is not observable passively.

**Quota-only handshake:** succeeded in every viable profile. No `proxy`, `daemon`, `--listen`, turn, or `command/exec` was used.

## Spike 4 — real footprint

Prototypes: a WPF app (tiny XAML window; `--tray` = hidden window + WinForms `NotifyIcon`) and a Native AOT bridge workload (bounded stdin read, streaming allow-list parse, atomic file write). Default JIT, no ReadyToRun, no trimming (not optimised). Warm OS file cache.

| WPF prototype | Framework-dependent | Self-contained |
|---|---|---|
| Size on disk | **0.16 MB** (4 files; needs the .NET 10 Desktop Runtime) | **154.1 MB** (251 files); 64.5 MB zipped |
| Ready, tray mode (since process start) | median **338 ms** (first 340) | median 326 ms (first 340) |
| Ready, window mode | median 520 ms (first 718) | median 510 ms (first 519) |
| Idle 10 s, **tray mode**: private working set / working set / commit | **10.9 MB / 58.6 MB / 24.0 MB** | 10.8 / 58.4 / 23.9 MB |
| Idle 10 s, window mode | 34.7 / 95.0 / 58.6 MB | 34.3 / 94.7 / 58.0 MB |

| Bridge (40 runs, spawn → exit, stdin piped) | Size | First | Median | p90 |
|---|---|---|---|---|
| **Native AOT** | **1.36 MB** | 77 ms | **29 ms** | 32 ms |
| self-contained single-file (JIT) | 70.1 MB | 133 ms | 85 ms | 87 ms |
| framework-dependent single-file | 0.16 MB | 120 ms | 85 ms | 87 ms |

**Verdict:** the stack recommendation **stands**: tray-resident idle cost ≈ 11 MB private / 59 MB working set and ≈ 0.33 s to ready; the AOT bridge is ~3× faster and ~50× smaller than a self-contained .NET exe, which matters because Claude runs it on every status-line update. **Correction to Phase 1:** a self-contained WPF app is **154 MB**, not "tens of MB". Distribution options: (a) framework-dependent (tiny, requires the .NET 10 Desktop Runtime) or (b) self-contained (no install, 154 MB / 64 MB zipped).
Toolchain note: Native AOT needs the Visual Studio Build Tools C++ workload and `vswhere` on PATH (handled in `scripts/dotnet.ps1`); ILCompiler and runtime packs are Microsoft build-time packages from nuget.org, not runtime dependencies.

## Spike 5 — MSIX Codex discovery

Prototype `spikes/spike5-msix-discovery`: kernel32 `GetPackagesByPackageFamily` + `GetPackagePathByFullName`, final-path canonicalisation, `WinVerifyTrust` embedded Authenticode with UI off / revocation off / cache-only URL retrieval, signer CN+O match. **Nothing is executed.**

Real result: family `OpenAI.Codex_2p2nqsd0c76g0` → versioned package folder → canonical `…\WindowsApps\…\app\resources\codex.exe`, signature valid, signer `CN="OpenAI OpCo, LLC", O="OpenAI OpCo, LLC"`. **Validation cost ≈ 360 ms cold / 320 ms warm** (the exe is about 310 MB and is hashed in full).
Discovered fact: the signing certificate is a **short-lived Microsoft ID Verified certificate (valid 3 days)** counter-signed by Microsoft's timestamp authority — so **do not pin a thumbprint; pin subject CN+O**.

| Scenario (10 + real) | Outcome |
|---|---|
| Real package | `Ok` |
| Not installed (bogus family) | `NotInstalled` |
| v1 only / v1+v2 installed / v2 then removed | `Ok`; picks highest version; re-resolved each call, nothing cached |
| Locator lists a version whose folder is gone | `PackagePathMissing` |
| Newest version has no exe | `ExecutableMissing` — **no silent fallback to an older version** |
| Valid signature, wrong signer | `SignerMismatch` |
| Unsigned | `SignatureInvalid` (`TRUST_E_NOSIGNATURE`) |
| Signed file with one byte modified | `SignatureInvalid` (`TRUST_E_BAD_DIGEST`) |
| `app\resources` is a junction to an outside directory | `PathEscapesPackage` |

**Final discovery/launch contract:**
1. Stateless resolve on every launch: package family → highest-version non-resource, architecture-matching package → install root → `app\resources\codex.exe`.
2. Canonicalise root and exe (final path); require a local drive-letter path, `.exe`, exe inside the root.
3. `WinVerifyTrust` must return 0; the leaf signer CN and O must equal `OpenAI OpCo, LLC` (policy object, not thumbprint). Fail closed on any error (including "can't verify offline").
4. Cost control: run the full check at app start and whenever the identity tuple (canonical path + package full name + size + last-write time) changes; per spawn do only the cheap identity check. WindowsApps is admin-owned, so same-user TOCTOU is not exploitable.
5. Typed failures surfaced to the UI with specific text; **no bare-name fallback, no older-version fallback**; a manually chosen path (later feature) goes through the same validation.
6. Never persist a versioned MSIX path as authoritative.
Not tested: a *real* Codex update mid-run (simulated with fake packages), ARM64, certificate rotation across the pinned subject.

---

## Corrections to Phase 1 / 1b (all folded into the decisions below)
1. **Signer pinning:** Phase 1b said "signer recorded at setup". The certificate is short-lived → pin **subject CN+O**, never a thumbprint.
2. **Validation cost:** per-spawn full validation is about 360 ms for the 310 MB exe → validate by identity tuple, not on every spawn.
3. **Minimal environment:** Phase 1b guessed a curated set (USERPROFILE/APPDATA/TEMP/…); reality is `SystemRoot` only.
4. **Search order:** Phase 1b measured PATH > CWD with CPython; .NET 10 shows **application directory > PATH**, CWD never. Portable app folders are a plant vector.
5. **WPF self-contained size:** 154 MB (64 MB zipped), not "tens of MB".
6. **Claude surfaces:** Phase 1b left coverage UNKNOWN; now terminal-only (VS Code panel confirmed not firing).
7. Matches expectations: AOT bridge 1.36 MB / 29 ms (estimate 1–3 MB / < 30 ms); WPF tray working set 58.6 MB (estimate 40–60 MB).

## Architecture decisions that followed
- **Claude source = terminal CLI only**, stated as a product limitation with a dedicated "waiting for terminal Claude Code" state.
- **Discovery:** kernel32 package APIs (no WinRT); subject-pinned `WinVerifyTrust` (revocation off, cache-only retrieval); highest version only; identity-cached validation; stateless re-resolve per launch (Spike 5 contract).
- **Launch:** absolute canonical `.exe` only; `SystemRoot`-only environment (+ optional allow-listed `CODEX_HOME` and proxy/cert variables); BOM-free UTF-8 + LF; explicit working directory; always `-c analytics.enabled=false`, never `--strict-config`.
- **Bridge:** Native AOT; the build uses the Visual Studio Build Tools and `vswhere` on PATH.
- **Runtime distribution:** framework-dependent for v1 (the official .NET 10 Desktop Runtime is required); self-contained rejected for v1.
- **Tooling:** repo-local SDK via `scripts/dotnet.ps1`; `nuget.config` restricted to nuget.org.

## Implementation sequence (as followed)
1. Skeleton: solution, `Directory.Build.props` (nullable, warnings-as-errors, deterministic), `src/QuotaDuck.Core`, xunit tests (dev-only), `scripts/check-denylist.ps1` (no `HttpClient`, sockets, `Registry`, or bare-name `Process.Start`).
2. **QuotaDuck.Core:** provider-neutral models; Claude-payload and Codex-rate-limits parsers (Utf8JsonReader skip-parse → closed types; oversize, malformed and fuzz tests); the freshness engine per spec §34 (`receivedAt` / `changedAt` / `resetsAt`, Elapsed state, same-epoch monotonic merge, clock and resume handling).
3. **Claude bridge (AOT):** production exe, atomic current-user-ACL state file, coalesced writes, `--tap` mode, size and start budgets enforced by a test (≤ 2 MB, ≤ 40 ms median).
4. **Codex runner:** the reviewed Spike 5 discovery; a structural 3-method client; environment and launch contract; timeouts; typed errors; backoff; identity-cached validation; tests against a fake app-server (including the BOM trap).
5. **Sources and scheduling:** directory watcher plus debounce for the bridge file; one-shot reset timers (resume and time-change aware); Codex cadence (10 min while visible, user refresh ≥ 60 s apart, one read at reset + 5 s, none when hidden).
6. **WPF shell:** tray icon and flyout, single instance, reduced-motion and high-contrast hooks; footprint re-measured.
7. **Art, setup guide, layout polish, release packaging.**

## Decisions recorded 2026-10-05
- **Distribution:** framework-dependent for v1 (official .NET 10 Desktop Runtime required).
- **Terminal-only Claude:** accepted as the v1 baseline limitation (documented in `CLAUDE.md` and the product).
- **Desktop Code tab:** one clean re-test after the baseline commit (result above).
- **Baseline commit:** approved (Step-0 work only).

Confirmed Claude limitation: terminal status line **works**; VS Code panel **does not fire** (tested); Desktop tab **unsupported (observed)**; missing observations are **never** shown as 0 %; usage generated elsewhere may stay invisible until a terminal session reports again.
