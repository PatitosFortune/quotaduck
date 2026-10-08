# Claude bridge (`quotaduck-statusline.exe`)

Deliberately boring: **status-line stdin JSON → allow-listed quota fields → Core merge → atomic local state**. Nothing else.

> **Product limitation (observed in Step 0):** the status-line hook fires for Claude Code in a **terminal** (CLI, including VS Code's integrated terminal). It does **not** fire for the VS Code Claude Code panel, and it did not fire for the Claude Desktop Code tab (one clean retest; residual uncertainty because the folder was moved mid-session). Claude quota in QuotaDuck is therefore *terminal-Claude-Code driven*; usage generated elsewhere may stay invisible until a terminal session reports again. Missing data is never shown as 0 %.

## Modes and exit codes
| Invocation | stdout | State | Exit |
|---|---|---|---|
| `quotaduck-statusline` (normal) | one short ASCII line, e.g. `QuotaDuck \| 5h 23.5% used \| wk 41.2% used` | updated if the payload has a usable window | 0 |
| `quotaduck-statusline --tap` | **zero bytes** | same | 0 |
| `--label=<text>` (either mode) | prefix of the line (`[A-Za-z0-9 ._-]`, ≤ 24 chars) | – | 0 |
| any other / duplicate / unsafe argument | nothing | nothing read or written | **2** |

Runtime problems (bad payload, locked/unwritable state, I/O errors) never fail the status line: no output beyond the normal line, exit 0, nothing logged. The line is built only from the persisted state through the Core derivation, so it can never contain payload text: `5h 24% used`, `5h 24% used (old)`, `5h reset`, `5h --`, or `waiting for first report`.

The bridge does **not** wrap or execute another status-line command, and never edits Claude settings.

## Setup (you apply it; QuotaDuck never edits Claude settings)
No existing status line — in `~/.claude/settings.json` (Claude Code runs status lines through Git Bash here, so use forward slashes, and the path is ALWAYS a quoted shell word inside the JSON string, so folders with spaces, apostrophes, `$` or non-ASCII characters work; the app's generated snippet does this for you):
```json
{ "statusLine": { "type": "command", "command": "\"C:/Path/To/quotaduck-statusline.exe\"" } }
```
Existing status line script — add one line that feeds the same JSON to the bridge in **tap** mode (your own output is untouched):
```bash
input=$(cat)
echo "$input" | "C:/Path/To/quotaduck-statusline.exe" --tap
# ... the rest of your script keeps using $input
```
A project-level `statusLine` shadows a user-level one in that repo. Moving the exe means updating the path.

## Parser (`QuotaDuck.Core.Claude.ClaudeStatusLineParser`, pure)
* Reads **only** `rate_limits.five_hour|seven_day.used_percentage|resets_at` with a streaming `Utf8JsonReader`; everything else is skipped token by token. No DOM, no string materialised, nothing retained.
* Bounds: input ≤ 256 KiB (oversized stdin is read only up to the cap, then drained ≤ 4 MiB and rejected); nesting ≤ 32 anywhere (even in skipped fields); whole payload must be valid UTF-8 (a BOM is tolerated); duplicate allow-listed keys reject the payload.
* Validation split: the parser rejects what is not representable (non-number / non-finite / outside 0..100 percentage; reset not an integer or outside 2000–2100). Clock-relative plausibility (reset in the past, further than window length + 10 min) is the Core merge's job, because the parser is clock-free.
* Windows are independent: one absent/invalid window never affects the other; no type coercion (`"40"` is invalid).
* Result type holds only numbers and flags — no string can escape (reflection-tested).

## Persisted state — `%LOCALAPPDATA%\QuotaDuck\bridge\claude-v1.json`
Schema v1, ≤ 4096 bytes, UTC millisecond times:
```json
{"schema":1,
 "fiveHour":{"usedPercent":23.5,"resetsAt":"2030-01-01T15:00:00.000Z","changedAt":"…Z","receivedAt":"…Z","lowerCandidate":null,"lowerStreak":0},
 "sevenDay":{…}|null}
```
`changedAt` (provider data changed) and `receivedAt` (hook ran) are kept separate (§34); only a recent `changedAt` is freshness evidence. Stored timestamps never go backwards (clock step-back safe). No account, session, path, model, cost, prompt or raw JSON can be stored — the record type has no field for them. Unknown fields in a state file are ignored and dropped on the next write; a **future-schema** file is never overwritten; corrupt/oversized files are treated as empty and healed by the next valid report; an invalid window record is dropped on its own.

## Permissions and atomic write
* Directory: created with a **protected DACL granting FullControl to exactly one SID — the current user** (SDDL `D:P(A;OICI;FA;;;<sid>)`, SID strictly validated, object/container inherit, so the state file inherits the same). If that cannot be established the bridge **fails closed** (no directory, no write). An existing directory is left as is (no permission churn). The parent `%LOCALAPPDATA%\QuotaDuck` keeps normal permissions.
* Write: temp file in the same directory (`CreateNew`, no sharing, write-through, flushed) → atomic replace. Any failure deletes the temp file and leaves the previous file untouched. Leftover temps from a crashed run are removed under the lock; nothing else in the directory is touched. No backups, history or logs.
* Concurrency: named mutex derived from the state path, 300 ms bounded wait; on timeout the run skips its write and shows what is on disk (the next hook run catches up). Abandoned mutexes are recovered.
* Unchanged readings update `receivedAt` at most once per 60 s and **never** `changedAt`.
* **Location is fixed and not redirectable:** `<the user's real LocalAppData>\QuotaDuck\bridge\claude-v1.json`. The root is derived from the **process-user token** — `SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT_PATH, token)` cross-checked to lie strictly inside `GetUserProfileDirectoryW(token)` (disagreement ⇒ nothing is written). The production assemblies read **no environment variable at all** (no `LOCALAPPDATA`, no `USERPROFILE`), accept **no path argument** (any unknown argument ⇒ exit 2 before reading or writing), and contain no test hook (compiled-assembly and AOT-binary scans).
  * *Why not just "the known folder":* investigated empirically — `Environment.GetFolderPath` and a plain `SHGetKnownFolderPath` expand `%USERPROFILE%` from the process environment, and the shell caches the answer per process, so a hostile `USERPROFILE` (or any component that looked it up first) redirects them to e.g. `C:\hostile-USERPROFILE\AppData\Local`. `KF_FLAG_DEFAULT_PATH` + token bypasses both; tests run a fresh process with every relevant variable hostile, including after another component has already poisoned the cache.
  * *Testability without weakening production:* in-process tests inject a directory into `FileClaudeStateStore` directly. Process-level tests run a **separate test-only executable** (`tests/QuotaDuck.Bridge.TestSeam`) built from the same sources except `Program.cs`; its entry point takes `QD_TEST_STATE_DIR` and refuses to run (exit 3) without it, so a test cannot touch the real state. Production source contains no `#if`, override or extra argument.

## What the bridge contains / never contains
Contains: bounded stdin read, the Core parser/merge/codec/formatter, one named mutex, one fixed-file store, and six `LibraryImport` functions in `Native.cs` (create a user-only directory: `CreateDirectoryW`, `ConvertStringSecurityDescriptorToSecurityDescriptorW`, `LocalFree`; locate LocalAppData from the token: `SHGetKnownFolderPath`, `GetUserProfileDirectoryW`).
Never: network APIs, process spawning, registry, browser/credential access, Claude settings writes, repository/directory scanning (only its own state directory for temp cleanup), telemetry, logging, updater. Enforced by `scripts/check-denylist.ps1` (2 narrow named allowances), reference/literal scans of the compiled assemblies, and tests.

## Verification
`powershell -File scripts\verify-bridge-aot.ps1` — publishes the production bridge **and** the test-seam build as Native AOT; gates the production exe (single file, size, binary string scan for override/test-hook strings); runs the process tests against both AOT binaries; and measures timing (state redirected to a temp directory via the seam; the real state directory is never touched).


## Atomic replace retry (coordinator gate)

The state is written to a temp file in the same directory and moved over `claude-v1.json` (`File.Move(overwrite)`). Windows can refuse that
rename while another process (e.g. the app reading the file) has the destination open. Only this **final replace** is retried: at most three
retries after 5, 10 and 20 ms (<= 35 ms added in the worst case), and only for a sharing/lock violation or an access-denied error on an
existing, non-read-only destination. Missing directories, bad paths, read-only destinations, disk-full and everything else are permanent
and fail on the first attempt. When the retries are exhausted the exception is handled as before: the temp file is deleted, the previous state
file is left byte-for-byte intact, and the status line is never blocked for longer than the lock timeout.
