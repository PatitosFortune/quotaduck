# NU1903: Microsoft.DotNet.ILCompiler 10.0.12 (GHSA-p3jp-9v83-76w9)

Recorded 2026-11-09 while finishing the Copy-usage PR. **Assessment only: no dependency, SDK or build change was made.**

## What the build reports

`scripts\dotnet.ps1 build/test` prints, for `src/QuotaDuck.Bridge`:

```
warning NU1903: Package 'Microsoft.DotNet.ILCompiler' 10.0.12 has a known high severity vulnerability, https://github.com/advisories/GHSA-p3jp-9v83-76w9
```

It is non-fatal because `Directory.Build.props` lists NU1901-NU1904 in `WarningsNotAsErrors`. It affects only the Native AOT helper (`quotaduck-statusline.exe`); the WPF app is framework-dependent and does not use ILCompiler.

## The advisory (UNVERIFIED summary)

Source: GitHub advisory page, read through a delegated fetch that was **truncated and partly model-summarised**. Treat the following as leads to confirm on the advisory itself, not as verified fact:

- Title: ".NET Brotli Decoder Out-of-bounds Write Vulnerability", High (CVSS 3.1 score 8.1, CWE-787), published 2026-10-25.
- Affected: `Microsoft.DotNet.ILCompiler` `>= 10.0.0, < 10.0.14` (patched 10.0.14). Also listed: `Microsoft.NETCore.App.Runtime.NativeAOT.win-x64` `>= 10.0.0, < 10.0.14`.
- Exposure needs an app to **decompress attacker-controlled Brotli data** (`BrotliStream`, `BrotliDecoder`, or HTTP/ASP.NET automatic Brotli decompression). "An application is not exposed merely because an affected .NET package is installed."
- Not confirmed: whether `runtime.win-x64.microsoft.dotnet.ilcompiler` (also in our package cache at 10.0.12) is listed.

The owner has stated that 10.0.14 is available; I did not verify availability.

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

## Recommendation (not done; needs owner approval, separate PR)

1. Confirm the advisory text and patched versions directly.
2. The ILCompiler and NativeAOT runtime-pack versions come from the SDK pinned in `global.json` (10.0.401 with `rollForward: latestPatch`) and the repo-local `.tools\dotnet`. The likely fix is moving to a patched SDK (the advisory summary mentions 10.0.403, unverified) rather than editing a package version, then regenerating the helper.
3. Rebuild and re-verify the helper (`scripts\verify-bridge-aot.ps1`, `scripts\release.ps1`) before any **stable** release; rc.1's helper was built with 10.0.12. Do not alter the published rc.1 artifacts.
4. Severity for the Phase 3 audit: **Important** (supply-chain hygiene and trust, since the build log shows a high-severity warning), not Critical, given no reachable Brotli path.
