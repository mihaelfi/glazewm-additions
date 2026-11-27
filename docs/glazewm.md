# GlazeWM configuration

Read this when you touch the GlazeWM config, keybindings or `workspace.ps1`.
General rules and paths are in [`PROJECT_STATE.md`](../PROJECT_STATE.md); ideas and bugs are in [`backlog.md`](backlog.md).

Live files (not in Git): `%USERPROFILE%\.glzr\glazewm\config.yaml` and
`%USERPROFILE%\.glzr\glazewm\scripts\workspace.ps1`. Back up before editing (see PROJECT_STATE).

## Current state

Starting point: a fresh default config for 3.10.1, created 2026-11-22. The older
May 2026 setup, which used YASB instead of Zebar, was archived to
`%USERPROFILE%\.glzr\glazewm-backup-20261122-193910\` (it has its own `CHANGELOG.md`).
It is not used now.

Unchanged from the defaults: gaps (inner 20px; outer 60px top and 20px on the other
sides, which leaves room for the 44px bar), border colours, `hide_method: cloak`,
and window rules (Zebar windows are ignored by default).

### Two workspace groups, one per monitor

- Workspaces `11`–`19` are bound to the primary monitor and `21`–`29` to the secondary one.
- The bindings use YAML anchors so the script can swap them:
  `bind_to_monitor: &primary_monitor 1` and `bind_to_monitor: &secondary_monitor 0`.
- History: the first version (10:53) used `10`–`19` / `20`–`29` with a `0` key. At
  11:01 it was cut to nine per monitor, `11`–`19` / `21`–`29`, and the `alt+0` bindings were removed.

### Keybindings (changed or added)

| Keys | Action |
|---|---|
| `alt+1`…`alt+9` | `workspace.ps1 -Action Focus -Digit N`: focus workspace `1N` on the primary monitor, or `2N` if the secondary monitor has focus |
| `alt+shift+1`…`9` | `workspace.ps1 -Action Move -Digit N`: move the focused window to that workspace, then focus it |
| `ctrl+alt+1`…`9` | `focus --workspace 2N` directly, to reach the secondary group when undocked |
| `ctrl+alt+r` | `workspace.ps1 -Action Sync`: re-read the display layout and put workspaces back on the right monitors (shows a message box on error) |
| `alt+s` / `alt+a` | Changed to `focus --next/prev-active-workspace-on-monitor` (cycles only on the current display) |
| `alt+space` | **Not bound in GlazeWM.** GlazeLauncher registers it itself (a GlazeWM binding would swallow it) |
| `alt+c` | `wm-cycle-focus` (tiling → floating → fullscreen), moved here from `alt+space` |

The default `focus --workspace N` and `move --workspace N` bindings were replaced by the script calls above.
All script calls use `shell-exec --hide-window powershell.exe -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File ...`.

### Startup and window rules

`startup_commands` runs `shell-exec zebar`, then `workspace.ps1 -Action Initialize`, then
`shell-exec %USERPROFILE%/.glzr/launcher/GlazeLauncher.exe`.

Window rules: an extra `ignore` rule for `window_process: GlazeLauncher`, so GlazeWM never tiles the launcher popup.

### `scripts/workspace.ps1`

- Actions: `Focus`, `Move`, `Sync`, `Initialize`; `-Digit` takes 1–9.
- Picks base 10 or 20 by checking whether the focused GlazeWM monitor is the Windows
  primary screen (`System.Windows.Forms.Screen`). With only one monitor it always uses base 10.
- `Sync`/`Initialize` with two monitors: rewrites the `primary_monitor`/`secondary_monitor`
  anchor indexes in `config.yaml` (atomic replace, with rollback if `wm-reload-config`
  fails), then moves any workspace on the wrong monitor with `move-workspace --direction`,
  which keeps its windows and layout. The two monitors must share an edge.
- `Initialize` with one monitor: if GlazeWM started that monitor on workspace `21`, it
  renames it to `11` and binds it to monitor 1.
- Requires Windows and GlazeWM to agree on 1 or 2 displays; otherwise it throws ("wait after docking, then Ctrl+Alt+R").

## Known issues

- Alt+N after clicking an empty monitor goes to the previously focused monitor's group. Tracked in [`backlog.md`](backlog.md#glazewm).

## Backups

- `.glzr\glazewm\config.before-monitor-workspaces-20261127-105357.yaml`: default config before the workspace groups
- `.glzr\glazewm\backup-before-nine-workspaces-20261127-110114\`: config + script from the 10–19 / 20–29 version
- `.glzr\glazewm\config.before-launcher-20261127-140419.yaml`: config before the launcher (alt+space was `wm-cycle-focus`)

## Change log

Newest first.

### 2026-11-27
- For GlazeLauncher (backup `config.before-launcher-20261127-140419.yaml`): `wm-cycle-focus` moved from `alt+space` to `alt+c`, an `ignore` rule for `GlazeLauncher`, and a startup command.
- Changed to nine workspaces per monitor (`11`–`19` / `21`–`29`), dropped the `0` key.
- Added the monitor-aware workspace groups, `workspace.ps1`, `ctrl+alt+1..9`, `ctrl+alt+r` sync, Initialize at startup, and per-monitor workspace cycling.

### 2026-11-22
- Installed GlazeWM 3.10.1 with a fresh default config and archived the old YASB-era config.
