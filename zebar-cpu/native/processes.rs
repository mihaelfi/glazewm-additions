//! Per-process CPU and memory, read for all processes at once with
//! `NtQuerySystemInformation(SystemProcessInformation)`, the call Task Manager
//! uses. One system call per second replaces opening every process.
//!
//! Every sample records which processes used CPU, so the CPU panel can show each
//! process's current, average and peak load over the history window, including
//! spikes that already ended.

use std::{
    collections::{HashMap, HashSet, VecDeque},
    ptr,
    time::Instant,
};

use serde::Serialize;
use windows::Wdk::System::SystemInformation::{NtQuerySystemInformation, SystemProcessInformation};

/// Processes below this share of total CPU are left out of a sample.
const MIN_USAGE: f64 = 0.05;
const CPU_RESPONSE_LIMIT: usize = 40;
const STATUS_INFO_LENGTH_MISMATCH: i32 = 0xC000_0004_u32 as i32;

// Byte offsets in SYSTEM_PROCESS_INFORMATION on 64-bit Windows.
const NEXT_ENTRY: usize = 0;
const CREATE_TIME: usize = 32;
const USER_TIME: usize = 40;
const KERNEL_TIME: usize = 48;
const IMAGE_NAME_LENGTH: usize = 56;
const IMAGE_NAME_BUFFER: usize = 64;
const PROCESS_ID: usize = 80;
const WORKING_SET: usize = 144;

/// A process instance: the PID plus its creation time, so a reused PID isn't merged with an old process.
type Key = (u32, i64);

#[derive(Serialize)]
pub struct CpuProcess {
    name: String,
    pid: u32,
    /// Share of total CPU in the latest sample, in percent.
    current: f32,
    /// Mean share of total CPU over the window, counting idle seconds as zero.
    average: f32,
    /// Highest one-second share in the window.
    peak: f32,
}

#[derive(Serialize)]
pub struct CpuProcesses {
    /// Seconds of history the averages cover.
    window: usize,
    processes: Vec<CpuProcess>,
}

#[derive(Serialize)]
pub struct ProcessMemory {
    name: String,
    pid: u32,
    bytes: u64,
}

pub struct ProcessSampler {
    capacity: usize,
    cores: f64,
    buffer: Vec<u64>,
    /// CPU time (100 ns units) per process at the previous sample.
    cpu_times: HashMap<Key, u64>,
    sampled_at: Option<Instant>,
    /// Working set per process at the latest sample.
    memory: Vec<(Key, u64)>,
    /// One entry per sample: processes that used CPU, in hundredths of a percent of total CPU.
    cpu_history: VecDeque<Vec<(Key, u16)>>,
    names: HashMap<Key, String>,
}

impl ProcessSampler {
    pub fn new(capacity: usize, cores: usize) -> Self {
        Self {
            capacity,
            cores: cores.max(1) as f64,
            buffer: Vec::new(),
            cpu_times: HashMap::new(),
            sampled_at: None,
            memory: Vec::new(),
            cpu_history: VecDeque::with_capacity(capacity),
            names: HashMap::new(),
        }
    }

    pub fn sample(&mut self) {
        if !self.query() {
            return;
        }
        let now = Instant::now();
        // CPU times are in 100 ns units.
        let elapsed = self.sampled_at.map(|at| now.duration_since(at).as_nanos() as f64 / 100.0);
        self.sampled_at = Some(now);

        let mut cpu_times = HashMap::with_capacity(self.cpu_times.len());
        let mut memory = Vec::with_capacity(self.memory.len());
        let mut usage = Vec::new();
        let bytes = buffer_bytes(&self.buffer);
        let mut offset = 0;
        loop {
            let entry = &bytes[offset..];
            let pid = read::<usize>(entry, PROCESS_ID) as u32;
            let key = (pid, read::<i64>(entry, CREATE_TIME));
            let cpu_time = read::<i64>(entry, USER_TIME).max(0) as u64 + read::<i64>(entry, KERNEL_TIME).max(0) as u64;

            // PID 0 is the idle process.
            if pid != 0 {
                if !self.names.contains_key(&key) {
                    self.names.insert(key, image_name(entry));
                }
                memory.push((key, read::<usize>(entry, WORKING_SET) as u64));
                if let (Some(elapsed), Some(previous)) = (elapsed, self.cpu_times.get(&key)) {
                    let share = cpu_time.saturating_sub(*previous) as f64 / (elapsed * self.cores) * 100.0;
                    if share >= MIN_USAGE {
                        usage.push((key, (share * 100.0).round().min(f64::from(u16::MAX)) as u16));
                    }
                }
                cpu_times.insert(key, cpu_time);
            }

            let next = read::<u32>(entry, NEXT_ENTRY) as usize;
            if next == 0 {
                break;
            }
            offset += next;
        }

        self.cpu_times = cpu_times;
        self.memory = memory;
        if elapsed.is_some() {
            if self.cpu_history.len() == self.capacity {
                self.cpu_history.pop_front();
            }
            self.cpu_history.push_back(usage);
        }
        // Keep names for running processes and for any still in the CPU window.
        let in_window: HashSet<Key> = self.cpu_history.iter().flatten().map(|(key, _)| *key).collect();
        let cpu_times = &self.cpu_times;
        self.names.retain(|key, _| cpu_times.contains_key(key) || in_window.contains(key));
    }

    /// Current, average and peak CPU per process over the last `window` seconds.
    pub fn cpu_summary(&self, window: usize) -> CpuProcesses {
        let window = window.min(self.cpu_history.len());
        let mut totals: HashMap<Key, (u32, u16)> = HashMap::new();
        for sample in self.cpu_history.iter().rev().take(window) {
            for (key, usage) in sample {
                let entry = totals.entry(*key).or_default();
                entry.0 += u32::from(*usage);
                entry.1 = entry.1.max(*usage);
            }
        }
        let latest: HashMap<Key, u16> = self.cpu_history.back().into_iter().flatten().copied().collect();

        let mut processes: Vec<_> = totals
            .into_iter()
            .map(|(key, (sum, peak))| CpuProcess {
                name: self.name(key),
                pid: key.0,
                current: hundredths(latest.get(&key).copied().unwrap_or(0).into()),
                average: hundredths(sum / window.max(1) as u32),
                peak: hundredths(peak.into()),
            })
            .collect();
        processes.sort_unstable_by(|a, b| {
            b.average.total_cmp(&a.average).then(b.peak.total_cmp(&a.peak)).then(a.pid.cmp(&b.pid))
        });
        processes.truncate(CPU_RESPONSE_LIMIT);
        CpuProcesses { window, processes }
    }

    pub fn top_memory(&self, limit: usize) -> Vec<ProcessMemory> {
        let mut ranked: Vec<_> = self.memory.iter().filter(|(_, bytes)| *bytes > 0).collect();
        ranked.sort_unstable_by_key(|((pid, _), bytes)| (std::cmp::Reverse(*bytes), *pid));
        ranked
            .into_iter()
            .take(limit)
            .map(|(key, bytes)| ProcessMemory { name: self.name(*key), pid: key.0, bytes: *bytes })
            .collect()
    }

    fn name(&self, key: Key) -> String {
        self.names.get(&key).cloned().unwrap_or_else(|| format!("PID {}", key.0))
    }

    /// Fills `buffer` with the process list. Returns false if the call failed.
    fn query(&mut self) -> bool {
        for _ in 0..4 {
            let size = (self.buffer.len() * size_of::<u64>()) as u32;
            let mut needed = 0;
            let status = unsafe {
                NtQuerySystemInformation(
                    SystemProcessInformation,
                    self.buffer.as_mut_ptr().cast(),
                    size,
                    &mut needed,
                )
            };
            if status.0 == STATUS_INFO_LENGTH_MISMATCH || (status.is_ok() && size == 0) {
                // Leave room for processes that start before the next call.
                let words = (needed as usize).div_ceil(size_of::<u64>());
                self.buffer.resize(words + words / 4 + 1024, 0);
                continue;
            }
            return status.is_ok();
        }
        false
    }
}

fn buffer_bytes(buffer: &[u64]) -> &[u8] {
    unsafe { std::slice::from_raw_parts(buffer.as_ptr().cast(), std::mem::size_of_val(buffer)) }
}

fn read<T: Copy>(entry: &[u8], offset: usize) -> T {
    assert!(offset + size_of::<T>() <= entry.len());
    unsafe { ptr::read_unaligned(entry.as_ptr().add(offset).cast()) }
}

/// The image name points into the same buffer the system call filled.
fn image_name(entry: &[u8]) -> String {
    let length = usize::from(read::<u16>(entry, IMAGE_NAME_LENGTH)) / 2;
    let buffer = read::<*const u16>(entry, IMAGE_NAME_BUFFER);
    if length == 0 || buffer.is_null() {
        return String::new();
    }
    String::from_utf16_lossy(unsafe { std::slice::from_raw_parts(buffer, length) })
}

fn hundredths(value: u32) -> f32 {
    // One decimal is enough for display.
    (value as f32 / 10.0).round() / 10.0
}
