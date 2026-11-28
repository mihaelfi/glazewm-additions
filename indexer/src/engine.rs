//! Runs the index: the settings, one worker thread per turned-on drive (load or scan it, then follow its change journal),
//! and the shared state the pipe requests read.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, RwLock};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use serde_json::{json, Value};

use crate::index::Index;
use crate::ntfs::{self, Change, ReadResult, Volume, REASON_BASIC_INFO, REASON_CLOSE, REASON_CREATE, REASON_DATA, REASON_DELETE, REASON_RENAME_NEW};
use crate::settings::{serial_text, Rules, Settings};
use crate::{log, search, snapshot, volumes, win};

const SAVE_EVERY: Duration = Duration::from_secs(10 * 60);
/// Passed as the journal read's timeout, but Windows waits for a change regardless; stopping cancels the read instead.
const JOURNAL_WAIT_S: u64 = 2;

pub struct VolumeState {
    pub letter: char,
    pub serial: u32,
    pub index: RwLock<Option<Index>>,
    pub state: Mutex<String>,
    stop: AtomicBool,
    rescan: AtomicBool,
    worker: Mutex<Option<JoinHandle<()>>>,
}

enum Exit { Stopped, Restart }

pub struct Engine {
    volumes: RwLock<Vec<Arc<VolumeState>>>,
    settings: Mutex<Settings>,
    rules: RwLock<Arc<Rules>>,
    stop: AtomicBool,
    data: PathBuf,
    settings_path: PathBuf,
}

impl Engine {
    pub fn start(base: PathBuf, data: PathBuf) -> Arc<Engine> {
        if !win::enable_backup_privilege() { log!("note: SeBackupPrivilege not enabled; sizes of some protected files may lag"); }
        let settings_path = base.join("settings.json");
        let settings = match Settings::load(&settings_path) {
            Ok(Some(s)) => s,
            Ok(None) => first_settings(&data, &settings_path),
            Err(e) => { log!("settings: {e}; using defaults until it's fixed (the file is left alone)"); Settings::default() }
        };
        let engine = Arc::new(Engine {
            volumes: RwLock::new(Vec::new()),
            rules: RwLock::new(Arc::new(Rules::from(&settings))),
            settings: Mutex::new(settings),
            stop: AtomicBool::new(false),
            data,
            settings_path,
        });
        engine.sync_volumes();
        engine
    }

    /// Starts workers for turned-on drives that are present, and stops the others.
    fn sync_volumes(self: &Arc<Self>) {
        let drives = volumes::detect();
        let settings = self.settings.lock().unwrap().clone();
        let running: Vec<Arc<VolumeState>> = self.volumes.read().unwrap().clone();
        for v in &running {
            let present = drives.iter().any(|d| d.serial == v.serial);
            if !settings.drive_enabled(v.serial) || !present {
                self.stop_volume(v, !settings.drive_enabled(v.serial));
            }
        }
        for d in drives {
            if d.unsupported.is_some() || !settings.drive_enabled(d.serial) { continue; }
            if self.volumes.read().unwrap().iter().any(|v| v.serial == d.serial) { continue; }
            let v = Arc::new(VolumeState {
                letter: d.letter, serial: d.serial, index: RwLock::new(None), state: Mutex::new("starting".into()),
                stop: AtomicBool::new(false), rescan: AtomicBool::new(false), worker: Mutex::new(None),
            });
            self.volumes.write().unwrap().push(v.clone());
            let (engine, v2) = (self.clone(), v.clone());
            let worker = std::thread::Builder::new().name(format!("volume {}", d.letter))
                .spawn(move || engine.run_volume(&v2)).expect("spawn volume worker");
            *v.worker.lock().unwrap() = Some(worker);
            log!("{}: indexing on", d.letter);
        }
    }

    /// Stops a drive's worker (cancelling a blocked journal read) and drops its index; `forget` also deletes its snapshot.
    fn stop_volume(&self, v: &Arc<VolumeState>, forget: bool) {
        v.stop.store(true, Ordering::SeqCst);
        Self::join_worker(v, Duration::from_secs(25));
        self.volumes.write().unwrap().retain(|x| !Arc::ptr_eq(x, v));
        *v.index.write().unwrap() = None;
        if forget {
            let _ = std::fs::remove_file(snapshot::path_for(&self.data, v.serial));
            log!("{}: indexing off; index and snapshot dropped", v.letter);
        }
    }

    /// Cancels the worker's blocked read until it exits (a journal read on a quiet drive waits for a change regardless of
    /// its timeout), repeatedly, in case it was between reads.
    fn join_worker(v: &VolumeState, limit: Duration) {
        use std::os::windows::io::AsRawHandle;
        let Some(worker) = v.worker.lock().unwrap().take() else { return };
        let deadline = Instant::now() + limit;
        while !worker.is_finished() && Instant::now() < deadline {
            unsafe { win::CancelSynchronousIo(worker.as_raw_handle() as win::Handle) };
            std::thread::sleep(Duration::from_millis(100));
        }
        if worker.is_finished() { let _ = worker.join(); }
    }

    pub fn shutdown(&self) {
        self.stop.store(true, Ordering::SeqCst);
        let running: Vec<Arc<VolumeState>> = self.volumes.read().unwrap().clone();
        for v in &running { Self::join_worker(v, Duration::from_secs(25)); }
        log!("stopped");
    }

    fn stopping(&self, v: &VolumeState) -> bool {
        self.stop.load(Ordering::SeqCst) || v.stop.load(Ordering::SeqCst)
    }

    fn set_state(v: &VolumeState, state: &str) {
        *v.state.lock().unwrap() = state.to_owned();
    }

    fn rules(&self) -> Arc<Rules> { self.rules.read().unwrap().clone() }

    fn run_volume(&self, v: &VolumeState) {
        let mut failures = 0u32;
        while !self.stopping(v) {
            match self.follow_volume(v) {
                Ok(Exit::Stopped) => return,
                Ok(Exit::Restart) => failures = 0,
                Err(e) => {
                    failures += 1;
                    log!("{}: {e}", v.letter);
                    Self::set_state(v, &format!("error: {e}"));
                    // Back off: 5 s, 30 s, then every 5 minutes.
                    let wait = match failures { 1 => 5, 2 => 30, _ => 300 };
                    for _ in 0..wait { if self.stopping(v) || v.rescan.load(Ordering::SeqCst) { break; } std::thread::sleep(Duration::from_secs(1)); }
                }
            }
        }
    }

    /// Loads or scans the volume, then applies journal changes until stopped, a rescan is asked for, or the journal is lost.
    fn follow_volume(&self, v: &VolumeState) -> Result<Exit, String> {
        let volume = Volume::open(v.letter)?;
        let create = self.settings.lock().unwrap().create_journals;
        let journal = ntfs::ensure_journal(&volume, create)?;
        let fresh = v.rescan.swap(false, Ordering::SeqCst);

        let loaded = if fresh { None } else {
            snapshot::load(&self.data, v.serial, v.letter)
                .filter(|s| s.journal_id == journal.id && s.next_usn >= journal.first_usn && s.next_usn <= journal.next_usn)
        };
        let mut next = match loaded {
            Some(mut saved) => {
                saved.index.refresh(&self.rules());
                log!("{}: loaded the snapshot ({} entries); catching up from the journal", v.letter, saved.index.live());
                *v.index.write().unwrap() = Some(saved.index);
                saved.next_usn
            }
            None => {
                Self::set_state(v, "scanning");
                // Changes made during the scan are replayed from here afterwards.
                let start = journal.next_usn;
                unsafe { win::SetThreadPriority(win::GetCurrentThread(), win::THREAD_MODE_BACKGROUND_BEGIN) };
                let scanned = ntfs::scan(&volume, &self.rules());
                unsafe { win::SetThreadPriority(win::GetCurrentThread(), 0x0002_0000) }; // THREAD_MODE_BACKGROUND_END
                let (index, stats) = scanned?;
                log!("{}: scanned {} entries in {} ms ({:.0} MB)", v.letter, stats.entries, stats.time.as_millis(), index.memory() as f64 / 1_048_576.0);
                *v.index.write().unwrap() = Some(index);
                self.save(v, journal.id, start);
                start
            }
        };
        Self::set_state(v, "ready");

        let mut buffer = vec![0u8; 256 * 1024];
        let mut dirty = false;
        let mut last_save = Instant::now();
        let mut applied = 0u64;
        while !self.stopping(v) {
            let started = Instant::now();
            let (mut changes, after) = match ntfs::read_journal(&volume, journal.id, next, JOURNAL_WAIT_S, &mut buffer) {
                ReadResult::Changes(c, n) => (c, n),
                ReadResult::Lost(e) => {
                    // A read cancelled to stop or rescan (error 995) isn't a lost journal.
                    if self.stopping(v) { break; }
                    if v.rescan.load(Ordering::SeqCst) { return Ok(Exit::Restart); }
                    if dirty { self.save(v, journal.id, next); }
                    return Err(format!("{e}; rescanning"));
                }
            };
            if changes.is_empty() {
                // Guard against a journal read that returns at once instead of waiting.
                if started.elapsed() < Duration::from_millis(20) { std::thread::sleep(Duration::from_millis(250)); }
            } else {
                lookup_sizes(&volume, &mut changes);
                let rules = self.rules();
                let mut guard = v.index.write().unwrap();
                if let Some(index) = guard.as_mut() {
                    for change in &changes {
                        if index.apply(change, &rules) { dirty = true; applied += 1; }
                    }
                }
            }
            next = after;
            if dirty && last_save.elapsed() >= SAVE_EVERY {
                log!("{}: {applied} changes applied since the last save", v.letter);
                self.save(v, journal.id, next);
                dirty = false;
                applied = 0;
                last_save = Instant::now();
            }
        }
        // Stopped: save, unless the drive was turned off (its snapshot is being deleted).
        if dirty && !v.stop.load(Ordering::SeqCst) { self.save(v, journal.id, next); }
        Ok(Exit::Stopped)
    }

    /// Compacts if worthwhile, then writes the snapshot.
    fn save(&self, v: &VolumeState, journal_id: u64, next_usn: i64) {
        {
            let mut guard = v.index.write().unwrap();
            if let Some(index) = guard.as_mut().filter(|i| i.needs_compaction()) { index.compact(); }
        }
        let guard = v.index.read().unwrap();
        let Some(index) = guard.as_ref() else { return };
        let started = Instant::now();
        match snapshot::save(&self.data, v.serial, index, journal_id, next_usn) {
            Ok(bytes) => log!("{}: snapshot saved ({:.0} MB, {} ms)", v.letter, bytes as f64 / 1_048_576.0, started.elapsed().as_millis()),
            Err(e) => log!("{}: snapshot save failed: {e}", v.letter),
        }
    }

    // ---- settings ------------------------------------------------------------------------------------------------

    pub fn settings(&self) -> Settings { self.settings.lock().unwrap().clone() }

    /// Validates, saves and applies new settings: drives turned on start indexing, drives turned off stop (index and
    /// snapshot dropped), and changed exclusions or demoted names are re-applied to every index.
    pub fn update_settings(self: &Arc<Self>, mut new: Settings, who: &str) -> Result<Settings, String> {
        new.normalize()?;
        let old = self.settings();
        if new == old { return Ok(new); }
        new.save(&self.settings_path)?;
        log!("settings changed by {who}: {}", describe_change(&old, &new));
        *self.settings.lock().unwrap() = new.clone();
        if (old.excluded.clone(), old.demoted.clone(), old.demoted_at_root.clone()) != (new.excluded.clone(), new.demoted.clone(), new.demoted_at_root.clone()) {
            let rules = Arc::new(Rules::from(&new));
            *self.rules.write().unwrap() = rules.clone();
            let started = Instant::now();
            for v in self.volumes.read().unwrap().iter() {
                if let Some(index) = v.index.write().unwrap().as_mut() { index.refresh(&rules); }
            }
            log!("re-applied exclusions and demoted names in {} ms", started.elapsed().as_millis());
        }
        if old.drives != new.drives { self.sync_volumes(); }
        Ok(new)
    }

    /// Throws a drive's index away and scans it again.
    pub fn rescan(&self, letter: char) -> Result<(), String> {
        let v = self.volumes.read().unwrap().iter().find(|v| v.letter.eq_ignore_ascii_case(&letter)).cloned()
            .ok_or_else(|| format!("{letter}: isn't being indexed"))?;
        v.rescan.store(true, Ordering::SeqCst);
        let worker = v.worker.lock().unwrap();
        if let Some(w) = worker.as_ref() {
            use std::os::windows::io::AsRawHandle;
            unsafe { win::CancelSynchronousIo(w.as_raw_handle() as win::Handle) };
        }
        log!("{}: rescan requested", v.letter);
        Ok(())
    }

    // ---- reading ----------------------------------------------------------------------------------------------

    /// Runs `f` with read access to every ready index (and the drive each belongs to).
    pub fn with_indexes<R>(&self, f: impl FnOnce(&[&Index]) -> R) -> R {
        let volumes = self.volumes.read().unwrap().clone();
        let guards: Vec<_> = volumes.iter().map(|v| v.index.read().unwrap()).collect();
        let indexes: Vec<&Index> = guards.iter().filter_map(|g| g.as_ref()).collect();
        f(&indexes)
    }

    pub fn all_ready(&self) -> bool {
        self.volumes.read().unwrap().iter().all(|v| v.index.read().unwrap().is_some())
    }

    pub fn status(&self) -> Value {
        let settings = self.settings();
        let running: HashMap<u32, Arc<VolumeState>> = self.volumes.read().unwrap().iter().map(|v| (v.serial, v.clone())).collect();
        let mut drives = Vec::new();
        let present = volumes::detect();
        for d in &present {
            let (state, entries, memory) = match running.get(&d.serial) {
                Some(v) => {
                    let guard = v.index.read().unwrap();
                    let (entries, memory) = guard.as_ref().map(|i| (i.live(), i.memory())).unwrap_or((0, 0));
                    (v.state.lock().unwrap().clone(), entries, memory)
                }
                None => ((if settings.drive_enabled(d.serial) { "not started" } else { "off" }).to_string(), 0, 0),
            };
            drives.push(json!({
                "drive": format!("{}:", d.letter), "label": d.label, "serial": serial_text(d.serial), "file_system": d.file_system,
                "usb": d.usb, "removable": d.removable, "total": d.total, "free": d.free,
                "enabled": settings.drive_enabled(d.serial), "supported": d.unsupported.is_none(), "reason": d.unsupported,
                "state": state, "entries": entries, "memory": memory,
            }));
        }
        for s in settings.drives.iter().filter(|s| s.enabled) {
            if present.iter().any(|d| serial_text(d.serial) == s.serial) { continue; }
            drives.push(json!({ "drive": s.label, "serial": s.serial, "enabled": true, "state": "not connected" }));
        }
        json!({
            "ok": true,
            "version": env!("CARGO_PKG_VERSION"),
            "build": win::build_time(),
            "private_memory": win::private_memory(),
            "min_query": search::MIN_QUERY,
            "settings_file": self.settings_path.display().to_string(),
            "drives": drives,
        })
    }
}

/// The settings a first start writes. Nothing is indexed by default, except that drives with a snapshot from an earlier
/// version (which indexed every internal drive) stay on.
fn first_settings(data: &std::path::Path, path: &std::path::Path) -> Settings {
    let mut settings = Settings::default();
    let snapshots: Vec<u32> = std::fs::read_dir(data).into_iter().flatten().flatten()
        .filter_map(|e| e.file_name().to_str().and_then(|n| n.strip_prefix("vol-")?.strip_suffix(".glix").map(str::to_owned)))
        .filter_map(|s| u32::from_str_radix(&s, 16).ok())
        .collect();
    let drives = volumes::detect();
    for serial in &snapshots {
        let letter = drives.iter().find(|d| d.serial == *serial).map(|d| d.letter).unwrap_or('?');
        settings.set_drive(*serial, letter, true);
    }
    match settings.save(path) {
        Ok(()) if snapshots.is_empty() => log!("settings created; no drive is indexed until one is turned on"),
        Ok(()) => log!("settings created; kept indexing the drives that had snapshots: {}",
            settings.drives.iter().map(|d| d.label.as_str()).collect::<Vec<_>>().join(" ")),
        Err(e) => log!("settings: {e}"),
    }
    settings
}

fn describe_change(old: &Settings, new: &Settings) -> String {
    let mut parts = Vec::new();
    for d in &new.drives {
        let before = old.drives.iter().find(|o| o.serial == d.serial).map(|o| o.enabled).unwrap_or(false);
        if before != d.enabled { parts.push(format!("{} {} ({})", if d.enabled { "turned on" } else { "turned off" }, d.label, d.serial)); }
    }
    let list = |a: &Vec<String>, b: &Vec<String>, what: &str, parts: &mut Vec<String>| {
        for x in b.iter().filter(|x| !a.contains(x)) { parts.push(format!("{what} + {x}")); }
        for x in a.iter().filter(|x| !b.contains(x)) { parts.push(format!("{what} - {x}")); }
    };
    list(&old.excluded, &new.excluded, "excluded", &mut parts);
    list(&old.demoted, &new.demoted, "demoted", &mut parts);
    list(&old.demoted_at_root, &new.demoted_at_root, "demoted at root", &mut parts);
    if old.search_hidden != new.search_hidden { parts.push(format!("search_hidden = {}", new.search_hidden)); }
    if old.create_journals != new.create_journals { parts.push(format!("create_journals = {}", new.create_journals)); }
    if parts.is_empty() { "no effective change".into() } else { parts.join("; ") }
}

/// For files whose content or name may have changed and that were closed, reads the current size and time once per file.
fn lookup_sizes(volume: &Volume, changes: &mut [Change]) {
    let mut last: HashMap<u64, usize> = HashMap::new();
    for (k, c) in changes.iter().enumerate() {
        if c.reason & REASON_CLOSE != 0 && c.reason & REASON_DELETE == 0 && c.attributes & 0x10 == 0
            && c.reason & (REASON_DATA | REASON_CREATE | REASON_RENAME_NEW | REASON_BASIC_INFO) != 0 {
            last.insert(c.file_reference, k);
        }
    }
    for (reference, k) in last {
        changes[k].info = volume.file_info(reference);
    }
}
