//! Appends timestamped lines to `%ProgramData%\GlazeIndex\index.log` (and stderr in console mode).

use std::io::Write;
use std::path::PathBuf;
use std::sync::Mutex;

use crate::win;

static LOG: Mutex<Option<PathBuf>> = Mutex::new(None);
static ECHO: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
const MAX_BYTES: u64 = 1 << 20;

pub fn init(path: PathBuf, echo: bool) {
    // Keep one older file when the log grows past 1 MB.
    if std::fs::metadata(&path).map(|m| m.len() > MAX_BYTES).unwrap_or(false) {
        let _ = std::fs::rename(&path, path.with_extension("log.old"));
    }
    *LOG.lock().unwrap() = Some(path);
    ECHO.store(echo, std::sync::atomic::Ordering::Relaxed);
}

pub fn write(message: &str) {
    let mut t = win::SystemTime::default();
    unsafe { win::GetLocalTime(&mut t) };
    let line = format!("{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03} {message}\n",
        t.year, t.month, t.day, t.hour, t.minute, t.second, t.milliseconds);
    if ECHO.load(std::sync::atomic::Ordering::Relaxed) {
        eprint!("{line}");
    }
    let guard = LOG.lock().unwrap();
    if let Some(path) = guard.as_ref() {
        if let Ok(mut file) = std::fs::OpenOptions::new().create(true).append(true).open(path) {
            let _ = file.write_all(line.as_bytes());
        }
    }
}

#[macro_export]
macro_rules! log {
    ($($arg:tt)*) => { $crate::log::write(&format!($($arg)*)) };
}
