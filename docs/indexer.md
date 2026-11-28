# File index (GlazeIndex): Everything-style search and folder sizes

Status: **running as a Windows service** (2026-11-28). GlazeIndex knows every file and folder on the NTFS drives you turn
on, kept current from the NTFS change journal, with every folder's size and file count, and serves:

- **GlazeLauncher:** file and folder results (`tooltips`, `dragon tooltips` → `D:\Dragon Age Mods\Detailed Tooltips-248`,
  filters such as `*.zip` or `in:D:\Mods`), then Open, Terminal / PowerShell / cmd here, Open file location, Copy path.
  See `docs/launcher.md`.
- **Zebar storage popup (backend ready, view not built):** the space map (`children`), largest files, space by file type.
- **Settings UIs to come** (widget or launcher): drives on/off, excluded folders, demoted names, all through the pipe.

Backlog items: "File and folder index, Everything-style" (Launcher) and "Space map, TreeSize-style" (Zebar: storage).

## Current state

| What | Where |
|---|---|
| Source | `indexer/` (Rust; plain Win32 calls, `serde`/`serde_json` for settings and requests). `src/main.rs` → `GlazeIndex.exe`; `src/probe.rs` → `index-probe.exe` (the measuring prototype, standalone) |
| Settings | `%ProgramData%\GlazeIndex\settings.json` (owned by the service; change it through the pipe, see Settings) |
| Installed service | `C:\Program Files\GlazeIndex\GlazeIndex.exe --service`, service `GlazeIndex`, LocalSystem, automatic (delayed) start |
| Log | `%ProgramData%\GlazeIndex\index.log` (users can read it; rotated at 1 MB to `index.log.old`) |
| Snapshots | `%ProgramData%\GlazeIndex\data\vol-<serial>.glix` (administrators and SYSTEM only: they list every file name) |
| Pipe | `\\.\pipe\GlazeIndex` (SYSTEM, administrators, interactive users; remote clients refused) |

```powershell
cd C:\codex\glazewm\indexer
cargo build --release --offline
# admin terminal: install or update (stops the old service, which saves its snapshots, copies, starts)
.\target\release\GlazeIndex.exe --install
# any terminal (asks the running service; prints JSON):
GlazeIndex.exe --status | --settings | --enable D: | --disable D: | --exclude D:\Private | --unexclude D:\Private | --rescan D:
GlazeIndex.exe --search dragon tooltips | --children D:\ | --largest C:\ | --types E:\ | --recent
GlazeIndex.exe --request '{"op":"children","path":"D:\\","sort":"name"}'
# admin: --console runs it in the terminal instead (Enter stops); --uninstall removes service, program and data
```

An agent can't install or update it (no admin); ask the user to run `--install` **from the build folder**
(`C:\codex\glazewm\indexer\target\release\GlazeIndex.exe --install`). Run from the installed copy it refuses, because it
would only restart the old version. If the old service doesn't stop within 20 s, `--install` ends its process (the next
start loads the last snapshot and catches up from the journal). `--install` prints timestamped lines naming the build it
installs and the one it replaces (by file time), and the log's start line names the running build
(`starting GlazeIndex 0.1.0, build of 2026-11-28 11:28`). `indexer/out/` and `indexer/target/` are
git-ignored; probe reports list real file paths and must stay out of Git.

**How it runs** (`engine.rs`): at start it reads the settings and finds the drives (`volumes.rs`: fixed and removable,
keyed by volume serial), then runs one worker per drive that is turned on, present and NTFS. Changing the drive settings
starts or stops workers on the fly (stopping cancels the blocked journal read; turning a drive off drops its index and
snapshot). Each worker:

1. Opens the volume and gets its journal, creating one if missing (`ntfs::ensure_journal`, logged).
2. Loads the snapshot if it was saved against the same journal ID and its position is still in the journal; otherwise scans
   the MFT (`ntfs::scan`, in background I/O mode), saves a snapshot, and replays the changes made during the scan.
3. Follows the journal: blocking reads of up to 2 s, a batch at a time. Closed files whose content or name changed get their
   size and time from `OpenFileById` (with SeBackupPrivilege), once per file per batch. Changes apply under the volume's
   write lock (`Index::apply`: create, rename/move with size moved between folders, delete, size delta up the parent chain).
4. Every 10 minutes with changes, and on stop, compacts when a tenth is dead and saves the snapshot (written to `.tmp`, then
   renamed). A lost position (journal wrapped or deleted) logs and rescans; other failures retry after 5 s, 30 s, then 5 min.

**Search** (`search.rs`): needs 3 letters unless a filter narrows it (`*.iso` alone lists every ISO, biggest first). Filters,
as request fields or inside the query text: `*.zip` / `ext:zip,rar`, `file:` / `folder:`, `in:D:\Mods` (`in:"D:\My Mods"`),
`sort:size` / `sort:modified`. Every word must be in the name or a folder above it, at least one in the name.
Score: exact name 1000, name start 800, word start 600, elsewhere 400; +100 when all words are in the name; folders +100;
noisy places −350 (`node_modules`, `.git`, `AppData`, `__pycache__`, `site-packages`, `.cargo`, `.nuget`, `.npm`, … anywhere;
`Windows`, `ProgramData`, `Recovery`, … at the root; inherited by everything below); hidden/system −200; minus half the
name length and 4 per folder level. Parallel over all CPU threads. Returns the top N with path, name, folder flag, size
(folder total), modified time (Unix ms) and the matched word's position for highlighting.

## Settings

`settings.json` (`settings.rs`), written by the service. Any logged-in user may change it through the pipe (the user's
choice, 2026-11-28: the launcher and widget run without admin); each change is logged with the Windows user and program
(`settings changed by PC\user via GlazeIndex.exe (pid N): excluded + D:\Private`).

| Key | Meaning | Default |
|---|---|---|
| `drives` | `[{serial, label, enabled}]`: drives to index, by volume serial (the label is the last-seen letter) | none: **opt-in**, the user's choice. An upgrade keeps the drives that had snapshots |
| `excluded` | Folders whose contents never show in search or the space map; their size still counts in their parents | `[]` |
| `demoted` | Folder names that push everything under them down in search, anywhere | `node_modules`, `.git`, `AppData`, `__pycache__`, `site-packages`, `.cargo`, `.nuget`, `.npm`, … |
| `demoted_at_root` | The same, only directly under a drive root | `Windows`, `ProgramData`, `Recovery`, `$WINDOWS.~BT`, … |
| `search_hidden` | Hidden and system entries show in search (pushed down) | `true` |
| `create_journals` | Turn on a missing USN journal (32 MB) | `true` |

Changing exclusions or demoted names re-applies them to every index (about 90 ms for C); changing drives starts or stops
them. A broken file is logged and left alone (defaults are used until it's fixed).

## Requests

One JSON object per line on `\\.\pipe\GlazeIndex`, one JSON reply (`"ok": true` or `"ok": false, "error"`), then the server
closes the connection (`api.rs`, `pipe.rs`). Items (files and folders) look like
`{"path","name","folder","size","files","modified","hidden","noisy","excluded"}`: `size` and `files` are a folder's totals,
`modified` is Unix ms. Paths are case-insensitive; `D:` or `D:\` is a drive's root.

| `op` | Fields | Reply |
|---|---|---|
| `ping` | | `version` |
| `status` | | `version`, `build`, `private_memory`, `min_query`, `settings_file`, `drives`: every fixed or removable drive with `drive`, `label`, `serial`, `file_system`, `usb`, `removable`, `total`, `free`, `enabled`, `supported`, `reason`, `state` (`ready`, `scanning`, `off`, `error: …`, `not connected`), `entries`, `memory` |
| `settings` | | `settings` |
| `settings.set` | `settings`: the keys to replace (lists replaced whole) | the new `settings` |
| `drive` | `drive` (`"D:"`) or `serial`, `enabled` | the new `settings` |
| `exclude` | `path`, `excluded` (default true) | the new `settings` |
| `rescan` | `drive` | |
| `search` | `query`, `limit` (20), `kind` (`file`/`folder`), `ext` (`["zip"]`), `in` (a folder), `sort` (`score`/`size`/`modified`), `hidden` | `query`, `matches`, `ms`, `ready`, `results`: items plus `score` and `highlight` `[start, length]` |
| `children` | `path`, `sort` (`size`/`name`/`modified`), `limit` (200), `offset` | `folder` (item), `count`, `offset`, `items` |
| `largest` | `path` (optional: all drives), `limit` (50) | `items`: the biggest files |
| `types` | `path` (optional), `limit` (50) | `size`, `files`, `types` (count), `items`: `{ext, size, files}` biggest first (`ext` "" = no extension) |
| `recent` | `path` (optional), `limit` (50), `since` (Unix ms), `noisy` (include noisy/hidden) | `items`: newest first; future dates are skipped |
| `stat` | `path` | `item` |

The older text lines `search<TAB>limit<TAB>text` (GlazeLauncher's) and `status` still work; the search line returns the same
reply as `search`, so typed filters work in the launcher too. Timings on this PC: `children D:\`, `largest C:\`, `types E:\`
and `recent` 12–18 ms including starting the CLI; searches 1–16 ms.

**Security**: the service binary lives in Program Files because a SYSTEM service in a user-writable folder could be replaced
by any program the user runs. `--install` creates `%ProgramData%\GlazeIndex` fresh with a protected ACL (SYSTEM and
administrators full, users read; `data\` without users) so nobody can plant links there first.

**First run on this PC (2026-11-28):** created journals on D and E; scanned D in 0.6 s, E in 0.9 s, C (2.52 M entries) in
3.5 s; snapshots 31, 37 and 131 MB. Live tracking checked through the pipe: a new folder appeared, a 5 MB file raised the
folder's total, a rename kept the size, a delete removed both, each within a second.

## How Everything-style indexing works

1. **First index: read the MFT.** Opening `\\.\C:` (admin) and reading the `$MFT` file sequentially gives every file's name,
   parent, size, times and attributes in one pass, without walking folders. Records need the update-sequence fix-up;
   names come from `$FILE_NAME` (Win32 name preferred over the DOS short name), sizes from the unnamed `$DATA` attribute,
   extension records (base reference set) are merged into their base record.
2. **Changes: the USN change journal.** NTFS logs every create, delete, rename and write. A reader waits on
   `FSCTL_READ_USN_JOURNAL` (blocking, no polling) and remembers the last USN, so after a restart it replays only what
   changed. If the journal wrapped past that point (or its ID changed), the volume is rescanned.
3. **The index lives in memory** in flat arrays (packed names buffer, parent numbers, sizes, flags); a search is a parallel
   scan over all names. A snapshot file makes startup fast.

## Measurements (2026-11-28, this PC: 24 threads, NVMe drives)

| | C: | D: | E: |
|---|---|---|---|
| Used | 2.78 TB | 1.19 TB | 2.52 TB |
| MFT size | 2.5 GB | 0.9 GB | 0.8 GB |
| Entries (files + folders) | 2.52 M (2.05 M + 0.47 M) | 0.58 M | 0.72 M |
| MFT read + parse | 3.8 s (reads 2.5 s, 1.0 GB/s) | 1.2 s | 1.0 s |
| `FSCTL_ENUM_USN_DATA` (names only) | 7.2 s | – | – |
| Folder totals | 22 ms | 3 ms | 4 ms |
| Index memory | 127 MB (53 B/entry) | 32 MB | 36 MB |
| Snapshot save / load | 127 MB, 76 / 50 ms | 32 MB, 21 / 12 ms | 36 MB, 23 / 14 ms |
| USN journal | 32 MB max, oldest record ~12 h back | **not active** | **not active** |

- **Correctness:** the MFT read found the same entries as Windows' own enumeration (2,516,617 vs 2,516,576 on C), no unreadable
  records, and file sizes add up to the drive's used space (100%) on all three. A handful of orphans (3–15 per drive) attach
  to the root.
- **All three drives:** 3.8 M entries, 194 MB of index, 196 MB process private memory (peak 305 MB while scanning).
  Names average 23–26 bytes; the rest is about 28 bytes per entry (parent, size, modified time, flags, record map).
- **Search over all 3.8 M names takes 10–11 ms** (24 threads, lowercasing on the fly), even for a one-letter query with
  2.2 M matches. A pre-lowercased copy saves 3–4 ms and costs 101 MB: **not worth it.**
- **Loose subsequence matching is noise at this scale:** "tooltips" has 139 substring matches but 2,738 subsequence matches;
  "readme" 9,049 vs 64,154. File results need contiguous (substring / word-start) matching only.
- **Ranking needs more than name quality:** for "tooltips", exact folder names ranked first (good), but `node_modules`
  files came before mod folders like "Detailed Tooltips". Needs path penalties (`node_modules`, `AppData`, caches, `Windows`,
  hidden/system), folders before files, frecency of opened results, and a minimum query length (no files for 1–2 letters).
- **`FSCTL_ENUM_USN_DATA` is slower than reading the MFT (7.2 s vs 3.8 s) and has no sizes**, so the MFT read is the way.
- **Journal watching works:** 1,006 records in 20 s on C (about 50/s, mostly browser, Claude and temp files), each resolved to
  a full path through the record map, including folders created after the scan. Applying them is trivial work.
- **The C journal holds about 12 hours** at this activity level. After a longer outage the helper rescans C (about 4 s);
  that is cheap enough that the journal size can stay as Windows set it.
- **D and E had no journal** (journals are switched on by programs, not by file activity); the service now creates them.

## Decisions (2026-11-28, with the user)

- **A Windows service** (LocalSystem, installed once from an admin terminal), not an elevated logon task. Separate from the
  Zebar collector, which must stay small.
- **About 200 MB resident for C, D and E is acceptable**, modified times included (for recent files and sorting later).
- **Search runs in the service**; the launcher gets the top 12 and ranks them below name matches of apps.
- **Drives are opt-in** (the user's choice, 2026-11-28): nothing is indexed until a drive is turned on, also on other PCs;
  USB NTFS drives can be turned on too. Here C, D and E are on (kept from their snapshots on upgrade); F and H (USB, NTFS)
  are off; G is exFAT (unsupported).
- **Excluding a folder hides it, keeps its size** (the user's choice): no search results or space-map listing inside it, but
  its total still counts in its parents, so drive totals stay right.
- **Any logged-in user may change settings** (the user's choice); every change is logged with the user and program.
- **Endpoints wanted now** (the user's choice): space map, largest files, file types, recent files, search filters.
- **Works on any PC, not just this one.** Drives are found at runtime (fixed and removable) and keyed
  by volume serial, not drive letter. A missing journal is created by the service itself: `FSCTL_QUERY_USN_JOURNAL` returns
  `ERROR_JOURNAL_NOT_ACTIVE` (1179) → `FSCTL_CREATE_USN_JOURNAL` (32 MB maximum, 8 MB allocation delta, like Windows' default
  on C). It never changes an existing journal's size. Journals aren't created by file activity; Windows only turns one on for
  the system drive, other tools (Everything, backup software, Windows Search) turn them on elsewhere. Wait and retry on
  `ERROR_JOURNAL_DELETE_IN_PROGRESS` (1178). Creating them is **on by default, without a prompt** (the user's decision); each
  creation is logged with its undo (`fsutil usn deletejournal /d D:`). The `create_journals` setting turns it off.

## Known issues

- **Only NTFS.** exFAT/FAT drives (no MFT, no journal) and ReFS, including Windows 11 Dev Drives (journal but no MFT), are
  skipped. Fallbacks to sketch: a folder walk plus `ReadDirectoryChangesW`; for ReFS `FSCTL_ENUM_USN_DATA` if it works there.
- **Drives added while it runs** (a new disk, a USB drive plugged in) are picked up at the next settings change or service
  start, not when they appear.
- **A drive letter that changes while it runs** keeps the old letter in paths until the drive's worker restarts.
- **`status` and drive changes touch every drive** (volume information, free space, bus type), which can wake a sleeping USB
  disk.
- **Shared PCs:** search and the space map show every user's file names (the service reads the whole drive), and any local
  user can change the settings. Fine for a personal PC; a shared one would want per-user filtering.
- **Excluding a folder that doesn't exist yet** works once it's created (checked on create), but a folder renamed *to* an
  excluded path's name is only caught at the next rescan or settings change.
- **Without a journal** (creation failed) the drive errors and retries every 5 minutes instead of falling back to rescans.
- **Moving a folder into or out of a noisy place** doesn't update its descendants' noisy flag until the next rescan.
- **Hard links:** one name per file (C has 234 k extra hard-link names, mostly the Windows component store).
- Alternate data streams are not counted in sizes.
- **Pipe squatting:** the launcher trusts whoever serves `\\.\pipe\GlazeIndex`; a program running as the user could create it
  first while the service is down and feed fake results (no privilege gain). Checking the server's process path would close it.
- The MFT is read through the file cache in 8 MB synchronous chunks (1.0–1.2 GB/s); unbuffered overlapped reads would be
  faster if first-scan time ever matters. Three drives scan at once, so memory peaks above the steady state while scanning.
- **First build: Vec doubling.** Right after a scan every array was exactly full, so the first journal change doubled them
  (C: 160 → 245 MB, later 286 MB). Fixed (growth by about 3%): after the reinstall C is 153 MB, the process 234 MB.
- **First build: stop hung.** The journal read waits for a change no matter what timeout is passed, so on a quiet drive the
  worker never noticed the stop and `--install` gave up after 60 s (the service stopped once a folder was created and
  deleted on D and E). Fixed: shutdown cancels the blocked reads with `CancelSynchronousIo` (checked: the 11:53 install stopped the 11:30 build in a second). `--install` also waits for a stop already in progress (error 1061) and ends a service that hangs for 20 s, which got the 11:04 build out (checked).
- **First build: `--install` from the installed copy** silently restarted the old version (copying a file onto itself was
  skipped), so the first fix round never got installed. Fixed: it refuses and names the build folder.
- **First build: pipe close.** The server ended connections with `DisconnectNamedPipe`, which Rust clients read as error 233
  instead of end of data (.NET treats it as the end). Fixed (it closes the handle); `--status` and `--search` work (checked).

## Change log

Newest first.

### 2026-11-28
- **Files under `\$Extend` no longer show up at the drive root.** NTFS moves a file that is deleted while still open into
  `\$Extend\$Deleted` under a hex name (`0005000000276C8628591B6B`). The scan skipped only `$Extend`'s direct children and
  the journal fell back to the root for unknown parents, so `recent` listed these as `C:\<hex>`. `ntfs.rs` now skips
  everything under `$Extend`; `index.rs` drops a change whose parent isn't indexed and removes an entry moved there. The
  11:55 build (future dates in `recent`, real case for `exclude`) was installed and checked at 15:16. This fix was installed
  at 15:19; a `--rescan C:` (3.8 s) cleared the 13 entries already in the snapshot, and `recent` and the C:\ listing
  showed no hex names afterwards.
- **Settings and the space-map backend.** New `settings.rs` (`settings.json`: opt-in drives by serial, excluded folders,
  demoted names, `search_hidden`, `create_journals`; first start keeps drives with snapshots), `api.rs` (JSON requests:
  status, settings, settings.set, drive, exclude, rescan, search with filters, children, largest, types, recent, stat; the
  old text lines still work), `query.rs` (the listing requests). `index.rs`: per-folder file counts, the `EXCLUDED` flag,
  rules from the settings, path lookup, children. `engine.rs`: workers start and stop with the settings, rescan,
  status of every drive. `volumes.rs`: every fixed or removable drive with label, file system, bus and space. Settings
  changes are logged with the user and program (`win::pipe_client`). CLI shortcuts for each request. `json.rs` removed
  (serde). Checked through the pipe: migration kept C, D, E; children, largest, types, recent, filters, exclude and
  un-exclude, drive off and on (E rescanned in 0.9 s), rescan D (0.6 s), error replies; the launcher's typed filters
  (`tooltips *.zip`). Then fixed `recent` listing files with future dates first, and `exclude` storing the typed case.
- **Built and installed GlazeIndex** (the service: `main.rs`, `engine.rs`, `ntfs.rs`, `index.rs`, `search.rs`,
  `snapshot.rs`, `pipe.rs`, `service.rs`, `volumes.rs`, `log.rs`, `json.rs`, `win.rs`). The user installed it; it created
  journals on D and E and scanned all three drives in 3.5 s. Live create, grow, rename and delete were checked through the
  pipe. Then fixed the Vec doubling, the pipe close, the hanging stop and three `--install` problems (run from the installed copy, a stop already in progress, a hung old service); the user installed the 11:30 build, which loaded all three snapshots in 60 ms. Launcher side: see `docs/launcher.md`.
- **Added the measuring probe** (`indexer/`, `index-probe.exe`) and ran it elevated on C, D and E. Results above. Found that
  D and E have no USN journal. No service, client or launcher changes yet.
