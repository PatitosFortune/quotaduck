# Security policy

QuotaDuck is a small, personal-project Windows tray utility. Security reports are welcome and are handled on a best-effort basis: there is no service-level agreement, no bug bounty, and replies may take a while.

## Supported versions

Only the latest source on `main` and the latest published release (currently the `1.0.0-rc.1` release candidate, which is unsigned) are looked at. Older builds are not patched; the fix is a newer release.

## How to report a vulnerability

Please **do not put vulnerability details in a public issue, pull request or discussion.**

1. **Preferred:** open the repository's **Security** tab. If it offers **Report a vulnerability**, use it; that creates a private report visible only to the maintainer. (This page does not assume that option is enabled. If the button is missing or the form does not open, use step 2.)
2. **Fallback:** open a public issue titled `Security contact request` that contains **no technical details** (no exploit steps, no sample files, no paths), only a request for a private way to send them. The maintainer will answer in the issue and arrange a private channel.

There is no dedicated security email address. Please do not guess one.

A useful report says what you did, what you expected, what happened, the QuotaDuck version (the release tag or the commit), and your Windows version. Please do not include real Claude or Codex credentials, tokens, cookies, account identifiers or files from your own profile; synthetic examples are enough.

## What is in scope

QuotaDuck's own code and packaging, for example:

- the Claude status-line helper `quotaduck-statusline.exe` (what it reads from stdin, and the small state file it writes under `%LOCALAPPDATA%\QuotaDuck`);
- how the tray app finds, validates and starts the Codex program for the quota read;
- the clipboard report, and anything that could make QuotaDuck read, store or send data it is documented not to (credentials, cookies, browser storage, prompts, account identity);
- the build and release scripts and the contents of the release ZIP.

## What is out of scope

- Vulnerabilities in Claude, Claude Code, Codex, Windows, or the .NET runtime itself. Please report those to their vendors. (QuotaDuck relies on a patched .NET Desktop Runtime installed by the user; see the README.)
- Findings that need an attacker who can already run code or write files as your Windows user.
- Missing code signing on the release candidate. That is documented in the README and release notes.
- Automated-scanner output without a demonstrated impact on QuotaDuck.

A known build-toolchain advisory that was assessed as not reachable from the helper is written up in [`docs/security/NU1903-ILCOMPILER-BROTLI.md`](docs/security/NU1903-ILCOMPILER-BROTLI.md); you do not need to report it again.

## What to expect

The maintainer will try to acknowledge a report, say whether it is accepted, and, for confirmed issues, publish a fix and credit you if you want to be credited. Please allow reasonable time before public disclosure.
