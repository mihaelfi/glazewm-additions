//! GPU sampling through the same sources Task Manager uses: DXGI for adapter
//! names and memory sizes, the "GPU Engine" / "GPU Adapter Memory" performance
//! counters for load and memory use, and D3DKMT perf data for temperature.

use std::{collections::HashMap, ptr, slice, time::Instant};

use serde::Serialize;
use windows::{
    Wdk::Graphics::Direct3D::{
        D3DKMT_ADAPTER_PERFDATA, D3DKMT_CLOSEADAPTER, D3DKMT_OPENADAPTERFROMLUID,
        D3DKMT_QUERYADAPTERINFO, D3DKMTCloseAdapter, D3DKMTOpenAdapterFromLuid,
        D3DKMTQueryAdapterInfo, KMTQAITYPE_ADAPTERPERFDATA,
    },
    Win32::{
        Foundation::{CloseHandle, LUID},
        Graphics::Dxgi::{CreateDXGIFactory1, DXGI_ADAPTER_FLAG_SOFTWARE, IDXGIFactory1},
        System::Diagnostics::ToolHelp::{
            CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW,
            TH32CS_SNAPPROCESS,
        },
        System::Performance::{
            PDH_FMT, PDH_FMT_COUNTERVALUE_ITEM_W, PDH_FMT_DOUBLE, PDH_FMT_LARGE, PDH_HCOUNTER,
            PDH_HQUERY, PDH_MORE_DATA, PdhAddEnglishCounterW, PdhCloseQuery, PdhCollectQueryData,
            PdhGetFormattedCounterArrayW, PdhOpenQueryW,
        },
    },
    core::{PCWSTR, w},
};

/// Engine groups shown in the panel, in display order.
pub const ENGINE_GROUPS: [&str; 5] = ["3D", "Copy", "Video Decode", "Video Encode", "Compute"];
const PROCESS_LIMIT: usize = 10;
const MICROSOFT_VENDOR: u32 = 0x1414;
const PDH_CSTATUS_VALID_DATA: u32 = 0;
const PDH_CSTATUS_NEW_DATA: u32 = 1;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AdapterInfo {
    name: String,
    dedicated_total: u64,
    shared_total: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuSample {
    usage: u8,
    engines: [u8; ENGINE_GROUPS.len()],
    dedicated_used: u64,
    shared_used: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    temperature: Option<f32>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuProcess {
    adapter: usize,
    pid: u32,
    name: String,
    usage: u8,
    dedicated: u64,
}

struct Adapter {
    /// `0x00000000_0x000160AD`, as it appears in counter instance names.
    luid_key: String,
    kmt: Option<u32>,
}

pub struct GpuSampler {
    pub adapters: Vec<AdapterInfo>,
    devices: Vec<Adapter>,
    query: Query,
    engine: PDH_HCOUNTER,
    dedicated: PDH_HCOUNTER,
    shared: PDH_HCOUNTER,
    process_query: Query,
    process_dedicated: PDH_HCOUNTER,
    process_refreshed_at: Option<Instant>,
    /// Engine load per (adapter, pid), from the latest sample.
    process_load: HashMap<(usize, u32), f64>,
    process_cache: Vec<GpuProcess>,
    buffer: Vec<u64>,
}

struct Query(PDH_HQUERY);

impl Query {
    fn open() -> Option<Self> {
        let mut handle = PDH_HQUERY::default();
        (unsafe { PdhOpenQueryW(PCWSTR::null(), 0, &mut handle) } == 0).then_some(Self(handle))
    }

    fn add(&self, path: PCWSTR) -> Option<PDH_HCOUNTER> {
        let mut counter = PDH_HCOUNTER::default();
        (unsafe { PdhAddEnglishCounterW(self.0, path, 0, &mut counter) } == 0).then_some(counter)
    }

    fn collect(&self) -> bool {
        unsafe { PdhCollectQueryData(self.0) == 0 }
    }
}

impl Drop for Query {
    fn drop(&mut self) {
        unsafe { PdhCloseQuery(self.0) };
    }
}

impl GpuSampler {
    pub fn new() -> Option<Self> {
        let (mut adapters, mut devices) = enumerate_adapters();
        let query = Query::open()?;
        let engine = query.add(w!("\\GPU Engine(*)\\Utilization Percentage"))?;
        let dedicated = query.add(w!("\\GPU Adapter Memory(*)\\Dedicated Usage"))?;
        let shared = query.add(w!("\\GPU Adapter Memory(*)\\Shared Usage"))?;
        // Rate counters need a first reading before they produce values.
        query.collect();

        // DXGI can list extra entries for one card (for example behind a virtual
        // display driver) that have no counters. Keep the adapters Windows tracks.
        let mut buffer = Vec::new();
        let mut tracked = Vec::new();
        for_each_value(dedicated, PDH_FMT_LARGE, &mut buffer, |name, _| tracked.push(name.to_owned()));
        if !tracked.is_empty() {
            let keep: Vec<bool> = devices
                .iter()
                .map(|device| tracked.iter().any(|name| name.contains(&device.luid_key)))
                .collect();
            let mut flags = keep.iter();
            adapters.retain(|_| *flags.next().unwrap());
            let mut flags = keep.iter();
            devices.retain(|_| *flags.next().unwrap());
        }
        if adapters.is_empty() {
            return None;
        }

        let process_query = Query::open()?;
        let process_dedicated = process_query.add(w!("\\GPU Process Memory(*)\\Dedicated Usage"))?;

        Some(Self {
            adapters,
            devices,
            query,
            engine,
            dedicated,
            shared,
            process_query,
            process_dedicated,
            process_refreshed_at: None,
            process_load: HashMap::new(),
            process_cache: Vec::new(),
            buffer,
        })
    }

    pub fn sample(&mut self) -> Vec<GpuSample> {
        let mut samples: Vec<_> = self
            .devices
            .iter()
            .map(|device| GpuSample {
                usage: 0,
                engines: [0; ENGINE_GROUPS.len()],
                dedicated_used: 0,
                shared_used: 0,
                temperature: device.kmt.and_then(temperature),
            })
            .collect();
        if !self.query.collect() {
            return samples;
        }

        // Each instance is one process on one engine. Sum processes per engine,
        // as Task Manager does, then report the busiest engine of each group.
        let mut engines: HashMap<(usize, u32, u32), (Option<usize>, f64)> = HashMap::new();
        let mut process_load = HashMap::new();
        let devices = &self.devices;
        let mut buffer = std::mem::take(&mut self.buffer);
        for_each_value(self.engine, PDH_FMT_DOUBLE, &mut buffer, |name, value| {
            let value = unsafe { value.Anonymous.doubleValue };
            let Some(instance) = EngineInstance::parse(name) else { return };
            let Some(adapter) = devices.iter().position(|device| device.luid_key == instance.luid) else {
                return;
            };
            let entry = engines
                .entry((adapter, instance.phys, instance.engine))
                .or_insert((engine_group(instance.engine_type), 0.0));
            entry.1 += value;
            let process: &mut f64 = process_load.entry((adapter, instance.pid)).or_default();
            *process = process.max(value);
        });
        for ((adapter, _, _), (group, load)) in engines {
            let load = percent(load);
            let sample = &mut samples[adapter];
            sample.usage = sample.usage.max(load);
            if let Some(group) = group {
                sample.engines[group] = sample.engines[group].max(load);
            }
        }
        self.process_load = process_load;

        for (counter, shared) in [(self.dedicated, false), (self.shared, true)] {
            for_each_value(counter, PDH_FMT_LARGE, &mut buffer, |name, value| {
                let bytes = unsafe { value.Anonymous.largeValue }.max(0) as u64;
                let Some(adapter) = devices.iter().position(|device| name.contains(&device.luid_key))
                else {
                    return;
                };
                let sample = &mut samples[adapter];
                if shared {
                    sample.shared_used += bytes;
                } else {
                    sample.dedicated_used += bytes;
                }
            });
        }
        self.buffer = buffer;
        samples
    }

    /// Processes using the GPU, from the latest engine sample plus their
    /// dedicated memory. Refreshed at most every `PROCESS_INTERVAL`.
    pub fn processes(&mut self) -> &[GpuProcess] {
        if self
            .process_refreshed_at
            .is_some_and(|last| last.elapsed() < super::PROCESS_INTERVAL)
        {
            return &self.process_cache;
        }
        self.process_refreshed_at = Some(Instant::now());

        let mut memory: HashMap<(usize, u32), u64> = HashMap::new();
        if self.process_query.collect() {
            let devices = &self.devices;
            let mut buffer = std::mem::take(&mut self.buffer);
            for_each_value(self.process_dedicated, PDH_FMT_LARGE, &mut buffer, |name, value| {
                let Some(pid) = parse_pid(name) else { return };
                let Some(adapter) = devices.iter().position(|device| name.contains(&device.luid_key))
                else {
                    return;
                };
                *memory.entry((adapter, pid)).or_default() +=
                    unsafe { value.Anonymous.largeValue }.max(0) as u64;
            });
            self.buffer = buffer;
        }

        let mut keys: Vec<_> = self
            .process_load
            .iter()
            .filter(|(_, load)| **load >= 0.5)
            .map(|(key, _)| *key)
            .chain(memory.iter().filter(|(_, bytes)| **bytes > 0).map(|(key, _)| *key))
            .filter(|(_, pid)| *pid != 0)
            .collect();
        keys.sort_unstable();
        keys.dedup();
        let mut ranked: Vec<_> = keys
            .into_iter()
            .map(|key| {
                let usage = percent(self.process_load.get(&key).copied().unwrap_or(0.0));
                (key, usage, memory.get(&key).copied().unwrap_or(0))
            })
            .collect();
        ranked.sort_unstable_by(|a, b| b.1.cmp(&a.1).then(b.2.cmp(&a.2)));

        // Keep the top entries for each adapter, so the panel can show any of them.
        let mut kept = vec![0; self.adapters.len()];
        ranked.retain(|((adapter, _), _, _)| {
            kept[*adapter] += 1;
            kept[*adapter] <= PROCESS_LIMIT
        });
        let names = process_names();
        self.process_cache = ranked
            .into_iter()
            .map(|((adapter, pid), usage, dedicated)| GpuProcess {
                adapter,
                pid,
                name: names.get(&pid).cloned().unwrap_or_else(|| format!("PID {pid}")),
                usage,
                dedicated,
            })
            .collect();
        &self.process_cache
    }
}

/// Executable names for all processes. A Toolhelp snapshot also names
/// protected processes such as dwm.exe, which can't be opened for querying.
fn process_names() -> HashMap<u32, String> {
    let mut names = HashMap::new();
    let Ok(snapshot) = (unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) }) else {
        return names;
    };
    let mut entry = PROCESSENTRY32W { dwSize: size_of::<PROCESSENTRY32W>() as u32, ..Default::default() };
    let mut more = unsafe { Process32FirstW(snapshot, &mut entry) }.is_ok();
    while more {
        let length = entry.szExeFile.iter().position(|&c| c == 0).unwrap_or(entry.szExeFile.len());
        names.insert(entry.th32ProcessID, String::from_utf16_lossy(&entry.szExeFile[..length]));
        more = unsafe { Process32NextW(snapshot, &mut entry) }.is_ok();
    }
    let _ = unsafe { CloseHandle(snapshot) };
    names
}

impl Drop for GpuSampler {
    fn drop(&mut self) {
        for device in &self.devices {
            if let Some(handle) = device.kmt {
                let close = D3DKMT_CLOSEADAPTER { hAdapter: handle };
                let _ = unsafe { D3DKMTCloseAdapter(&close) };
            }
        }
    }
}

fn enumerate_adapters() -> (Vec<AdapterInfo>, Vec<Adapter>) {
    let mut found = Vec::new();
    let Ok(factory) = (unsafe { CreateDXGIFactory1::<IDXGIFactory1>() }) else {
        return (Vec::new(), Vec::new());
    };
    for index in 0.. {
        let Ok(adapter) = (unsafe { factory.EnumAdapters1(index) }) else { break };
        let Ok(desc) = (unsafe { adapter.GetDesc1() }) else { continue };
        if desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32 != 0 || desc.VendorId == MICROSOFT_VENDOR {
            continue;
        }
        let length = desc.Description.iter().position(|&c| c == 0).unwrap_or(desc.Description.len());
        let name = String::from_utf16_lossy(&desc.Description[..length]).trim().to_owned();
        found.push((desc.AdapterLuid, name, desc.DedicatedVideoMemory as u64, desc.SharedSystemMemory as u64));
    }
    // Largest dedicated memory first: on a desktop that's the discrete card.
    found.sort_by(|a, b| b.2.cmp(&a.2));
    found.dedup_by(|a, b| a.0 == b.0);

    found
        .into_iter()
        .map(|(luid, name, dedicated_total, shared_total)| {
            (
                AdapterInfo { name, dedicated_total, shared_total },
                Adapter { luid_key: luid_key(luid), kmt: open_kmt(luid) },
            )
        })
        .unzip()
}

fn luid_key(luid: LUID) -> String {
    // Counter instance names use upper-case hex digits.
    format!("0x{:08X}_0x{:08X}", luid.HighPart as u32, luid.LowPart)
}

fn open_kmt(luid: LUID) -> Option<u32> {
    let mut open = D3DKMT_OPENADAPTERFROMLUID { AdapterLuid: luid, hAdapter: 0 };
    let status = unsafe { D3DKMTOpenAdapterFromLuid(&mut open) };
    (status.is_ok() && temperature(open.hAdapter).is_some()).then_some(open.hAdapter).or_else(|| {
        if status.is_ok() {
            let close = D3DKMT_CLOSEADAPTER { hAdapter: open.hAdapter };
            let _ = unsafe { D3DKMTCloseAdapter(&close) };
        }
        None
    })
}

fn temperature(handle: u32) -> Option<f32> {
    let mut data = D3DKMT_ADAPTER_PERFDATA::default();
    let mut query = D3DKMT_QUERYADAPTERINFO {
        hAdapter: handle,
        Type: KMTQAITYPE_ADAPTERPERFDATA,
        pPrivateDriverData: ptr::from_mut(&mut data).cast(),
        PrivateDriverDataSize: size_of::<D3DKMT_ADAPTER_PERFDATA>() as u32,
    };
    let status = unsafe { D3DKMTQueryAdapterInfo(&mut query) };
    // Reported in tenths of a degree; zero means the driver doesn't provide it.
    (status.is_ok() && data.Temperature > 0).then(|| data.Temperature as f32 / 10.0)
}

struct EngineInstance<'a> {
    pid: u32,
    luid: &'a str,
    phys: u32,
    engine: u32,
    engine_type: &'a str,
}

impl<'a> EngineInstance<'a> {
    /// Parses `pid_4772_luid_0x00000000_0x000160ad_phys_0_eng_2_engtype_VideoDecode`.
    fn parse(name: &'a str) -> Option<Self> {
        let rest = name.strip_prefix("pid_")?;
        let (pid, rest) = rest.split_once("_luid_")?;
        let (luid, rest) = rest.split_once("_phys_")?;
        let (phys, rest) = rest.split_once("_eng_")?;
        let (engine, engine_type) = rest.split_once("_engtype_")?;
        Some(Self {
            pid: pid.parse().ok()?,
            luid,
            phys: phys.parse().ok()?,
            engine: engine.parse().ok()?,
            engine_type,
        })
    }
}

fn parse_pid(name: &str) -> Option<u32> {
    name.strip_prefix("pid_")?.split_once('_')?.0.parse().ok()
}

fn engine_group(engine_type: &str) -> Option<usize> {
    let kind = engine_type.to_ascii_lowercase();
    let group = match kind.as_str() {
        "3d" => 0,
        "copy" => 1,
        "videodecode" => 2,
        "videoencode" => 3,
        _ if kind.starts_with("compute") || kind == "cuda" => 4,
        _ => return None,
    };
    Some(group)
}

fn percent(value: f64) -> u8 {
    value.round().clamp(0.0, 100.0) as u8
}

/// Reads every instance of a wildcard counter. `buffer` is reused between calls.
fn for_each_value(
    counter: PDH_HCOUNTER,
    format: PDH_FMT,
    buffer: &mut Vec<u64>,
    mut visit: impl FnMut(&str, &windows::Win32::System::Performance::PDH_FMT_COUNTERVALUE),
) {
    for _ in 0..3 {
        let mut size = (buffer.len() * size_of::<u64>()) as u32;
        let mut count = 0;
        let items = buffer.as_mut_ptr().cast::<PDH_FMT_COUNTERVALUE_ITEM_W>();
        let status = unsafe {
            PdhGetFormattedCounterArrayW(
                counter,
                format,
                &mut size,
                &mut count,
                (!buffer.is_empty()).then_some(items),
            )
        };
        if status == PDH_MORE_DATA {
            buffer.resize((size as usize).div_ceil(size_of::<u64>()) + 64, 0);
            continue;
        }
        if status != 0 {
            return;
        }
        let items = unsafe { slice::from_raw_parts(items, count as usize) };
        for item in items {
            let status = item.FmtValue.CStatus;
            if status != PDH_CSTATUS_VALID_DATA && status != PDH_CSTATUS_NEW_DATA {
                continue;
            }
            let Ok(name) = (unsafe { item.szName.to_string() }) else { continue };
            visit(&name, &item.FmtValue);
        }
        return;
    }
}
