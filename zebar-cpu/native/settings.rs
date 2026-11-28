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
const DEFAULT_BAR_HEIGHT: u32 = 38;
const DEFAULT_BAR_ORDER: [&str; 8] = [
    "storage", "ram", "cpu", "gpu", "tilingDirection", "weather", "language", "network",
];

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    /// How far back graphs, heatmaps and process averages look.
    pub history_minutes: u32,
    pub bar_height: u32,
    pub bar: BarItems,
    pub bar_order: Vec<String>,
    pub language_display: LanguageDisplay,
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

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum LanguageDisplay {
    Code,
    Flag,
    #[default]
    #[serde(other)]
    CodeAndFlag,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct BarItems {
    pub storage: bool,
    pub ram: bool,
    pub cpu: bool,
    pub gpu: bool,
    pub network: bool,
    pub weather: bool,
    pub language: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            history_minutes: 1, bar_height: DEFAULT_BAR_HEIGHT, bar: BarItems::default(), drives: None, gpu_adapter: None,
            bar_order: DEFAULT_BAR_ORDER.map(str::to_owned).into(),
            language_display: LanguageDisplay::default(),
            drive_activity: BTreeMap::new(),
            clock_format: DEFAULT_CLOCK_FORMAT.into(),
            repeat_timer_chime: true,
        }
    }
}

impl Default for BarItems {
    fn default() -> Self {
        Self { storage: true, ram: true, cpu: true, gpu: true, network: true, weather: true, language: true }
    }
}

impl Settings {
    /// Fixes values a hand edit or an old file could get wrong.
    fn sanitized(mut self) -> Self {
        self.bar_height = self.bar_height.clamp(34, 52);
        if !HISTORY_CHOICES.contains(&self.history_minutes) {
            self.history_minutes = Settings::default().history_minutes;
        }
        let bar = &self.bar;
        // Keep access to the metric popups independently of weather and language.
        if !(bar.storage || bar.ram || bar.cpu || bar.gpu || bar.network) {
            self.bar = BarItems { weather: bar.weather, language: bar.language, ..BarItems::default() };
        }
        let mut order = Vec::with_capacity(DEFAULT_BAR_ORDER.len());
        for item in self.bar_order.into_iter().chain(DEFAULT_BAR_ORDER.map(str::to_owned)) {
            if DEFAULT_BAR_ORDER.contains(&item.as_str()) && !order.contains(&item) {
                order.push(item);
            }
        }
        self.bar_order = order;
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn older_settings_keep_the_default_order_and_existing_visibility() {
        let settings = serde_json::from_str::<Settings>(r#"{"bar":{"gpu":false,"network":false}}"#)
            .unwrap()
            .sanitized();
        assert_eq!(settings.bar_order, DEFAULT_BAR_ORDER);
        assert!(!settings.bar.gpu && !settings.bar.network);
        assert!(settings.bar.weather && settings.bar.language);
        assert_eq!(settings.language_display, LanguageDisplay::CodeAndFlag);
    }

    #[test]
    fn language_display_round_trips_and_falls_back_for_unknown_modes() {
        for (stored, mode) in [
            ("code", LanguageDisplay::Code),
            ("flag", LanguageDisplay::Flag),
            ("codeAndFlag", LanguageDisplay::CodeAndFlag),
            ("unknown", LanguageDisplay::CodeAndFlag),
        ] {
            let input = serde_json::json!({ "languageDisplay": stored, "bar": { "language": false } });
            let settings = serde_json::from_value::<Settings>(input).unwrap().sanitized();
            assert_eq!(settings.language_display, mode);
            assert!(!settings.bar.language);
            let saved = serde_json::to_value(&settings).unwrap();
            let restored = serde_json::from_value::<Settings>(saved).unwrap();
            assert_eq!(restored.language_display, mode);
        }
    }

    #[test]
    fn bar_order_ignores_unknown_items_and_restores_missing_ones() {
        let settings = serde_json::from_str::<Settings>(
            r#"{"barOrder":["language","gpu","network","gpu","unknown"],"bar":{"language":false}}"#,
        )
        .unwrap()
        .sanitized();
        assert_eq!(
            settings.bar_order,
            ["language", "gpu", "network", "storage", "ram", "cpu", "tilingDirection", "weather"]
        );
        assert!(!settings.bar.language);
    }

    #[test]
    fn layouts_saved_before_network_was_movable_append_it() {
        let settings = serde_json::from_str::<Settings>(
            r#"{"barOrder":["language","gpu","storage","ram","cpu","tilingDirection","weather"]}"#,
        )
        .unwrap()
        .sanitized();
        assert_eq!(
            settings.bar_order,
            ["language", "gpu", "storage", "ram", "cpu", "tilingDirection", "weather", "network"]
        );
    }

    #[test]
    fn keeping_metric_panels_accessible_preserves_indicator_visibility() {
        let settings = serde_json::from_str::<Settings>(
            r#"{"bar":{"storage":false,"ram":false,"cpu":false,"gpu":false,"network":false,"weather":false,"language":false}}"#,
        )
        .unwrap()
        .sanitized();
        assert!(settings.bar.storage && settings.bar.ram && settings.bar.cpu && settings.bar.gpu && settings.bar.network);
        assert!(!settings.bar.weather && !settings.bar.language);
    }
}
