# Agent instructions: GlazeWM + Zebar setup

## Before you start
Read `PROJECT_STATE.md` in this folder first. It records what has already been built
(GlazeWM config, workspace script, the Zebar `cpu-activity` widget pack), where the
live files are, and what is still open. Don't redo or undo work it describes unless asked.

## While you work
- The live configs are outside this folder, in `%USERPROFILE%\.glzr\glazewm\` and
  `%USERPROFILE%\.glzr\zebar\`. Before editing one, copy it next to itself with a
  `.before-<change>-<yyyyMMdd-HHmmss>` suffix.
- Performance comes first: the widgets use Solid with canvas graphs, and all sampling runs in the shared Rust
  collector. Don't add React, chart libraries, or polling loops inside widgets.
- GlazeWM command strings from the bar must not quote arguments (`focus --workspace 12`, not `"12"`).
- Widget source is in `zebar-cpu/`. Build there, then deploy to `%USERPROFILE%\.glzr\zebar\cpu-activity\`
  (steps in `PROJECT_STATE.md`).

## When you finish a change (required)
Update `PROJECT_STATE.md` in the same session:
1. Add a dated entry at the top of **Change log**: what changed, which files, anything left unfinished.
2. Update the matching section so it describes the current state.
3. Add or remove items under **Open items** as needed.
