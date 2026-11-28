//! The service's settings, `%ProgramData%\GlazeIndex\settings.json`. The service owns the file; clients change it through
//! the pipe (`settings.set`, `drive`, `exclude`), and every change is logged with who made it.

use std::path::Path;

use serde::{Deserialize, Serialize};

pub const DEFAULT_DEMOTED: &[&str] = &["node_modules", ".git", ".svn", ".hg", "appdata", "__pycache__", "site-packages",
    ".cargo", ".rustup", ".nuget", ".npm", ".gradle", ".m2", ".vs", ".venv", "$recycle.bin", "system volume information"];
pub const DEFAULT_DEMOTED_AT_ROOT: &[&str] = &["windows", "programdata", "recovery", "$windows.~ws", "$windows.~bt",
    "$winreagent", "msocache"];

#[derive(Serialize, Deserialize, Clone, PartialEq, Debug)]
#[serde(default)]
pub struct Settings {
    pub version: u32,
    /// Drives to index, by volume serial (8 hex digits). Nothing is indexed until a drive is turned on.
    pub drives: Vec<DriveSetting>,
    /// Folders whose contents never show (search, space map); their size still counts in their parents.
    pub excluded: Vec<String>,
    /// Folder names that push everything under them down in search, anywhere on a drive.
    pub demoted: Vec<String>,
    /// The same, only directly under a drive's root (Windows, ProgramData, ...).
    pub demoted_at_root: Vec<String>,
    /// Hidden and system files and folders show in search (pushed down) when true.
    pub search_hidden: bool,
    /// Turn on a drive's USN change journal when it has none (32 MB). Without one a drive can't be followed.
    pub create_journals: bool,
}

#[derive(Serialize, Deserialize, Clone, PartialEq, Debug)]
pub struct DriveSetting {
    pub serial: String,
    /// The letter it had when last seen; informational only.
    #[serde(default)]
    pub label: String,
    pub enabled: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            version: 1,
            drives: Vec::new(),
            excluded: Vec::new(),
            demoted: DEFAULT_DEMOTED.iter().map(|s| s.to_string()).collect(),
            demoted_at_root: DEFAULT_DEMOTED_AT_ROOT.iter().map(|s| s.to_string()).collect(),
            search_hidden: true,
            create_journals: true,
        }
    }
}

pub fn serial_text(serial: u32) -> String {
    format!("{serial:08X}")
}

impl Settings {
    pub fn load(path: &Path) -> Result<Option<Settings>, String> {
        let text = match std::fs::read_to_string(path) {
            Ok(t) => t,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(e) => return Err(format!("can't read {}: {e}", path.display())),
        };
        let mut settings: Settings = serde_json::from_str(&text).map_err(|e| format!("{} is not valid: {e}", path.display()))?;
        settings.normalize()?;
        Ok(Some(settings))
    }

    pub fn save(&self, path: &Path) -> Result<(), String> {
        let temp = path.with_extension("json.tmp");
        let text = serde_json::to_string_pretty(self).map_err(|e| e.to_string())?;
        std::fs::write(&temp, text).map_err(|e| format!("can't write {}: {e}", temp.display()))?;
        std::fs::rename(&temp, path).map_err(|e| format!("can't replace {}: {e}", path.display()))
    }

    pub fn drive_enabled(&self, serial: u32) -> bool {
        let s = serial_text(serial);
        self.drives.iter().any(|d| d.enabled && d.serial.eq_ignore_ascii_case(&s))
    }

    pub fn set_drive(&mut self, serial: u32, letter: char, enabled: bool) {
        let s = serial_text(serial);
        let label = format!("{letter}:");
        match self.drives.iter_mut().find(|d| d.serial.eq_ignore_ascii_case(&s)) {
            Some(d) => { d.enabled = enabled; d.label = label; }
            None => self.drives.push(DriveSetting { serial: s, label, enabled }),
        }
    }

    /// Checks and tidies the values a client sent: paths absolute and without a trailing backslash, no duplicates.
    pub fn normalize(&mut self) -> Result<(), String> {
        for d in &mut self.drives {
            d.serial = d.serial.trim().to_ascii_uppercase();
            if d.serial.len() != 8 || u32::from_str_radix(&d.serial, 16).is_err() {
                return Err(format!("drive serial \"{}\" isn't 8 hex digits", d.serial));
            }
        }
        dedup(&mut self.drives, |d| d.serial.clone());
        let mut excluded = Vec::new();
        for p in &self.excluded {
            excluded.push(normalize_path(p).ok_or_else(|| format!("excluded path \"{p}\" must be a folder like D:\\Private (to skip a whole drive, turn the drive off)"))?);
        }
        dedup(&mut excluded, |p| p.to_lowercase());
        self.excluded = excluded;
        for list in [&mut self.demoted, &mut self.demoted_at_root] {
            for n in list.iter_mut() { *n = n.trim().to_string(); }
            if let Some(bad) = list.iter().find(|n| n.is_empty() || n.contains(['\\', '/'])) {
                return Err(format!("\"{bad}\" isn't a folder name"));
            }
            dedup(list, |n| n.to_lowercase());
        }
        Ok(())
    }
}

fn dedup<T>(list: &mut Vec<T>, key: impl Fn(&T) -> String) {
    let mut seen = std::collections::HashSet::new();
    list.retain(|item| seen.insert(key(item)));
}

/// `d:/Games/` -> `D:\Games`. None for anything that isn't a folder below a drive root.
pub fn normalize_path(path: &str) -> Option<String> {
    let p = path.trim().replace('/', "\\");
    let p = p.trim_end_matches('\\');
    let bytes = p.as_bytes();
    if bytes.len() < 4 || !bytes[0].is_ascii_alphabetic() || bytes[1] != b':' || bytes[2] != b'\\' { return None; }
    if p[3..].split('\\').any(|c| c.is_empty() || c == "." || c == "..") { return None; }
    Some(format!("{}{}", (bytes[0] as char).to_ascii_uppercase(), &p[1..]))
}

/// The lower-cased rules the index applies (from the settings).
#[derive(Default)]
pub struct Rules {
    pub demoted: Vec<String>,
    pub demoted_at_root: Vec<String>,
    /// Lower-cased excluded paths.
    pub excluded: Vec<String>,
}

impl Rules {
    pub fn from(settings: &Settings) -> Rules {
        Rules {
            demoted: settings.demoted.iter().map(|s| s.to_lowercase()).collect(),
            demoted_at_root: settings.demoted_at_root.iter().map(|s| s.to_lowercase()).collect(),
            excluded: settings.excluded.iter().map(|s| s.to_lowercase()).collect(),
        }
    }

    pub fn demotes(&self, name: &str, at_root: bool) -> bool {
        self.demoted.iter().any(|n| name.eq_ignore_ascii_case(n)) || (at_root && self.demoted_at_root.iter().any(|n| name.eq_ignore_ascii_case(n)))
    }

    /// Excluded paths on one drive, lower-cased.
    pub fn excluded_on(&self, letter: char) -> impl Iterator<Item = &str> {
        let prefix = format!("{}:\\", letter.to_ascii_lowercase());
        self.excluded.iter().filter(move |p| p.starts_with(&prefix)).map(String::as_str)
    }
}
