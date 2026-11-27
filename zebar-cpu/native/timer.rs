use std::{
    path::PathBuf,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use windows::{
    Win32::Media::Audio::{PlaySoundW, SND_ALIAS, SND_ASYNC, SND_FLAGS},
    core::{PCWSTR, w},
};

use crate::state_file;

const MAX_SECONDS: u64 = 1_000 * 60 * 60 - 1;
const CHIME_INTERVAL: Duration = Duration::from_secs(4);

#[derive(Clone, Copy, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Timer {
    deadline_ms: u64,
    duration_seconds: u64,
    alarm_at_ms: Option<u64>,
}

pub struct TimerStore {
    path: Option<PathBuf>,
    pub active: Option<Timer>,
    next_chime_at: Option<Instant>,
}

impl TimerStore {
    pub fn load() -> Self {
        let path = state_file::beside_executable("timer.json");
        let active = state_file::read::<Timer>(path.as_deref()).filter(|timer| {
            (1..=MAX_SECONDS).contains(&timer.duration_seconds)
                && (1..=8_640_000_000_000_000).contains(&timer.deadline_ms)
        });
        Self {
            path,
            active,
            next_chime_at: None,
        }
    }

    pub fn start(&mut self, seconds: Option<u64>) -> Result<(), u16> {
        let seconds = seconds
            .filter(|seconds| (1..=MAX_SECONDS).contains(seconds))
            .ok_or(400_u16)?;
        if self.active.is_some() {
            return Err(409);
        }
        self.save(Some(Timer {
            deadline_ms: now_ms() + seconds * 1_000,
            duration_seconds: seconds,
            alarm_at_ms: None,
        }))
    }

    pub fn clear(&mut self, deadline_ms: Option<u64>) -> Result<(), u16> {
        let deadline_ms = deadline_ms.ok_or(400_u16)?;
        let Some(timer) = self.active else {
            return Ok(());
        };
        if timer.deadline_ms != deadline_ms {
            return Err(409);
        }
        self.save(None)?;
        self.next_chime_at = None;
        if timer.alarm_at_ms.is_some() {
            stop_chime();
        }
        Ok(())
    }

    pub fn tick(&mut self, repeat_chime: bool) {
        if !repeat_chime && self.next_chime_at.take().is_some() {
            stop_chime();
        }
        let Some(mut timer) = self.active else { return };
        let first_chime = timer.alarm_at_ms.is_none();
        if first_chime {
            let now = now_ms();
            if now < timer.deadline_ms {
                return;
            }
            timer.alarm_at_ms = Some(now);
            // Persist the first expiry only. Keep the repeat schedule in memory.
            let _ = self.save(Some(timer));
            self.active = Some(timer);
        }

        let now = Instant::now();
        if first_chime || (repeat_chime && self.next_chime_at.is_none_or(|next| now >= next)) {
            play_chime();
            self.next_chime_at = repeat_chime.then_some(now + CHIME_INTERVAL);
        }
    }

    fn save(&mut self, timer: Option<Timer>) -> Result<(), u16> {
        state_file::write(self.path.as_deref(), &timer).map_err(|_| 500_u16)?;
        self.active = timer;
        Ok(())
    }
}

fn play_chime() {
    let _ = unsafe { PlaySoundW(w!("SystemExclamation"), None, SND_ALIAS | SND_ASYNC) };
}

fn stop_chime() {
    let _ = unsafe { PlaySoundW(PCWSTR::null(), None, SND_FLAGS(0)) };
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}
