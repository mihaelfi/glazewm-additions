//! Runs the index: one worker thread per volume that loads or scans it, then follows its change journal; plus the
//! search and status answers for the pipe.

use std::collections::HashMap;
use std::fmt::Write as _;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, RwLock};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use crate::index::{Index, DIR};
use crate::ntfs::{self, Change, ReadResult, Volume, REASON_BASIC_INFO, REASON_CLOSE, REASON_CREATE, REASON_DATA, REASON_DELETE, REASON_RENAME_NEW};
use crate::{json, log, search, snapshot, volumes, win};

const SAVE_EVERY: Duration = Duration::from_secs(10 * 60);
/// Passed as the journal read's timeout, but Windows waits for a change regardless; shutdown cancels the read instead.
const JOURNAL_WAIT_S: u64 = 2;

pub struct VolumeState {
    pub letter: char,
    pub serial: u32,
    pub index: RwLock<Option<Index>>,
    pub state: Mutex<String>,
}

pub struct Engine {
    pub volumes: Vec<Arc<VolumeState>>,
    pub skipped: Vec<String>,
    stop: AtomicBool,
    data: PathBuf,
    workers: Mutex<Vec<JoinHandle<()>>>,
}

impl Engine {
    pub fn start(data: PathBuf) -> Arc<Engine> {
        if !win::enable_backup_privilege() { log!("note: SeBackupPrivilege not enabled; sizes of some protected files may lag"); }
        let mut volumes = Vec::new();
        let mut skipped = Vec::new();
        for candidate in volumes::find() {
            match candidate {
                Ok(c) => volumes.push(Arc::new(VolumeState {
                    letter: c.letter, serial: c.serial, index: RwLock::new(None), state: Mutex::new("starting".into()),
                })),
                Err(reason) => { log!("{reason}"); skipped.push(reason); }
            }
        }
        let engine = Arc::new(Engine { volumes, skipped, stop: AtomicBool::new(false), data, workers: Mutex::new(Vec::new()) });
        for v in &engine.volumes {
            let (engine2, v2) = (engine.clone(), v.clone());
            let worker = std::thread::Builder::new().name(format!("volume {}", v.letter))
                .spawn(move || engine2.run_volume(&v2)).expect("spawn volume worker");
            engine.workers.lock().unwrap().push(worker);
        }
        engine
    }

    /// Asks the workers to save and stop, and waits for them. A worker waiting on a quiet drive's journal stays blocked
    /// until a change arrives (the read's timeout isn't honoured), so its read is cancelled (`CancelSynchronousIo`),
    /// repeatedly, in case a worker was between reads the first time.
    pub fn shutdown(&self) {
        use std::os::windows::io::AsRawHandle;
        self.stop.store(true, Ordering::SeqCst);
        let workers: Vec<_> = self.workers.lock().unwrap().drain(..).collect();
        let deadline = Instant::now() + Duration::from_secs(25);
        for w in workers {
            while !w.is_finished() && Instant::now() < deadline {
                unsafe { win::CancelSynchronousIo(w.as_raw_handle() as win::Handle) };
                std::thread::sleep(Duration::from_millis(100));
            }
            if w.is_finished() { let _ = w.join(); }
        }
        log!("stopped");
    }

    fn stopping(&self) -> bool { self.stop.load(Ordering::SeqCst) }

    fn set_state(v: &VolumeState, state: &str) {
        *v.state.lock().unwrap() = state.to_owned();
    }

    fn run_volume(&self, v: &VolumeState) {
        let mut failures = 0u32;
        while !self.stopping() {
            match self.follow_volume(v) {
                Ok(()) => return,
                Err(e) => {
                    failures += 1;
                    log!("{}: {e}", v.letter);
                    Self::set_state(v, &format!("error: {e}"));
                    // Back off: 5 s, 30 s, then every 5 minutes.
                    let wait = match failures { 1 => 5, 2 => 30, _ => 300 };
                    for _ in 0..wait { if self.stopping() { return; } std::thread::sleep(Duration::from_secs(1)); }
                }
            }
        }
    }

    /// Loads or scans the volume, then applies journal changes until told to stop (Ok) or the journal is lost (Err,
    /// which leads to a rescan).
    fn follow_volume(&self, v: &VolumeState) -> Result<(), String> {
        let volume = Volume::open(v.letter)?;
        let journal = ntfs::ensure_journal(&volume)?;

        let loaded = snapshot::load(&self.data, v.serial, v.letter)
            .filter(|s| s.journal_id == journal.id && s.next_usn >= journal.first_usn && s.next_usn <= journal.next_usn);
        let mut next = match loaded {
            Some(saved) => {
                log!("{}: loaded the snapshot ({} entries); catching up from the journal", v.letter, saved.index.live());
                *v.index.write().unwrap() = Some(saved.index);
                saved.next_usn
            }
            None => {
                Self::set_state(v, "scanning");
                // Changes made during the scan are replayed from here afterwards.
                let start = journal.next_usn;
                unsafe { win::SetThreadPriority(win::GetCurrentThread(), win::THREAD_MODE_BACKGROUND_BEGIN) };
                let scanned = ntfs::scan(&volume);
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
        while !self.stopping() {
            let started = Instant::now();
            let (mut changes, after) = match ntfs::read_journal(&volume, journal.id, next, JOURNAL_WAIT_S, &mut buffer) {
                ReadResult::Changes(c, n) => (c, n),
                ReadResult::Lost(e) => {
                    if dirty { self.save(v, journal.id, next); }
                    // A read cancelled by shutdown (error 995) isn't a lost journal.
                    if self.stopping() { return Ok(()); }
                    return Err(format!("{e}; rescanning"));
                }
            };
            if changes.is_empty() {
                // Guard against a journal read that returns at once instead of waiting.
                if started.elapsed() < Duration::from_millis(20) { std::thread::sleep(Duration::from_millis(250)); }
            } else {
                lookup_sizes(&volume, &mut changes);
                let mut guard = v.index.write().unwrap();
                if let Some(index) = guard.as_mut() {
                    for change in &changes {
                        if index.apply(change) { dirty = true; applied += 1; }
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
        if dirty { self.save(v, journal.id, next); }
        Ok(())
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

    // ---- pipe answers --------------------------------------------------------------------------------------------

    pub fn search_json(&self, query: &str, limit: usize) -> String {
        let started = Instant::now();
        let guards: Vec<_> = self.volumes.iter().map(|v| v.index.read().unwrap()).collect();
        let indexes: Vec<&Index> = guards.iter().filter_map(|g| g.as_ref()).collect();
        let outcome = search::search(&indexes, query, limit.clamp(1, 200));
        let mut out = String::with_capacity(4096);
        out.push_str("{\"query\":");
        json::string(&mut out, query);
        let _ = write!(out, ",\"matches\":{},\"ms\":{:.1},\"ready\":{},\"results\":[",
            outcome.matches, started.elapsed().as_secs_f64() * 1000.0, indexes.len() == self.volumes.len());
        for (k, hit) in outcome.hits.iter().enumerate() {
            let index = indexes[hit.volume];
            let i = hit.entry as usize;
            if k > 0 { out.push(','); }
            out.push_str("{\"path\":");
            json::string(&mut out, &index.path(i));
            out.push_str(",\"name\":");
            json::string(&mut out, index.name(i));
            let _ = write!(out, ",\"folder\":{},\"size\":{},\"modified\":{},\"score\":{},\"highlight\":[{},{}]}}",
                index.flags[i] & DIR != 0, index.size[i], filetime_to_unix_ms(index.modified[i]), hit.score, hit.highlight.0, hit.highlight.1);
        }
        out.push_str("]}");
        out
    }

    pub fn status_json(&self) -> String {
        let mut out = String::from("{\"volumes\":[");
        for (k, v) in self.volumes.iter().enumerate() {
            if k > 0 { out.push(','); }
            let guard = v.index.read().unwrap();
            let (entries, memory) = guard.as_ref().map(|i| (i.live(), i.memory())).unwrap_or((0, 0));
            out.push_str("{\"drive\":");
            json::string(&mut out, &format!("{}:", v.letter));
            out.push_str(",\"state\":");
            json::string(&mut out, &v.state.lock().unwrap());
            let _ = write!(out, ",\"entries\":{entries},\"memory\":{memory}}}");
        }
        out.push_str("],\"skipped\":[");
        for (k, s) in self.skipped.iter().enumerate() {
            if k > 0 { out.push(','); }
            json::string(&mut out, s);
        }
        let _ = write!(out, "],\"private_memory\":{},\"min_query\":{}}}", win::private_memory(), search::MIN_QUERY);
        out
    }
}

fn filetime_to_unix_ms(filetime: u64) -> i64 {
    if filetime == 0 { return 0; }
    (filetime / 10_000) as i64 - 11_644_473_600_000
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
