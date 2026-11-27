#![cfg_attr(windows, windows_subsystem = "windows")]
//! GlazeSupervisor keeps GlazeLauncher running. GlazeWM starts it instead of the launcher.
//!
//! It starts `GlazeLauncher.exe --detached --supervised` from its own folder and waits on the process:
//! - exit code 0 (Quit, or `--quit` before a deploy): the supervisor exits too;
//! - any other exit (a crash, or killed): it starts the launcher again at once, with a growing delay
//!   if crashes repeat (2 s, 10 s, then 60 s within two minutes);
//! - every 2 s it asks Windows whether the launcher's windows are hung (`IsHungAppWindow`, which means
//!   no message processing for 5 s); three hung checks in a row end the process and it restarts.
//!
//! The hotkeys stay in the launcher, so key presses never pass through this process. A launcher that is
//! already running when the supervisor starts (started by hand, or the supervisor itself was restarted)
//! is asked to quit and replaced by a supervised one.
//! The log is `%LOCALAPPDATA%\GlazeLauncher\supervisor.log`.

use std::{
    fs::OpenOptions,
    io::Write,
    path::{Path, PathBuf},
    thread::sleep,
    time::{Duration, Instant},
};

use windows::{
    Win32::{
        Foundation::{CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HWND, LPARAM, WAIT_OBJECT_0},
        System::{
            Diagnostics::ToolHelp::{CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW, TH32CS_SNAPPROCESS},
            SystemInformation::GetLocalTime,
            Threading::{
                CREATE_NO_WINDOW, CreateMutexW, CreateProcessW, DETACHED_PROCESS, GetExitCodeProcess, OpenProcess,
                PROCESS_INFORMATION, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE, PROCESS_TERMINATE,
                STARTUPINFOW, TerminateProcess, WaitForSingleObject,
            },
        },
        UI::WindowsAndMessaging::{EnumWindows, GetWindowThreadProcessId, IsHungAppWindow},
    },
    core::{BOOL, HSTRING, PCWSTR, PWSTR, w},
};

const DETACHED_FLAG: &str = "--detached";
const LAUNCHER_EXE: &str = "GlazeLauncher.exe";
const LAUNCHER_PIPE: &str = r"\\.\pipe\GlazeLauncher";
const CHECK_INTERVAL_MS: u32 = 2000;
const HUNG_CHECKS_BEFORE_KILL: u32 = 3;
const KILLED_HUNG_EXIT_CODE: u32 = 0xDEAD;

fn main() {
    // GlazeWM's shell-exec starts children with handle inheritance on. Like the launcher and the Zebar
    // collector, relaunch once without inherited handles so we never keep GlazeWM's sockets alive.
    if !std::env::args().any(|arg| arg == DETACHED_FLAG) {
        if let Err(e) = start_detached(&std::env::current_exe().unwrap_or_default(), DETACHED_FLAG, true) {
            log(&format!("detached relaunch failed: {e}"));
        }
        return;
    }

    // One supervisor at a time. The handle stays open for the life of the process.
    let _mutex = unsafe { CreateMutexW(None, true, w!("Local\\GlazeLauncher.Supervisor")) };
    if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
        return;
    }

    let launcher = std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|dir| dir.join(LAUNCHER_EXE)))
        .unwrap_or_else(|| PathBuf::from(LAUNCHER_EXE));
    trim_log();
    log(&format!("starting (pid {}), watching {}", std::process::id(), launcher.display()));

    replace_unsupervised();

    let mut crashes: Vec<Instant> = Vec::new();
    loop {
        let process = match start_detached(&launcher, "--detached --supervised", false) {
            Ok(process) => process,
            Err(e) => {
                log(&format!("could not start the launcher: {e}; retrying in 10 s"));
                sleep(Duration::from_secs(10));
                continue;
            }
        };
        log(&format!("launcher started (pid {})", process.dwProcessId));

        let code = watch(&process);
        unsafe {
            let _ = CloseHandle(process.hProcess);
            let _ = CloseHandle(process.hThread);
        }
        if code == 0 {
            log("launcher quit normally; supervisor exits");
            return;
        }

        let now = Instant::now();
        crashes.retain(|at| now.duration_since(*at) < Duration::from_secs(120));
        crashes.push(now);
        let delay = match crashes.len() {
            1 => 0,
            2 => 2,
            3 => 10,
            _ => 60,
        };
        log(&format!(
            "launcher ended with code 0x{code:X} ({} in the last 2 min); restarting in {delay} s",
            crashes.len()
        ));
        sleep(Duration::from_secs(delay));
    }
}

/// Waits for the launcher to exit, checking for a hang every 2 s. Returns its exit code.
fn watch(process: &PROCESS_INFORMATION) -> u32 {
    let mut hung_checks = 0;
    loop {
        if unsafe { WaitForSingleObject(process.hProcess, CHECK_INTERVAL_MS) } == WAIT_OBJECT_0 {
            let mut code = 1u32;
            let _ = unsafe { GetExitCodeProcess(process.hProcess, &mut code) };
            return code;
        }

        if has_hung_window(process.dwProcessId) {
            hung_checks += 1;
            log(&format!("launcher not responding ({hung_checks}/{HUNG_CHECKS_BEFORE_KILL})"));
            if hung_checks >= HUNG_CHECKS_BEFORE_KILL {
                log("ending the hung launcher");
                let _ = unsafe { TerminateProcess(process.hProcess, KILLED_HUNG_EXIT_CODE) };
                let _ = unsafe { WaitForSingleObject(process.hProcess, 5000) };
                return KILLED_HUNG_EXIT_CODE;
            }
        } else {
            hung_checks = 0;
        }
    }
}

/// Whether any top-level window of the process is "not responding" in Windows' sense.
fn has_hung_window(pid: u32) -> bool {
    struct Search {
        pid: u32,
        hung: bool,
    }

    unsafe extern "system" fn visit(hwnd: HWND, lparam: LPARAM) -> BOOL {
        let search = unsafe { &mut *(lparam.0 as *mut Search) };
        let mut owner = 0u32;
        unsafe { GetWindowThreadProcessId(hwnd, Some(&mut owner)) };
        if owner == search.pid && unsafe { IsHungAppWindow(hwnd) }.as_bool() {
            search.hung = true;
            return BOOL(0); // stop
        }
        BOOL(1)
    }

    let mut search = Search { pid, hung: false };
    let _ = unsafe { EnumWindows(Some(visit), LPARAM(&mut search as *mut Search as isize)) };
    search.hung
}

/// Asks any launcher already running to quit (over its pipe), and ends it if it hasn't after 5 s.
fn replace_unsupervised() {
    let running = find_processes(LAUNCHER_EXE);
    if running.is_empty() {
        return;
    }
    log(&format!("replacing {} running launcher(s) with a supervised one", running.len()));

    if let Ok(mut pipe) = OpenOptions::new().write(true).open(LAUNCHER_PIPE) {
        let _ = pipe.write_all(b"quit\n");
    }
    for pid in running {
        let access = PROCESS_SYNCHRONIZE | PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION;
        let Ok(handle) = (unsafe { OpenProcess(access, false, pid) }) else { continue };
        if unsafe { WaitForSingleObject(handle, 5000) } != WAIT_OBJECT_0 {
            log(&format!("launcher {pid} didn't quit; ending it"));
            let _ = unsafe { TerminateProcess(handle, 1) };
            let _ = unsafe { WaitForSingleObject(handle, 5000) };
        }
        let _ = unsafe { CloseHandle(handle) };
    }
}

fn find_processes(exe_name: &str) -> Vec<u32> {
    let mut pids = Vec::new();
    let Ok(snapshot) = (unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) }) else { return pids };
    let mut entry = PROCESSENTRY32W { dwSize: size_of::<PROCESSENTRY32W>() as u32, ..Default::default() };
    let mut ok = unsafe { Process32FirstW(snapshot, &mut entry) }.is_ok();
    while ok {
        let len = entry.szExeFile.iter().position(|&c| c == 0).unwrap_or(entry.szExeFile.len());
        if String::from_utf16_lossy(&entry.szExeFile[..len]).eq_ignore_ascii_case(exe_name) {
            pids.push(entry.th32ProcessID);
        }
        ok = unsafe { Process32NextW(snapshot, &mut entry) }.is_ok();
    }
    let _ = unsafe { CloseHandle(snapshot) };
    pids
}

/// Starts `exe` with `args` and no inherited handles. With `close`, the handles are closed and a
/// zeroed PROCESS_INFORMATION is returned.
fn start_detached(exe: &Path, args: &str, close: bool) -> windows::core::Result<PROCESS_INFORMATION> {
    let exe_w = HSTRING::from(exe.as_os_str());
    let mut command_line: Vec<u16> = format!("\"{}\" {args}", exe.display()).encode_utf16().chain([0]).collect();
    let directory = exe.parent().map(|dir| HSTRING::from(dir.as_os_str()));
    let startup = STARTUPINFOW { cb: size_of::<STARTUPINFOW>() as u32, ..Default::default() };
    let mut process = PROCESS_INFORMATION::default();
    unsafe {
        CreateProcessW(
            &exe_w,
            Some(PWSTR(command_line.as_mut_ptr())),
            None,
            None,
            false,
            DETACHED_PROCESS | CREATE_NO_WINDOW,
            None,
            directory.as_ref().map_or(PCWSTR::null(), |dir| PCWSTR(dir.as_ptr())),
            &startup,
            &mut process,
        )?;
    }
    if close {
        unsafe {
            let _ = CloseHandle(process.hProcess);
            let _ = CloseHandle(process.hThread);
        }
        return Ok(PROCESS_INFORMATION::default());
    }
    Ok(process)
}

fn log_path() -> Option<PathBuf> {
    std::env::var_os("LOCALAPPDATA").map(|dir| PathBuf::from(dir).join("GlazeLauncher").join("supervisor.log"))
}

fn trim_log() {
    if let Some(path) = log_path() {
        if std::fs::metadata(&path).is_ok_and(|meta| meta.len() > 256 * 1024) {
            let _ = std::fs::remove_file(path);
        }
    }
}

fn log(message: &str) {
    let Some(path) = log_path() else { return };
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(path) {
        let _ = writeln!(file, "{} {message}", local_time());
    }
}

/// "yyyy-MM-dd HH:mm:ss" in local time, without pulling in a date crate.
fn local_time() -> String {
    let t = unsafe { GetLocalTime() };
    format!("{:04}-{:02}-{:02} {:02}:{:02}:{:02}", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond)
}
