# Hyprland-style Windows desktop: GlazeWM + Zebar + GlazeLauncher

Shared state for anyone (person or agent) working in this folder. This file is loaded into every
agent session, so it holds only the overview, the rules and the gotchas that apply everywhere.
Details for each part live in `docs/` and are read only when you work on that part.

Last updated: 2026-11-27

## Goal

A Hyprland-like tiling desktop on Windows 11: GlazeWM tiles the windows, a
custom Zebar top bar shows workspaces (with app icons), drive free space, CPU, RAM
and GPU graphs, a clock with a timer, and system status, and a custom workspace-aware launcher
(**GlazeLauncher**, WPF / .NET 10) opens on Alt+Space.

## Which doc to read

| Working on | Read |
|---|---|
| GlazeWM config, keybindings, `workspace.ps1` | [`docs/glazewm.md`](docs/glazewm.md) |
| The Zebar bar or any popup (CPU, RAM, GPU, storage, clock, settings), build and deploy | [`docs/zebar.md`](docs/zebar.md) |
| The Rust collector: sampling, endpoints, settings and timer storage | [`docs/zebar-collector.md`](docs/zebar-collector.md) (plus `zebar.md`) |
| GlazeLauncher | [`docs/launcher.md`](docs/launcher.md) |
| Picking new work, or an idea or bug report from the user | [`docs/backlog.md`](docs/backlog.md) |

Each area doc has its current state, known issues, backups and its own change log.

## Rules that apply everywhere

- **Performance comes first.** The widgets use **Solid**, not React, on purpose: no virtual DOM,
  fine-grained signals. Graphs draw to `<canvas>`. All sampling happens in one small native Rust
  collector shared by every bar, not in the webviews. No React or other heavy frameworks, no chart
  libraries, no per-widget polling loops that duplicate the collector. The launcher follows the same
  idea: resident, pushed updates from GlazeWM instead of polling, shell work on one background thread.
- **GlazeWM command strings must not quote arguments.** GlazeWM keeps the quotes as part of the
  value, so `focus --workspace "12"` looks for a workspace named `"12"`. Write `focus --workspace 12`.
- **Back up live files before editing.** The live configs are outside this folder and not in Git
  (`%USERPROFILE%\.glzr\…`). Copy the file next to itself with a `.before-<change>-<yyyyMMdd-HHmmss>`
  suffix, or move an installed folder aside the same way. Each area doc lists its backups.
- **Child processes must not inherit handles.** Zebar's `shellSpawn` and GlazeWM's `shell-exec` start
  children with handle inheritance on. A resident child then keeps the parent's sockets alive (this
  once broke Zebar restarts). The collector and the launcher both relaunch themselves detached on start;
  anything new that stays resident must do the same.

## Where things live

| What | Path | Notes |
|---|---|---|
| GlazeWM config (live) | `%USERPROFILE%\.glzr\glazewm\config.yaml` | Edited in place; GlazeWM reads it from here |
| Workspace helper script | `%USERPROFILE%\.glzr\glazewm\scripts\workspace.ps1` | No longer used (the launcher's workspace manager replaced it) |
| Zebar settings | `%USERPROFILE%\.glzr\zebar\settings.json` | Picks which widget pack starts |
| Installed widget pack | `%USERPROFILE%\.glzr\zebar\cpu-activity\` | Built output + collector exe; Zebar loads from here |
| Widget settings | `%USERPROFILE%\.glzr\zebar\cpu-activity\settings.json` | Written by the collector when Settings changes; missing = defaults |
| Timer state | `%USERPROFILE%\.glzr\zebar\cpu-activity\timer.json` | Collector-owned active deadline, duration, and alarm timestamp |
| Widget pack source | `C:\codex\glazewm\zebar-cpu\` | Solid + TypeScript + Vite with a Rust collector in `native/` |
| GlazeWM installer | `C:\codex\glazewm\glazewm-v3.10.1.exe` | Version in use: GlazeWM 3.10.1 (not in Git) |
| Launcher source | `C:\codex\glazewm\launcher\` | WPF, .NET 10 (`GlazeLauncher.csproj`) |
| Installed launcher | `%USERPROFILE%\.glzr\launcher\` | Published output; GlazeWM starts it from here |
| Launcher data | `%LOCALAPPDATA%\GlazeLauncher\` | `launcher.log`, `usage.json` (frecency), `workspace-merges.json`, `snapshot.png` (debug) |

Versions: GlazeWM 3.10.1, Zebar 3.3.1 (npm `zebar@3.3.1`), `glazewm@1.7.0` JS client, Solid 1.9, Vite 7, .NET SDK 10.0.115 (desktop runtime 10.0.15).

Startup order (from GlazeWM's `startup_commands`): Zebar, then GlazeLauncher. Zebar's bar starts the collector itself
when its feed fails.

**Workspaces follow the monitors.** Each monitor owns nine workspaces (primary `11`–`19`, then `21`–`29` … left to right,
up to six monitors). GlazeLauncher owns Alt+1…9, Alt+Shift+1…9 and Ctrl+Alt+R and keeps GlazeWM's config in step when
monitors change, so those keys stop working if the launcher is down. Details: `docs/launcher.md` (Workspace manager).

## Repository

The `launcher/` and `zebar-cpu/` sources and these docs share one Git repository rooted at `C:\codex\glazewm`.
The public GitHub remote is [`mihaelfi/glazewm-additions`](https://github.com/mihaelfi/glazewm-additions), with `main` as the primary branch.
It is public, so keep personal paths and data out of new commits where possible.

## Gotchas for agents

- **MSIX redirection.** The Claude desktop app is an MSIX package. Processes started from its shell write
  `%LOCALAPPDATA%` into `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\…`, and reads from that
  shell see the redirected copy. Something you start from the shell is not the instance GlazeWM started.
- **Popups close on blur.** Zebar popups and the launcher close when they lose focus. Desktop-automation tools that
  move focus between steps close them too; that's not a bug.
- **Screenshots can't see everything.** The computer-use tool can't target the launcher popup or the Zebar bar windows.
  Use the launcher's `snapshot` pipe command, a browser preview of the widget HTML, or ask the user.

## Cross-cutting change log

Area changes are logged in each area doc. Only changes that span the project go here. Newest first.

### 2026-11-27
- **Moved the workspace keys and monitor handling from GlazeWM's config and `workspace.ps1` into GlazeLauncher.** See the
  2026-11-27 entries in `docs/launcher.md` and `docs/glazewm.md`.
- **Split this file into area docs and a backlog.** `PROJECT_STATE.md` now holds only the overview, the rules and
  shared gotchas. Details moved without loss to `docs/glazewm.md`, `docs/zebar.md`, `docs/zebar-collector.md` and
  `docs/launcher.md`, each with its own change log and backups. New `docs/backlog.md` holds feature ideas and bugs with
  a status, including the user's new launcher, GlazeWM, disk and network ideas. `AGENTS.md` says which doc to read and update.
- **Made the GitHub repository public.** Published `main` under `mihaelfi/glazewm-additions` with the GitHub noreply email in the commit history.
- **Published the source repository to GitHub.** Created `mihaelfi/glazewm-additions` (private at first) and set it as `origin` for `main`.
  The repository name distinguishes this launcher and widget pack from GlazeWM itself.
- **Started tracking GlazeLauncher and the Zebar widget pack in Git.** Both sources, dependency lockfiles, and project notes are tracked
  together on `main`. The root `.gitignore` excludes the GlazeWM installer and launcher build outputs. The widget `.gitignore`
  excludes widget build outputs and dependency caches.
- Wrote this file plus `AGENTS.md` / `CLAUDE.md`, so new sessions load this context.

## How to update the docs

- When you finish a change, update the area doc: its current-state section (so it describes the present, not only
  history), its known issues, its backups, and a dated entry at the top of its change log.
- Update `docs/backlog.md`: change the item's status, or add new ideas and bugs.
- Change this file only for things every session needs: a new rule, a new path, a new area doc, a cross-cutting change.
  Keep it short; it's loaded every time.
- Don't paste whole config files into the docs. Point to the path and describe what is special about it.
