# QuotaDuck: Future Improvements Backlog

Status: **Parked / not approved for implementation**  
Recorded: 2026-11-09  
Owner decision: Revisit when time permits; no deadline or release commitment.

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
