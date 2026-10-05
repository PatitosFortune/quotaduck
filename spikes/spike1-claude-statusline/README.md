# Spike 1 — does the Claude Code status line fire on each surface?

Temporary, project-local test. Nothing is written to `~/.claude/settings.json`.

## Apply (you do this by hand)

Run in PowerShell from the repo root (`<repo-root>` below means your checkout folder, for example `C:\Projects\QuotaDuck`). The template holds a `<repo-root>` placeholder; this fills in the real, forward-slashed path and writes the file UTF-8 without a byte-order mark:

```powershell
$root = (Get-Location).Path
New-Item -ItemType Directory -Force .claude | Out-Null
$json = (Get-Content spikes\spike1-claude-statusline\settings.local.json.template -Raw).Replace('<repo-root>', $root.Replace('\', '/'))
[IO.File]::WriteAllText("$root\.claude\settings.local.json", $json, (New-Object Text.UTF8Encoding($false)))
```

That creates `.claude\settings.local.json` (git-ignored) containing only (shown here for a checkout at `C:\Projects\QuotaDuck`):

```json
{
  "statusLine": {
    "type": "command",
    "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:/Projects/QuotaDuck/spikes/spike1-claude-statusline/probe.ps1\""
  }
}
```

It only affects Claude Code sessions whose working folder is `quotaduck-app`.

## Test (one short message per surface, working folder = `quotaduck-app`)

1. **Terminal CLI**: `cd <repo-root>; claude` → accept the folder-trust prompt → send `hi` → wait for the reply → `/exit`.
2. **VS Code**: open the folder `quotaduck-app` → open the Claude Code panel (extension `anthropic.claude-code`) → send `hi`.
3. **Claude desktop app → Code tab**: new session with folder `quotaduck-app` → send `hi`.

(Each `hi` is a normal, tiny message of yours; `rate_limits` is only delivered after a real API response.)
The probe appends one line per status-line run to `%TEMP%\qd-spike1\events.jsonl`, recording only presence flags and surface markers — no values, paths, or session ids.

## Remove (mandatory)

```powershell
Remove-Item .claude\settings.local.json
if (-not (Get-ChildItem .claude -Force)) { Remove-Item .claude }
```

Then tell Claude "removed"; it will verify the file is gone and that the user-level settings were not touched (key-existence check only).
