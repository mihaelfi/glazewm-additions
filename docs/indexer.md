# File index (GlazeIndex): Everything-style search and folder sizes

Status: **running as a Windows service** (2026-11-28). GlazeIndex knows every file and folder on the internal NTFS
drives, kept current from the NTFS change journal, and serves:

- **GlazeLauncher:** file and folder results (`tooltips`, `dragon tooltips` → `D:\Dragon Age Mods\Detailed Tooltips-248`),
  then Open, Terminal / PowerShell / cmd here, Open file location, Copy path. See `docs/launcher.md`.
- **Zebar storage popup (not built yet):** a TreeSize-style space map from the same folder totals.

Backlog items: "File and folder index, Everything-style" (Launcher) and "Space map, TreeSize-style" (Zebar: storage).

## Current state

| What | Where |
|---|---|
| Source | `indexer/` (Rust, no dependencies, plain Win32 calls). `src/main.rs` → `GlazeIndex.exe`; `src/probe.rs` → `index-probe.exe` (the measuring prototype, standalone) |
| Installed service | `C:\Program Files\GlazeIndex\GlazeIndex.exe --service`, service `GlazeIndex`, LocalSystem, automatic (delayed) start |
| Log | `%ProgramData%\GlazeIndex\index.log` (users can read it; rotated at 1 MB to `index.log.old`) |
| Snapshots | `%ProgramData%\GlazeIndex\data\vol-<serial>.glix` (administrators and SYSTEM only: they list every file name) |
| Pipe | `\\.\pipe\GlazeIndex` (SYSTEM, administrators, interactive users; remote clients refused) |

```powershell
cd C:\codex\glazewm\indexer
cargo build --release --offline
# admin terminal: install or update (stops the old service, which saves its snapshots, copies, starts)
.\target\release\GlazeIndex.exe --install
# any terminal:
.\target\release\GlazeIndex.exe --status
.\target\release\GlazeIndex.exe --search dragon tooltips
# admin: --console runs it in the terminal instead (Enter stops); --uninstall removes service, program and data
```

An agent can't install or update it (no admin); ask the user to run `--install` **from the build folder**
(`C:\codex\glazewm\indexer\target\release\GlazeIndex.exe --install`). Run from the installed copy it refuses, because it
would only restart the old version. If the old service doesn't stop within 20 s, `--install` ends its process (the next
start loads the last snapshot and catches up from the journal). `--install` prints timestamped lines naming the build it
installs and the one it replaces (by file time), and the log's start line names the running build
(`starting GlazeIndex 0.1.0, build of 2026-11-28 11:28`). `indexer/out/` and `indexer/target/` are
git-ignored; probe reports list real file paths and must stay out of Git.

**How it runs** (`engine.rs`): at start it finds the drives (`volumes.rs`: fixed, NTFS, not USB; keyed by volume serial),
then one worker per drive:

1. Opens the volume and gets its journal, creating one if missing (`ntfs::ensure_journal`, logged).
2. Loads the snapshot if it was saved against the same journal ID and its position is still in the journal; otherwise scans
   the MFT (`ntfs::scan`, in background I/O mode), saves a snapshot, and replays the changes made during the scan.
3. Follows the journal: blocking reads of up to 2 s, a batch at a time. Closed files whose content or name changed get their
   size and time from `OpenFileById` (with SeBackupPrivilege), once per file per batch. Changes apply under the volume's
   write lock (`Index::apply`: create, rename/move with size moved between folders, delete, size delta up the parent chain).
4. Every 10 minutes with changes, and on stop, compacts when a tenth is dead and saves the snapshot (written to `.tmp`, then
   renamed). A lost position (journal wrapped or deleted) logs and rescans; other failures retry after 5 s, 30 s, then 5 min.

**Search** (`search.rs`): needs 3 letters; every word must be in the name or a folder above it, at least one in the name.
Score: exact name 1000, name start 800, word start 600, elsewhere 400; +100 when all words are in the name; folders +100;
noisy places −350 (`node_modules`, `.git`, `AppData`, `__pycache__`, `site-packages`, `.cargo`, `.nuget`, `.npm`, … anywhere;
`Windows`, `ProgramData`, `Recovery`, … at the root; inherited by everything below); hidden/system −200; minus half the
name length and 4 per folder level. Parallel over all CPU threads. Returns the top N with path, name, folder flag, size
(folder total), modified time (Unix ms) and the matched word's position for highlighting.

**Pipe protocol** (`pipe.rs`): one request line per connection, one JSON reply, then the server closes it.
`search<TAB>limit<TAB>text` → `{"query","matches","ms","ready","results":[{"path","name","folder","size","modified","score","highlight":[start,len]}]}`;
`status` → `{"volumes":[{"drive","state","entries","memory"}],"skipped":[…],"private_memory","min_query"}`; `ping`.

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
- **Internal NTFS drives only** (C, D, E here; the USB drives F, G, H stay out, per the earlier storage decision; G is exFAT
  and has no MFT or journal anyway).
- **Works on any PC, not just this one.** Volumes are found at runtime (fixed NTFS volumes; USB ones are excluded) and keyed
  by volume serial, not drive letter. A missing journal is created by the service itself: `FSCTL_QUERY_USN_JOURNAL` returns
  `ERROR_JOURNAL_NOT_ACTIVE` (1179) → `FSCTL_CREATE_USN_JOURNAL` (32 MB maximum, 8 MB allocation delta, like Windows' default
  on C). It never changes an existing journal's size. Journals aren't created by file activity; Windows only turns one on for
  the system drive, other tools (Everything, backup software, Windows Search) turn them on elsewhere. Wait and retry on
  `ERROR_JOURNAL_DELETE_IN_PROGRESS` (1178). Creating them is **on by default, without a prompt** (the user's decision); each
  creation is logged with its undo (`fsutil usn deletejournal /d D:`). There is no setting to turn it off yet.
- **Space map from the same arrays** (next step): folder totals are kept current by adding each change's size delta up the
  parent chain; a `children <path>` pipe request will serve the Zebar popup.

## Known issues

- **Only NTFS.** exFAT/FAT drives (no MFT, no journal) and ReFS, including Windows 11 Dev Drives (journal but no MFT), are
  skipped. Fallbacks to sketch: a folder walk plus `ReadDirectoryChangesW`; for ReFS `FSCTL_ENUM_USN_DATA` if it works there.
- **Drives added while it runs** (a new internal disk) are picked up only at the next service start.
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
  deleted on D and E). Fixed: shutdown cancels the blocked reads with `CancelSynchronousIo` (installed 11:30; not yet seen in a real stop). `--install` also waits for a stop already in progress (error 1061) and ends a service that hangs for 20 s, which got the 11:04 build out (checked).
- **First build: `--install` from the installed copy** silently restarted the old version (copying a file onto itself was
  skipped), so the first fix round never got installed. Fixed: it refuses and names the build folder.
- **First build: pipe close.** The server ended connections with `DisconnectNamedPipe`, which Rust clients read as error 233
  instead of end of data (.NET treats it as the end). Fixed (it closes the handle); `--status` and `--search` work (checked).

## Change log

Newest first.

### 2026-11-28
- **Built and installed GlazeIndex** (the service: `main.rs`, `engine.rs`, `ntfs.rs`, `index.rs`, `search.rs`,
  `snapshot.rs`, `pipe.rs`, `service.rs`, `volumes.rs`, `log.rs`, `json.rs`, `win.rs`). The user installed it; it created
  journals on D and E and scanned all three drives in 3.5 s. Live create, grow, rename and delete were checked through the
  pipe. Then fixed the Vec doubling, the pipe close, the hanging stop and three `--install` problems (run from the installed copy, a stop already in progress, a hung old service); the user installed the 11:30 build, which loaded all three snapshots in 60 ms. Launcher side: see `docs/launcher.md`.
- **Added the measuring probe** (`indexer/`, `index-probe.exe`) and ran it elevated on C, D and E. Results above. Found that
  D and E have no USN journal. No service, client or launcher changes yet.
