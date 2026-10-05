# spikes/ — throw-away code

Everything here exists only to answer a Step-0 question. **Nothing here is production code**, nothing in `src/` may reference it, and it may be deleted wholesale. Promote a piece only after review, by re-writing it under `src/`.

| Folder | Spike |
|---|---|
| `spike1-claude-statusline/` | Does the Claude Code status-line hook fire on each surface? (probe script only; user applies the temporary config) |
| `spike2-process-launch/` | .NET `Process.Start` executable-resolution behaviour (harmless scratch exes only) |
| `spike3-codex-env/` | Minimal child environment for the signed MSIX `codex app-server` quota-only handshake |
| `spike4-footprint/` | WPF + Native AOT bridge prototypes for real size/start/memory numbers |
| `spike5-msix-discovery/` | Package-API discovery + validation routine for the Codex MSIX |
