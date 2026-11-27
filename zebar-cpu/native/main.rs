#![cfg_attr(windows, windows_subsystem = "windows")]

mod drive_io;
mod gpu;
mod pdh;
mod processes;
mod settings;
mod state_file;
mod timer;

use std::{
    collections::VecDeque,
    error::Error,
    io::{self, Read},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use serde::Serialize;
use sysinfo::{
    CpuRefreshKind, MemoryRefreshKind, RefreshKind, System,
};
use tiny_http::{Header, Method, Request, Response, Server};

const ADDRESS: &str = "127.0.0.1:6128";
const ZEBAR_ORIGIN: &str = "http://127.0.0.1:6124";
const SAMPLE_INTERVAL: Duration = Duration::from_secs(1);
const IDLE_TIMEOUT: Duration = Duration::from_secs(20);
const HISTORY_LENGTH: usize = settings::MAX_HISTORY_SECONDS;
const DEFAULT_LIMIT: usize = 60;
const MAX_BODY: u64 = 8 * 1024;
const PROCESS_INTERVAL: Duration = Duration::from_secs(3);
const PROCESS_LIMIT: usize = 10;
const DETACHED_FLAG: &str = "--detached";

#[derive(Serialize)]
struct Sample {
    sequence: u64,
    total: u8,
    usage: Vec<u8>,
    memory: Memory,
    gpu: Vec<gpu::GpuSample>,
}

#[derive(Serialize)]
struct Memory {
    used: u64,
    available: u64,
    total: u64,
}

#[derive(Serialize)]
struct History<'a> {
    session: u64,
    #[serde(rename = "settingsVersion")]
    settings_version: u64,
    timer: Option<timer::Timer>,
    gpus: &'a [gpu::AdapterInfo],
    #[serde(rename = "driveRates")]
    drive_rates: &'a drive_io::Rates,
    samples: Vec<&'a Sample>,
}

struct State {
    session: u64,
    history: VecDeque<Sample>,
    system: System,
    processes: processes::ProcessSampler,
    gpu: Option<gpu::GpuSampler>,
    disk: Option<drive_io::Sampler>,
    drive_rates: drive_io::Rates,
    settings: settings::SettingsStore,
    timer: timer::TimerStore,
}

fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    match std::env::args().nth(1).as_deref() {
        Some("--probe") => return probe(),
        Some(DETACHED_FLAG) => {}
        // Started by Zebar: hand over to a copy that inherits nothing, then exit.
        _ if relaunch_detached().is_ok() => return Ok(()),
        _ => {}
    }

    let server = match Server::http(ADDRESS) {
        Ok(server) => server,
        Err(error)
            if error
                .downcast_ref::<io::Error>()
                .is_some_and(|error| error.kind() == io::ErrorKind::AddrInUse) =>
        {
            // Another bar can race to start the shared sampler.
            return Ok(());
        }
        Err(error) => return Err(error),
    };

    let mut state = State {
        session: SystemTime::now().duration_since(UNIX_EPOCH)?.as_millis() as u64,
        history: VecDeque::with_capacity(HISTORY_LENGTH),
        system: System::new_with_specifics(
            RefreshKind::nothing().with_cpu(CpuRefreshKind::nothing().with_cpu_usage()),
        ),
        processes: processes::ProcessSampler::new(
            HISTORY_LENGTH,
            std::thread::available_parallelism().map_or(1, usize::from),
        ),
        gpu: gpu::GpuSampler::new(),
        disk: drive_io::Sampler::new(),
        drive_rates: drive_io::Rates::new(),
        settings: settings::SettingsStore::load(),
        timer: timer::TimerStore::load(),
    };
    let mut sample_at = Instant::now() + SAMPLE_INTERVAL;
    let mut last_client = Instant::now();
    let mut sequence = 0;
    state.timer.tick(state.settings.settings.repeat_timer_chime);

    while last_client.elapsed() < IDLE_TIMEOUT {
        let now = Instant::now();
        if now >= sample_at {
            state.timer.tick(state.settings.settings.repeat_timer_chime);
            if let Some(disk) = &mut state.disk {
                disk.sample(&mut state.drive_rates);
            }
            let State { system, history, gpu, processes, .. } = &mut state;
            system.refresh_cpu_usage();
            system.refresh_memory_specifics(MemoryRefreshKind::nothing().with_ram());
            processes.sample();
            let usage: Vec<_> = system
                .cpus()
                .iter()
                .map(|cpu| percent(cpu.cpu_usage()))
                .collect();

            if !usage.is_empty() {
                if history.len() == HISTORY_LENGTH {
                    history.pop_front();
                }
                sequence += 1;
                history.push_back(Sample {
                    sequence,
                    total: percent(system.global_cpu_usage()),
                    usage,
                    memory: Memory {
                        used: system.used_memory(),
                        available: system.available_memory(),
                        total: system.total_memory(),
                    },
                    gpu: gpu.as_mut().map(gpu::GpuSampler::sample).unwrap_or_default(),
                });
            }
            sample_at = now + SAMPLE_INTERVAL;
        }

        let timeout = sample_at
            .saturating_duration_since(Instant::now())
            .min(IDLE_TIMEOUT.saturating_sub(last_client.elapsed()));
        if let Some(request) = server.recv_timeout(timeout)?
            && serve_metrics(request, &mut state)?
        {
            last_client = Instant::now();
        }
    }
    Ok(())
}

fn percent(value: f32) -> u8 {
    value.round().clamp(0.0, 100.0) as u8
}

fn serve_metrics(mut request: Request, state: &mut State) -> Result<bool, serde_json::Error> {
    let url = request.url().to_owned();
    let (path, query) = url.split_once('?').unwrap_or((&url, ""));
    if !matches!(
        path,
        "/history" | "/memory/processes" | "/cpu/processes" | "/gpu/processes" | "/settings" | "/settings/save"
            | "/timer" | "/timer/start" | "/timer/clear"
    ) {
        let _ = request.respond(Response::empty(404));
        return Ok(false);
    }

    let header = |name: &str, value: &str| {
        request.headers().iter().any(|header| {
            header.field.as_str().as_str().eq_ignore_ascii_case(name)
                && header.value.as_str() == value
        })
    };
    if !header("Origin", ZEBAR_ORIGIN) || !header("Host", ADDRESS) {
        let _ = request.respond(Response::empty(403));
        return Ok(false);
    }

    let allow_origin = http_header("Access-Control-Allow-Origin", ZEBAR_ORIGIN);
    if request.method() == &Method::Options {
        let _ = request.respond(
            Response::empty(204)
                .with_header(allow_origin)
                .with_header(http_header("Access-Control-Allow-Methods", "POST"))
                .with_header(http_header("Access-Control-Max-Age", "600")),
        );
        return Ok(false);
    }
    if request.method() != &Method::Post {
        let _ = request.respond(Response::empty(405));
        return Ok(false);
    }

    let read_parameter = |name| {
        query
            .split('&')
            .filter_map(|parameter| parameter.split_once('='))
            .find(|(key, _)| *key == name)
            .and_then(|(_, value)| value.parse::<u64>().ok())
    };
    // Seconds of history a widget asked for, capped at what the collector keeps.
    let limit = read_parameter("limit").map_or(DEFAULT_LIMIT, |limit| limit as usize).min(HISTORY_LENGTH);

    let body = if path == "/memory/processes" {
        serde_json::to_vec(&state.processes.top_memory(PROCESS_LIMIT))?
    } else if path == "/cpu/processes" {
        let window = read_parameter("window").map_or(DEFAULT_LIMIT, |window| window as usize).min(HISTORY_LENGTH);
        serde_json::to_vec(&state.processes.cpu_summary(window))?
    } else if path == "/gpu/processes" {
        serde_json::to_vec(state.gpu.as_mut().map_or(&[][..], gpu::GpuSampler::processes))?
    } else if path == "/settings" {
        serde_json::to_vec(&state.settings.snapshot())?
    } else if path == "/settings/save" {
        let mut text = String::new();
        let parsed = request
            .as_reader()
            .take(MAX_BODY)
            .read_to_string(&mut text)
            .ok()
            .and_then(|_| serde_json::from_str::<settings::Settings>(&text).ok());
        let Some(new_settings) = parsed else {
            let _ = request.respond(Response::empty(400).with_header(allow_origin));
            return Ok(true);
        };
        let was_repeating = state.settings.settings.repeat_timer_chime;
        if state.settings.save(new_settings).is_err() {
            let _ = request.respond(Response::empty(500).with_header(allow_origin));
            return Ok(true);
        }
        let repeat_chime = state.settings.settings.repeat_timer_chime;
        if was_repeating != repeat_chime {
            state.timer.tick(repeat_chime);
        }
        serde_json::to_vec(&state.settings.snapshot())?
    } else if matches!(path, "/timer" | "/timer/start" | "/timer/clear") {
        let result = match path {
            "/timer/start" => state.timer.start(read_parameter("seconds")),
            "/timer/clear" => state.timer.clear(read_parameter("deadline")),
            _ => Ok(()),
        };
        if let Err(status) = result {
            let _ = request.respond(Response::empty(status).with_header(allow_origin));
            return Ok(true);
        }
        serde_json::to_vec(&state.timer.active)?
    } else {
        let after = if read_parameter("session") == Some(state.session) {
            read_parameter("after").unwrap_or(0)
        } else {
            0
        };
        let newer: Vec<_> = state.history.iter().filter(|sample| sample.sequence > after).collect();
        let skip = newer.len().saturating_sub(limit);
        serde_json::to_vec(&History {
            session: state.session,
            settings_version: state.settings.version,
            timer: state.timer.active,
            gpus: state.gpu.as_ref().map_or(&[], |gpu| &gpu.adapters),
            drive_rates: &state.drive_rates,
            samples: newer.into_iter().skip(skip).collect(),
        })?
    };
    let sent = request.respond(
        Response::from_data(body)
            .with_header(allow_origin)
            .with_header(http_header("Content-Type", "application/json"))
            .with_header(http_header("Cache-Control", "no-store")),
    );
    Ok(sent.is_ok())
}

fn http_header(name: &str, value: &str) -> Header {
    Header::from_bytes(name, value).expect("valid HTTP header")
}

/// `zebar-cpu-collector.exe --probe > probe.json` prints what the GPU sampler
/// sees, for checking a machine without the bar.
fn probe() -> Result<(), Box<dyn Error + Send + Sync>> {
    let Some(mut gpu) = gpu::GpuSampler::new() else {
        println!("null");
        return Ok(());
    };
    let started = Instant::now();
    std::thread::sleep(SAMPLE_INTERVAL);
    let sample = gpu.sample();
    let sample_time = started.elapsed() - SAMPLE_INTERVAL;
    let processes = serde_json::to_value(gpu.processes())?;
    println!(
        "{}",
        serde_json::to_string_pretty(&serde_json::json!({
            "sampleMillis": sample_time.as_secs_f64() * 1000.0,
            "adapters": gpu.adapters,
            "sample": &sample,
            "processes": processes,
        }))?
    );
    Ok(())
}

/// Zebar starts the collector with handle inheritance on, so the collector
/// would otherwise hold a copy of Zebar's own server socket (127.0.0.1:6124).
/// That copy outlives Zebar and stops a restarted Zebar from serving its
/// widgets (the bar shows up as a grey error page). Starting a second copy
/// with inheritance off, and exiting this one, drops every inherited handle.
fn relaunch_detached() -> windows::core::Result<()> {
    use windows::{
        Win32::{
            Foundation::CloseHandle,
            System::Threading::{
                CREATE_NO_WINDOW, CreateProcessW, DETACHED_PROCESS, PROCESS_INFORMATION,
                STARTUPINFOW,
            },
        },
        core::{HSTRING, PCWSTR, PWSTR},
    };

    let exe = std::env::current_exe().map_err(|_| windows::core::Error::from_thread())?;
    let exe = HSTRING::from(exe.as_os_str());
    let mut command_line: Vec<u16> = format!("\"{exe}\" {DETACHED_FLAG}")
        .encode_utf16()
        .chain([0])
        .collect();
    let startup = STARTUPINFOW { cb: size_of::<STARTUPINFOW>() as u32, ..Default::default() };
    let mut process = PROCESS_INFORMATION::default();
    unsafe {
        CreateProcessW(
            &exe,
            Some(PWSTR(command_line.as_mut_ptr())),
            None,
            None,
            false,
            DETACHED_PROCESS | CREATE_NO_WINDOW,
            None,
            PCWSTR::null(),
            &startup,
            &mut process,
        )?;
        let _ = CloseHandle(process.hThread);
        let _ = CloseHandle(process.hProcess);
    }
    Ok(())
}
