# Agent instructions: GlazeWM + Zebar + GlazeLauncher

## Before you start
`PROJECT_STATE.md` is the overview: goal, rules, paths and shared gotchas. Read it first, then read only
the doc for the part you're working on (the table in `PROJECT_STATE.md` says which):
`docs/glazewm.md`, `docs/zebar.md`, `docs/zebar-collector.md` or `docs/launcher.md`.
For new work or a new idea or bug from the user, check `docs/backlog.md`.
Don't redo or undo work these docs describe unless asked.

## While you work
- The live configs are outside this folder, in `%USERPROFILE%\.glzr\glazewm\` and
  `%USERPROFILE%\.glzr\zebar\`. Before editing one, copy it next to itself with a
  `.before-<change>-<yyyyMMdd-HHmmss>` suffix.
- Performance comes first: the widgets use Solid with canvas graphs, and all sampling runs in the shared Rust
  collector. Don't add React, chart libraries, or polling loops inside widgets.
- GlazeWM command strings from the bar must not quote arguments (`focus --workspace 12`, not `"12"`).
- Widget source is in `zebar-cpu/`. Build there, then deploy to `%USERPROFILE%\.glzr\zebar\cpu-activity\`
  (steps in `docs/zebar.md`). Launcher build and deploy steps are in `docs/launcher.md`.
- Keep a session to one backlog item or a small related group.

## When you finish a change (required)
In the same session:
1. In the area doc: add a dated entry at the top of its **Change log** (what changed, which files, anything
   left unfinished), update the section that describes the current state, and update its known issues and backups.
2. In `docs/backlog.md`: update the item's status, and add any new ideas or bugs you found.
3. Change `PROJECT_STATE.md` only for things every session needs (a new rule, path, area doc, or a
   cross-cutting change). Keep it short.
