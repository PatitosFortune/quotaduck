# Codex quota runner (gate 3)

`src/QuotaDuck.Codex` reads Codex quota **once, on demand**: `CodexQuotaReader.ReadAsync(ct)`. There is no scheduling, polling,
persistence or UI in this component. Parsing lives in `QuotaDuck.Core` (`CodexResponseParser`) so it stays pure and fuzz-testable.

## Architecture

```
CodexQuotaReader.ReadAsync            (serialised: one child at a time)
 ├─ CodexExecutableResolver.Resolve   re-run every read: package family -> highest version -> canonical path -> signature -> signer
 ├─ CodexLaunch.Production(exe)       the ONLY launch spec: args, working dir, environment (constants)
 ├─ ProcessAppServerLauncher.Start    the ONLY Process.Start: absolute exe, UseShellExecute=false, ArgumentList, cleared env, binary stdio
 ├─ AppServerInput.SendAsync(enum)    the ONLY way to write: 3 fixed messages, no caller-supplied text/bytes
 ├─ BoundedLineReader                 newline framing over stdout, <= 1 MiB per line, fragment-safe
 ├─ CodexResponseParser.Parse         allow-list streaming parse of each line -> CodexBucket
 └─ CodexObservations.FromRead        Core model (unchanged from the Core gate)
```

## Executable discovery and signature strategy

1. `GetPackagesByPackageFamily("OpenAI.Codex_2p2nqsd0c76g0")` (kernel32; no WinRT, no capability). Resource packages and other
   architectures are skipped.
2. **Highest version only.** A missing/invalid newest package is a failure; there is no fallback to an older copy.
3. `GetPackagePathByFullName` -> install root; both root and `app\resources\codex.exe` are canonicalised
   (`CreateFileW` + `GetFinalPathNameByHandleW`); UNC/device paths are refused; the executable's canonical path must stay inside the
   canonical package root (a junction/symlink leading out is `PathEscapesPackage`).
4. `WinVerifyTrust` on the file (UI none, revocation checks **off**, URL retrieval **cache-only** — QuotaDuck never causes network
   traffic here), then the leaf certificate subject must have **CN = O = "OpenAI OpCo, LLC"** (ordinal match). Never a thumbprint: the
   publisher uses short-lived Microsoft ID Verified certificates.
5. The ~360 ms check on a 310 MB binary is cached **only** by the identity tuple (canonical path, package full name, size, last-write
   time) and only after every check passed. A new version, path, size or timestamp re-verifies; a failure is never cached.
6. Never PATH, a bare name, a `.cmd`/`.ps1` shim, a user-configurable path, an environment variable or a command-line argument.
   Known limit: a time-of-check/time-of-use gap exists between validation and `CreateProcess` (an attacker able to replace files
   inside `WindowsApps` already has administrator rights).

## Child process contract

| Aspect | Value |
|---|---|
| Executable | the validated absolute path (`Path.IsPathFullyQualified` re-checked by the launcher) |
| Arguments (`ArgumentList`, never a command line string) | `app-server`, `--stdio`, `-c`, `analytics.enabled=false` |
| Never | `--listen`, `proxy`, `daemon`, `exec`, `--strict-config` (a future config change must not make reads fatal) |
| Environment | **`SystemRoot` only** (value from the Windows-folder API). Nothing inherited: no PATH, USERPROFILE, HOME, CODEX_*, proxy or API-key variables |
| Working directory | the system directory (no project-local Codex configuration can be picked up) |
| stdin/stdout/stderr | redirected **binary** streams: no encoding layer, so no BOM can ever be written. First transmitted byte is `{`, lines are LF-terminated |
| stderr | drained and discarded unseen (so the child can never block); never stored or logged |
| Window | none |

## Protocol subset (exactly three messages out)

```
{"id":1,"method":"initialize","params":{"clientInfo":{"name":"quotaduck","title":"QuotaDuck","version":"0.1.0"}}}
{"method":"initialized"}
{"id":2,"method":"account/rateLimits/read"}
```

`OutboundMessage` is a three-value enum; its bytes are compile-time constants. No API accepts text, bytes, a method name or a stream.

Inbound handling: notifications are ignored unread (<= 256 lines per phase, then `ProtocolViolation`); a server-initiated request
(a message with both `method` and `id`) is a `ProtocolViolation` — never answered, never acted on; a reply with an unexpected id is a
`ProtocolViolation`; a JSON-RPC `error` is `Rejected` and keeps **only its numeric code**; unparsable/oversized/duplicate-key lines are
`Malformed`; EOF is `ProcessExited`; deadlines (initialize 10 s, read 15 s, defaults) are `Timeout`. On any failure the whole process
tree is killed; on success stdin is closed and the child gets 2 s to exit by itself before the tree is killed.

## Parser: retained vs discarded

| Retained (allow-list) | Discarded (skipped token by token, never materialised) |
|---|---|
| message kind; numeric `id`; `error.code` | `error.message`, `error.data`, notification/request `params`, the method name |
| per bucket: `limitId` (or the by-id map key), `limitName`, `primary`/`secondary` | `accountId`, `planType`, `credits` (incl. balance) |
| per window: `usedPercent` (finite 0..100), `windowDurationMins` (>0), `resetsAt` (integer seconds 2000..2100) | `rateLimitResetCredits` (grants), `rateLimitUpsell` (banner/referral/URLs), `ordinaryUsageAllowed` |
| whether `rateLimitReachedType` is set (flag only) | `spendControlReached`, `individualLimit`, `normalModelSlug`, `userAgent`, `codexHome`, everything unknown |

Bounds: line <= 1 MiB, nesting <= 32, <= 16 buckets, valid UTF-8, **no BOM tolerance**, duplicate allow-listed keys reject the line.
An invalid window is dropped and counted; its sibling survives. No coercion (a string percentage is invalid).

## Failure never refreshes age

A failed read returns **no observations** and an attempt record only (`ProviderAttempt(At, kind)`). Core's provider derivation keeps
the last successful read's own timestamps, so a failure can never make older data look fresher. Caller cancellation is not a provider
failure and records no attempt. Nothing about Codex observations is persisted in v1.

## Network boundary

QuotaDuck's own code contains **no HTTP, socket, WebSocket or networking API** (enforced by assembly-reference and string-literal
guards and the denylist script). The only network activity is that of the Codex child process, which QuotaDuck does not control:
measured in Step 0, one spawn opens roughly 7–12 short TLS connections to `chatgpt.com`/Cloudflare during startup and the read. The
analytics override only switches off Codex's own analytics for this child; it does not prevent Codex's authentication/rate-limit
traffic, which is the data source. QuotaDuck sees only the stdout JSON.

## Testing

* `CodexResponseParserTests` (Core.Tests): allow-list, canaries, duplicates, bounds, windows, fuzz.
* `QuotaDuck.Codex.Tests` run the **real** runner and launcher against `tests/QuotaDuck.Codex.FakeServer` (a fake app-server started
  through the dotnet host): normal, notifications, fragments, stderr noise, malformed, wrong ids, unexpected server request, oversize,
  flood, error replies, timeouts, crashes, early EOF, grandchild kill, stdin-close exit, ignore-EOF kill, cancellation, serialisation,
  hostile parent environment, exact stdin bytes (first byte `{`, no BOM, only the three messages).
* Discovery tests use synthetic package layouts plus the genuine Windows signature code on a Microsoft-signed file (accepted only
  when the policy matches; unsigned/tampered refused; junction escape refused).
* Architecture guards: assembly references, string literals (no URLs/credentials/config paths/forbidden app-server surfaces), no
  environment reads, a single `Process.Start` site, public surface = reader + result types, no file I/O, injected clock.
