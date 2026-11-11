# QuotaDuck: Future Improvements Backlog

Status: **Parked / not approved for implementation**  
Recorded: 2026-11-09  
Owner decision: Revisit when time permits; no deadline or release commitment.

## Status after rc.2 (2026-11-11)

`1.0.0-rc.2` was published on 2026-11-11 as an **unsigned prerelease** (https://github.com/PatitosFortune/quotaduck/releases/tag/v1.0.0-rc.2, tag `v1.0.0-rc.2` at `c875db64301b9db967743527c8a5fbb9729ed70b`). `1.0.0-rc.1` remains available and unchanged. Of the four original items below: **1 (Copy usage)** and **2 (visual refinements)** were implemented and shipped in rc.2; **3 (public repository and distribution review)** was only partly addressed (security policy, README privacy/security notes, the Native AOT toolchain remediation) and no full audit was done; **4 (website)** has not been started. The original text below is kept as written.

### Deferred after rc.2 (parked; nothing here is authorized for implementation)

| # | Item | State | Evidence / where recorded |
|---|---|---|---|
| 1 | Flyout flickers and returns to the tray corner when dragged between monitors of different display scale | Open. Pre-existing (also in rc.1); cause not established; one attempted fix did not help and was reverted. | Issue #9; `docs/app/RC2-ACCEPTANCE-AND-MIXED-DPI.md` |
| 2 | Idle CPU with the flyout hidden | Accepted for rc.2, not proof of zero idle CPU. Measured during acceptance (process CPU time sampled over 60 s): about 1.3% of one core (0.11 to 0.17 CPU-seconds per 10 s over 60 s), regular and non-zero; cause not investigated; not compared with rc.1. Smallest next step: one profiler or thread-level sample of a hidden minute, then compare with rc.1. | This page |
| 3 | Accessibility and additional scaling verification | Untested (optional): Narrator or NVDA, high contrast, reduced motion, 150% and 200% scaling, the layout on a 100% external monitor, stale/expired/missing labelling in Copy output, Windows clipboard history, the full Tab order. | `docs/app/RC2-ACCEPTANCE-AND-MIXED-DPI.md`; release notes |
| 4 | Code signing and SmartScreen distribution | rc.1 and rc.2 are unsigned and no signing is committed (README, `SECURITY.md`). Needs an owner decision before any wider distribution. | README "Unsigned build and SmartScreen" |
| 5 | Feedback-driven release planning | No rc.3 and no next-version scope is authorized. Plan only from real feedback and an explicit owner decision. | This page |

Release housekeeping still open: the published `SHA256SUMS.txt` lists a per-file manifest (`QuotaDuck-1.0.0-rc.2-win-x64.files.sha256`) that is not attached to the release (see the publication record in `docs/app/RC2-ACCEPTANCE-AND-MIXED-DPI.md`).

## Purpose

Improve QuotaDuck's usefulness for sharing Claude and Codex usage in text, assess optional visual refinements, review the already-public repository and release posture, and decide how to present the tool on patitosfortune.com.

This document records ideas only. It does not authorize implementation, dependency changes, publishing, merging, or release.

## 1. Copy usage to clipboard (first priority)

Provide a simple, discoverable **Copy** action that exports a readable plain-text snapshot for:

- Codex only;
- Claude only;
- both providers together.

Where reported and available, include:

- session / five-hour usage **remaining** and weekly usage **remaining**;
- corresponding usage percentages if helpful, with unambiguous labels;
- reset times if available;
- **captured at** timestamp (including local timezone or offset);
- **last refreshed / last reported** timestamp **per provider**;
- explicit states for stale, waiting, unavailable, or expired data rather than presenting old data as current.

Keep the output useful for pasting into ChatGPT, Claude, notes, or an issue without screenshots. Do not confuse capture time with provider refresh time. Respect the existing Claude terminal-driven status-line limitation; do not imply QuotaDuck can refresh Claude usage independently or obtain values that have not been reported. Respect the existing Codex refresh constraints. Do not trigger model calls, read credentials, or add telemetry to support copying.

Illustrative output only (not real usage):

```text
QuotaDuck usage snapshot
Captured: 2026-11-09 18:47 +01:00

Codex
Session remaining: 82%
Weekly remaining: 26%
Last refreshed: 18:45 +01:00

Claude
Session remaining: 64%
Weekly remaining: 41%
Last reported: 18:43 +01:00
```

Acceptance ideas: clipboard output is plain text; provider selection is clear; timestamps are distinguishable; missing and stale data are honestly represented; no screenshot needed.

## 2. Optional visual improvements

Review the existing tray flyout for legibility, hierarchy, spacing, pond/gauge clarity, refresh age indicators, and the location/feedback of the Copy action. Keep the distinctive duck/pond identity. Compare alternatives before choosing any redesign. Do not expand scope to a new UI framework by default.

## 3. Public repository and distribution review

The repository is **already public**, and the README describes an unsigned `1.0.0-rc.1` release candidate. Review its suitability for wider public use, without claiming a new audit has been performed:

- source and history hygiene, sensitive paths/data, secrets and generated artifacts;
- license, notices, attribution, third-party dependencies and provenance;
- README accuracy, installation/uninstallation, limitations and accessibility;
- reproducible packaging, release artifacts, checksums, SmartScreen/code-signing messaging, and release readiness;
- security/privacy claims against actual behavior and supported evidence;
- issue reporting, contribution expectations and support boundaries.

Review remotely and statically first. Do not run scripts, build, install, or execute unreviewed code. Record findings and owner decisions before changes.

## 4. Positioning on Patitos Fortune website

Explore whether QuotaDuck belongs in a utilities/tools section, a dedicated product page, or a small project showcase at `patitosfortune.com`. Decide intended audience, product description, screenshots, platform requirements, downloads/source links, support policy and branding. Clearly distinguish a release candidate from a stable signed release. Coordinate with the website's own roadmap rather than changing that repository from this task.

## Suggested order when resumed

1. Review the current UI and data model, then specify the clipboard export.
2. Implement and verify clipboard behavior only after approval.
3. Evaluate optional visual polish.
4. Conduct and document public-use/release audit.
5. Decide website placement and coordinate any separate website changes.

## Boundaries

No due date. No automated work scheduled. No changes to runtime code, release artifacts, website, dependencies or architecture are authorized by this backlog entry. Keep future work in small, reviewable branches and do not merge without owner approval.
