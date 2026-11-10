# NU1903: Microsoft.DotNet.ILCompiler 10.0.12 (GHSA-p3jp-9v83-76w9)

Recorded 2026-11-09 while finishing the Copy-usage PR. **Assessment only: no dependency, SDK or build change was made.**

> **Status (2026-11-10): the source now builds the helper with the patched 10.0.14 toolchain; the published `1.0.0-rc.1` helper is unchanged (built with 10.0.12).** `global.json` pins SDK 10.0.403 and the build resolves the Native AOT compiler and runtime pack at 10.0.14. Build, tests and the denylist passed; the Native AOT verification script did **not** fully pass (one timing gate FAILED and carries an owner-accepted exception; see "Verification record"). Nothing has been released, so the fix is not yet in any shipped build; a release containing the rebuilt helper is pending release verification and owner approval.

## What the build reports

`scripts\dotnet.ps1 build/test` prints, for `src/QuotaDuck.Bridge`:

```
warning NU1903: Package 'Microsoft.DotNet.ILCompiler' 10.0.12 has a known high severity vulnerability, https://github.com/advisories/GHSA-p3jp-9v83-76w9
```

It is non-fatal because `Directory.Build.props` lists NU1901-NU1904 in `WarningsNotAsErrors`. It affects only the Native AOT helper (`quotaduck-statusline.exe`); the WPF app is framework-dependent and does not use ILCompiler.

## The advisory (checked against GitHub's advisory page and Microsoft's announcement, 2026-11-10)

Source: the GitHub advisory page and Microsoft's announcement (dotnet/announcements #494), read through delegated fetches. The fields below were read verbatim; parts of those pages (including the Native AOT republish guidance) were only summarised and are marked as such.

- Title: ".NET Brotli Decoder Out-of-bounds Write Vulnerability", High (CVSS 3.1 score 8.1, CWE-787), published 2026-10-25.
- Affected: `Microsoft.DotNet.ILCompiler` `>= 10.0.0, < 10.0.14` (patched 10.0.14). Also listed: `Microsoft.NETCore.App.Runtime.NativeAOT.win-x64` `>= 10.0.0, < 10.0.14`.
- Exposure needs an app to **decompress attacker-controlled Brotli data** (`BrotliStream`, `BrotliDecoder`, or HTTP/ASP.NET automatic Brotli decompression). "An application is not exposed merely because an affected .NET package is installed."
- Not confirmed verbatim: whether `runtime.win-x64.Microsoft.DotNet.ILCompiler` is listed by name. It is the same toolchain, shipped at the same version, and the build now resolves it at 10.0.14.
- Microsoft's announcement lists SDKs 10.0.114 and 10.0.403 as the updated .NET 10 SDKs. That Native AOT executables must be rebuilt with the patched toolchain comes from a summarised part of the announcement.

Availability of 10.0.14 is confirmed: nuget.org lists it for `Microsoft.DotNet.ILCompiler`, `runtime.win-x64.Microsoft.DotNet.ILCompiler` and `Microsoft.NETCore.App.Runtime.NativeAOT.win-x64`, and Microsoft's 10.0.14 release notes (25 Oct 2026) list SDKs 10.0.403 and 10.0.114.

## Can the QuotaDuck helper reach the affected functionality? (evidence)

| Check | Result | Strength |
|---|---|---|
| Source: any use of Brotli / `System.IO.Compression` in `src/`, `tools/` | Only `QuotaDuck.Art/Encoders.cs` uses `ZLibStream` (deflate, **compression** of PNG/ICO output). No Brotli anywhere. | Verified (grep) |
| Is `QuotaDuck.Art` part of the helper? | No. `QuotaDuck.Bridge` references only `QuotaDuck.Core`; Core has no compression use. | Verified (csproj + grep) |
| Helper's input | Claude Code's status-line JSON on stdin, parsed with `System.Text.Json`; no decompression step. | Verified from design/source |
| Shipped binary: the `quotaduck-statusline.exe` inside the rc.1 ZIP (SHA-256 `cdec2ef6...05a5`, matches `files.sha256`) | Zero occurrences of `brotli`, `BrotliDecoder`, `BrotliStream`, `System.IO.Compression`, `clrcompression`, `zlib`, `inflate`; its DLL import list has no compression library. | Verified (byte search, nothing executed) |
| The WPF app (framework-dependent) | Uses the user's installed .NET runtime, not ILCompiler; no Brotli use in source. | Verified (grep) |

**Conclusion (inference from the above):** the helper contains no Brotli code path that any input can reach, so the practical exploitability of this advisory against QuotaDuck is very low. The helper still *was built with* the affected toolchain, and the NativeAOT runtime pack (`microsoft.netcore.app.runtime.nativeaot.win-x64` 10.0.12) that is statically linked into it is on the same affected list. A scanner or a reader of the build log will see the warning.

## Limits of this assessment

- String/import absence in a stripped native binary is strong evidence, not proof; I did not disassemble the binary or inspect its symbol-level linkage.
- I did not verify the advisory's affected ranges first-hand (see above).
- The `artifacts/bridge-aot/quotaduck-statusline.exe` in the working tree is **not** byte-identical to the one in the ZIP; the ZIP's copy was used.

## Applicability to rc.1 (unchanged conclusion)

- **Package presence:** the published `1.0.0-rc.1` helper was built with the affected 10.0.12 compiler and Native AOT runtime pack. That is demonstrated.
- **Exploitability:** not demonstrated. The evidence above (no Brotli or compression use in the helper's source, no decompression step in its input path, no Brotli strings or compression imports in the shipped binary) shows no reachable path, but string absence in a stripped binary is not proof, so exploitability is not conclusively ruled out.
- The rc.1 release assets are not altered. The fix is a rebuilt helper in a release that is pending release verification and owner approval.

## Remediation (done in source; not yet released)

- `global.json` pins SDK **10.0.403** (`rollForward: latestPatch`). The Native AOT compiler version is not set in the repo: it comes from the SDK's bundled versions file, where 10.0.403 sets `ILCompilerPackVersion="10.0.14"` (it is 10.0.12 in SDK 10.0.401).
- `NOTICE` embeds the third-party notices shipped with the 10.0.14 compiler package. Their text is byte-identical to the 10.0.12 package's, so only the two marker lines changed; `release.ps1` checks this embedding against the resolved package.
- No package reference, application code, script or threshold was changed.

## Verification record (2026-11-10, one Windows machine, repo-local tooling)

**SDK acquisition.** SDK 10.0.403 was downloaded from `https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip` (300,745,255 bytes). Its SHA-512 matched the value published in the official `dotnet/core` release metadata (`release-notes/10.0/releases.json`, release 10.0.14, SDK 10.0.403, rid win-x64): `7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48`. The archive had no traversal or rooted paths. It was extracted to a staging folder first; SDK files were then copied into `.tools/dotnet` without overwriting or deleting anything (5,555 files added, 0 changed, 0 removed), and SDK 10.0.401 remains installed. The copy added workload-manifest subfolders under `sdk-manifests\10.0.100`; QuotaDuck uses no workloads. In the staged SDK, 33 of 36 `.exe` files carry a valid Microsoft signature; the 3 unsigned files are app-host templates, which are unsigned in the 10.0.401 install too.

**Selected tooling.** `scripts\dotnet.ps1 --version` reports `10.0.403`. `ILCompilerPackVersion` for net10.0 is `10.0.14`.

**Resolved packages** (`src/QuotaDuck.Bridge/obj/project.assets.json`): `Microsoft.DotNet.ILCompiler` 10.0.14, `runtime.win-x64.Microsoft.DotNet.ILCompiler` 10.0.14, `Microsoft.NETCore.App.Runtime.NativeAOT.win-x64` 10.0.14.

| Check | Result |
|---|---|
| `build QuotaDuck.sln -c Release` | Succeeded, 0 warnings, 0 errors; NU1903 no longer reported |
| `test QuotaDuck.sln -c Release` | **PASSED**: 1,071 tests, 0 failed, 0 skipped (Art 49, Core 398, Coordinator 125, Codex 63, Bridge 148, App 288) |
| `check-denylist.ps1` | **PASSED**: 65 production files, 8 narrow allowances, no violations |
| `verify-bridge-aot.ps1` | **FAILED** (exit 1) because of the production timing gate below; every other gate in it passed |

**Native AOT verification detail.**

- Helper size: 1,570,304 bytes (1.50 MB), gate 2 MB: PASS. The previous local build was 1,569,792 bytes.
- Production binary contains none of the five path-override / test-hook strings: PASS.
- Process tests (production-safe and seam): 34 passed.
- Timing gate, test-seam exe, unchanged data, median 29.3 ms against 40 ms: PASS.
- **Timing gate, production exe, resolve and read, median 40.8 ms against 40 ms: FAILED.**
- Cross-check with the Step-0 harness: median 35 ms.
- No Brotli or compression strings (`brotli`, `BrotliDecoder`, `BrotliStream`, `System.IO.Compression`, `clrcompression`, `inflate`, `zlib`), in ASCII or UTF-16, in the rebuilt helper (SHA-256 starts `5cb1e5a5`); this is the same limited byte-level evidence as for rc.1.

**Binary string scan: exact command.** Run once from the repository root (`C:\PatitosFortuneTools\GitHub\quotaduck-app`) in Git Bash with the system `python3`, after `verify-bridge-aot.ps1` had rebuilt the helper. It reads one file, `artifacts/bridge-aot/quotaduck-statusline.exe` (the Native AOT helper from that run, git-ignored), opened in binary read mode only. It writes nothing (no file, no output file, no registry or network access) and does not execute the binary; it prints to the terminal only.

```bash
python3 - <<'EOF'
d=open('artifacts/bridge-aot/quotaduck-statusline.exe','rb').read()
import hashlib;print(len(d),hashlib.sha256(d).hexdigest()[:16])
for s in ['brotli','BrotliDecoder','BrotliStream','System.IO.Compression','clrcompression','inflate','zlib']:
    a=d.lower().count(s.lower().encode()); u=d.lower().count(s.lower().encode('utf-16-le')); print(s,a,u)
EOF
```

Output: size 1,570,304 bytes, SHA-256 prefix `5cb1e5a5947701d6`, and `0 0` (ASCII count, UTF-16 count) for every one of the seven strings. The search lowercases the bytes, so it is case-insensitive for ASCII letters. An earlier attempt in the same session used `grep` with a UTF-16 pattern built through shell substitution; it was invalid (the null bytes were dropped) and is not used as evidence. The command above was not re-run for this write-up.

**OWNER-ACCEPTED EXCEPTION (one-time, for this remediation).** The production timing gate FAILED by 0.8 ms (40.8 ms against 40 ms, 2%). The owner accepted that result as a documented, one-time performance exception. The 40 ms threshold and `scripts/verify-bridge-aot.ps1` were not changed, and the verification suite is not described as passing. Supporting measurement (same production code path, 40 runs per figure, medians in ms, old helper built with 10.0.12 against the new helper built with 10.0.14, interleaved on the same machine): round 1 39.3 against 39.1, round 2 39.3 against 40.0, round 3 40.4 against 39.6. The old helper also exceeded 40 ms in one round, so this does not show a regression, but it also does not explain why the script's own run reached 40.8. These figures come from one machine with PowerShell process-start overhead included.

## Not yet done

- `scripts\release.ps1` has not been run, so a rebuilt release ZIP does not exist and rc.1 remains the only published build.
- No release, tag, signing or publication.
- The helper has not been exercised against a live Claude Code session; its behaviour is covered by the automated process tests above.
- The advisory's exact scope for the Native AOT runtime pack beyond the fields quoted above was not independently re-read in full.
