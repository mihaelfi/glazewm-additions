//! Widget settings, kept in `settings.json` next to the collector exe (the
//! widget pack folder). The collector owns the file so every bar and popup
//! reads one copy, and the file can also be edited by hand.

use std::{fs, io, path::PathBuf};

use serde::{Deserialize, Serialize};

/// History lengths offered in the Settings popup, in minutes.
pub const HISTORY_CHOICES: [u32; 4] = [1, 5, 10, 15];
/// Samples the collector keeps: enough for the longest choice.
pub const MAX_HISTORY_SECONDS: usize = 15 * 60;
const FILE_NAME: &str = "settings.json";
const MAX_ADAPTER_NAME: usize = 128;

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    /// How far back graphs, heatmaps and process averages look.
    pub history_minutes: u32,
    pub bar: BarItems,
    /// Drive letters shown in storage. `None` shows every drive, including new ones.
    pub drives: Option<Vec<String>>,
    /// Name of the GPU the bar follows. `None` picks the one with the most video memory.
    pub gpu_adapter: Option<String>,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct BarItems {
    pub storage: bool,
    pub ram: bool,
    pub cpu: bool,
    pub gpu: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self { history_minutes: 1, bar: BarItems::default(), drives: None, gpu_adapter: None }
    }
}

impl Default for BarItems {
    fn default() -> Self {
        Self { storage: true, ram: true, cpu: true, gpu: true }
    }
}

impl Settings {
    /// Fixes values a hand edit or an old file could get wrong.
    fn sanitized(mut self) -> Self {
        if !HISTORY_CHOICES.contains(&self.history_minutes) {
            self.history_minutes = Settings::default().history_minutes;
        }
        let bar = &self.bar;
        // The bar items are the only way into the popups, so never hide them all.
        if !(bar.storage || bar.ram || bar.cpu || bar.gpu) {
            self.bar = BarItems::default();
        }
        if let Some(drives) = &mut self.drives {
            drives.retain(|letter| letter.len() == 1 && letter.chars().all(|c| c.is_ascii_uppercase()));
            drives.sort();
            drives.dedup();
        }
        self.gpu_adapter = self
            .gpu_adapter
            .map(|name| name.trim().chars().take(MAX_ADAPTER_NAME).collect::<String>())
            .filter(|name| !name.is_empty());
        self
    }
}

#[derive(Serialize)]
pub struct Snapshot<'a> {
    version: u64,
    settings: &'a Settings,
}

pub struct SettingsStore {
    path: Option<PathBuf>,
    pub settings: Settings,
    /// Changes on every save, so widgets know to fetch the settings again.
    pub version: u64,
}

impl SettingsStore {
    pub fn load() -> Self {
        let path = std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(|folder| folder.join(FILE_NAME)));
        let settings = path
            .as_ref()
            .and_then(|path| fs::read_to_string(path).ok())
            .and_then(|text| serde_json::from_str::<Settings>(&text).ok())
            .unwrap_or_default()
            .sanitized();
        Self { path, settings, version: 1 }
    }

    pub fn snapshot(&self) -> Snapshot<'_> {
        Snapshot { version: self.version, settings: &self.settings }
    }

    pub fn save(&mut self, settings: Settings) -> io::Result<()> {
        let settings = settings.sanitized();
        let path = self.path.as_ref().ok_or_else(|| io::Error::other("settings path unknown"))?;
        let temp = path.with_extension("json.tmp");
        fs::write(&temp, serde_json::to_string_pretty(&settings)? + "\n")?;
        // Replaces the old file in one step, so a crash never leaves half a file.
        fs::rename(&temp, path)?;
        self.settings = settings;
        self.version += 1;
        Ok(())
    }
}
