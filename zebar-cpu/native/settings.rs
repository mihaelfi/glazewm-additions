//! Widget settings, kept in `settings.json` next to the collector exe (the
//! widget pack folder). The collector owns the file so every bar and popup
//! reads one copy, and the file can also be edited by hand.

use std::{collections::BTreeMap, io, path::PathBuf};

use serde::{Deserialize, Serialize};
use crate::state_file;

/// History lengths offered in the Settings popup, in minutes.
pub const HISTORY_CHOICES: [u32; 4] = [1, 5, 10, 15];
/// Samples the collector keeps: enough for the longest choice.
pub const MAX_HISTORY_SECONDS: usize = 15 * 60;
const FILE_NAME: &str = "settings.json";
const MAX_ADAPTER_NAME: usize = 128;
const DEFAULT_CLOCK_FORMAT: &str = "yyyy-MM-dd HH:mm";

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    /// How far back graphs, heatmaps and process averages look.
    pub history_minutes: u32,
    pub bar: BarItems,
    /// Drive letters shown in storage. `None` shows every drive, including new ones.
    pub drives: Option<Vec<String>>,
    /// Bar activity modes by drive letter. Missing letters show capacity only.
    pub drive_activity: BTreeMap<String, DriveActivity>,
    /// Name of the GPU the bar follows. `None` picks the one with the most video memory.
    pub gpu_adapter: Option<String>,
    pub clock_format: String,
    pub repeat_timer_chime: bool,
}

#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum DriveActivity {
    Marks,
    Rates,
    #[serde(other)]
    None,
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
        Self {
            history_minutes: 1, bar: BarItems::default(), drives: None, gpu_adapter: None,
            drive_activity: BTreeMap::new(),
            clock_format: DEFAULT_CLOCK_FORMAT.into(),
            repeat_timer_chime: true,
        }
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
            drives.retain(|letter| valid_drive_letter(letter));
            drives.sort();
            drives.dedup();
        }
        self.drive_activity.retain(|letter, mode| valid_drive_letter(letter) && *mode != DriveActivity::None);
        self.gpu_adapter = self
            .gpu_adapter
            .map(|name| name.trim().chars().take(MAX_ADAPTER_NAME).collect::<String>())
            .filter(|name| !name.is_empty());
        self.clock_format = self.clock_format.trim().to_owned();
        if self.clock_format.is_empty() || self.clock_format.encode_utf16().count() > 80
            || self.clock_format.chars().any(char::is_control)
            || self.clock_format.matches('\'').count() % 2 != 0
        {
            self.clock_format = DEFAULT_CLOCK_FORMAT.into();
        }
        self
    }
}

fn valid_drive_letter(letter: &str) -> bool {
    matches!(letter.as_bytes(), [letter] if letter.is_ascii_uppercase())
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
        let path = state_file::beside_executable(FILE_NAME);
        let settings = state_file::read::<Settings>(path.as_deref())
            .unwrap_or_default()
            .sanitized();
        Self { path, settings, version: 1 }
    }

    pub fn snapshot(&self) -> Snapshot<'_> {
        Snapshot { version: self.version, settings: &self.settings }
    }

    pub fn save(&mut self, settings: Settings) -> io::Result<()> {
        let settings = settings.sanitized();
        state_file::write(self.path.as_deref(), &settings)?;
        self.settings = settings;
        self.version += 1;
        Ok(())
    }
}
