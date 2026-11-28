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

The gaps are 20px inside and on the outer left, right and bottom. The outer top gap is
54px for the default 38px Zebar bar and a 16px margin. The top gap can be changed separately
from Zebar's height setting. Border colours and `hide_method: cloak` follow the defaults.

### Workspace groups, one per monitor (up to six)

- Each monitor owns nine workspaces: the Windows primary monitor has `11`–`19`, the others follow left to right
  (then top to bottom) with `21`–`29`, `31`–`39`, … up to `61`–`69`. All 54 are declared in the config, because
  GlazeWM refuses to focus a workspace that isn't configured.
- Each group's `bind_to_monitor` uses a YAML anchor (`&group1_monitor` … `&group6_monitor`), and GlazeLauncher's
  workspace manager rewrites the anchor values to GlazeWM's monitor indexes (sorted left to right) and reloads the
  config whenever the monitors change. Groups without a monitor point at the primary. Details and the merge rule for a
  disconnected monitor are in [`launcher.md`](launcher.md#workspace-manager).
- `update-workspace-config` only works on active workspaces, which is why the anchors live in the file.
- History: the first version (10:53) used `10`–`19` / `20`–`29` with a `0` key. At
  11:01 it was cut to nine per monitor, `11`–`19` / `21`–`29`, and the `alt+0` bindings were removed. Until 18:55 the
  anchors were `&primary_monitor` / `&secondary_monitor`, kept in step by `workspace.ps1` on Ctrl+Alt+R only.

### Keybindings (changed or added)

| Keys | Action |
|---|---|
| `alt+1`…`alt+9` | **Not bound in GlazeWM.** GlazeLauncher registers them: focus workspace N of the group of the monitor you're on |
| `alt+shift+1`…`9` | **Not bound in GlazeWM.** GlazeLauncher: move the foreground window to workspace N of its monitor's group and follow it |
| `ctrl+alt+r` | **Not bound in GlazeWM.** GlazeLauncher: re-sync workspaces to monitors (normally automatic) |
| `alt+s` / `alt+a` | Changed to `focus --next/prev-active-workspace-on-monitor` (cycles only on the current display) |
| `alt+space` | **Not bound in GlazeWM.** GlazeLauncher registers it itself (a GlazeWM binding would swallow it) |
| `alt+c` | `wm-cycle-focus` (tiling → floating → fullscreen), moved here from `alt+space` |

The default `focus --workspace N` and `move --workspace N` bindings were removed. `ctrl+alt+1`…`9` (direct `2N`) were removed
on 2026-11-27: with the merge rule, `21`–`29` don't exist without a second monitor. The workspace keys only work while
GlazeLauncher runs; the launcher releases them while GlazeWM isn't running.

### Startup and window rules

`startup_commands` runs `shell-exec zebar`, then `shell-exec %USERPROFILE%/.glzr/launcher/GlazeSupervisor.exe`, which
starts the launcher and restarts it after a crash or hang (see [`launcher.md`](launcher.md)). The launcher syncs the
workspaces when it connects (this replaced `workspace.ps1 -Action Initialize`).

The `ignore` rules cover the GlazeLauncher popup and the exact `MassEffect2` process. The game rule
lets Mass Effect 2 manage its own fullscreen window when launched through Legendary Edition.

### `scripts/workspace.ps1` (no longer used)

Still on disk, but nothing calls it since 2026-11-27, and it expects the old `primary_monitor`/`secondary_monitor`
anchors, so it would fail against the current config. It supported only one or two monitors, picked the group from
GlazeWM's focused monitor (wrong after clicking an empty desktop), and started PowerShell on every key press.

## Known issues

- The workspace keys depend on GlazeLauncher running (see its doc).
- **GlazeWM 3.10.1 leaves windows hidden when it dies.** Hidden workspaces are shell-cloaked; its watcher re-shows the
  windows after a crash or kill but doesn't uncloak them, and a new GlazeWM skips cloaked windows. GlazeLauncher now
  uncloaks them (`docs/launcher.md`, "Rescuing windows after GlazeWM dies"). Without the launcher, close and reopen the app.
  When GlazeWM was killed on 2026-11-28, the watcher itself also crashed during its cleanup (access violation in `combase.dll`).
  Seen at 22:51 on 2026-11-27, when the user turned GlazeWM off: windows on hidden workspaces stayed cloaked.

## Backups

- `.glzr\glazewm\config.yaml.before-bar-height-20261128-123443`: before reducing the top gap.
- `.glzr\glazewm\config.yaml.before-mass-effect-2-20261127-213510`: before adding the game ignore rule.
- `.glzr\glazewm\config.before-monitor-workspaces-20261127-105357.yaml`: default config before the workspace groups
- `.glzr\glazewm\backup-before-nine-workspaces-20261127-110114\`: config + script from the 10–19 / 20–29 version
- `.glzr\glazewm\config.before-launcher-20261127-140419.yaml`: config before the launcher (alt+space was `wm-cycle-focus`)
- `.glzr\glazewm\config.before-workspace-manager-20261127-185520.yaml`: config before the launcher took over the workspace keys and groups 3–6 were added
- `.glzr\glazewm\config.before-supervisor-20261127-192822.yaml`: config before startup switched to GlazeSupervisor

## Change log

Newest first.

### 2026-11-28
- **Reduced the top gap to 54px** in the live `config.yaml` for the slimmer Zebar bar. GlazeWM
  accepted the reload.
- **Mass Effect 2 confirmed** by the user: a fresh launch shows the game. Noted that GlazeWM 3.10.1 leaves hidden
  windows cloaked when it exits (known issues); no config change.

### 2026-11-27
- **Ignored the Mass Effect 2 game window** in the live `config.yaml`. GlazeWM had classified the running
  window as fullscreen, and Windows reported it as visible and uncloaked. The reload succeeded and
  GlazeWM's window list no longer includes the existing game. Verification through a fresh Legendary Edition launch is pending.
- **Startup now runs GlazeSupervisor instead of GlazeLauncher** (backup `config.before-supervisor-20261127-192822.yaml`).
  The reload succeeded.
- **Handed the workspace keys to GlazeLauncher and declared six workspace groups.** Removed the 18 `workspace.ps1` Alt+N /
  Alt+Shift+N bindings, the nine Ctrl+Alt+N bindings and Ctrl+Alt+R, and the `-Action Initialize` startup command. Renamed the
  anchors to `&group1_monitor` / `&group2_monitor` and added `31`–`69` with `&group3_monitor` … `&group6_monitor`.
  Backup `config.before-workspace-manager-20261127-185520.yaml`. The reload succeeded and the launcher's sync accepted the anchors.
- For GlazeLauncher (backup `config.before-launcher-20261127-140419.yaml`): `wm-cycle-focus` moved from `alt+space` to `alt+c`, an `ignore` rule for `GlazeLauncher`, and a startup command.
- Changed to nine workspaces per monitor (`11`–`19` / `21`–`29`), dropped the `0` key.
- Added the monitor-aware workspace groups, `workspace.ps1`, `ctrl+alt+1..9`, `ctrl+alt+r` sync, Initialize at startup, and per-monitor workspace cycling.

### 2026-11-22
- Installed GlazeWM 3.10.1 with a fresh default config and archived the old YASB-era config.
