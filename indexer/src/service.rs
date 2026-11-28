//! Windows service plumbing, and installing/uninstalling the service.
//!
//! The service runs as LocalSystem (reading volumes needs admin) from `%ProgramFiles%\GlazeIndex\`, a folder only
//! administrators can write: a service binary in a user-writable folder would let any program running as the user get
//! SYSTEM rights by replacing it. Its data folder `%ProgramData%\GlazeIndex\` is created with a protected ACL for the
//! same reason (a user could otherwise plant links there); snapshots in `data\` are readable by administrators only,
//! since they list every file name on the drives. The log is readable by users.

use std::path::PathBuf;
use std::ptr::{null, null_mut};
use std::sync::atomic::{AtomicIsize, Ordering};
use std::sync::{mpsc, Mutex};
use std::time::{Duration, Instant};

use crate::win::{self, Sddl, ServiceStatus};
use crate::{engine::Engine, log, pipe};

/// Prints a line with the local time, so install output shows when each step happened.
macro_rules! say {
    ($($arg:tt)*) => {{
        let mut t = win::SystemTime::default();
        unsafe { win::GetLocalTime(&mut t) };
        println!("{:02}:{:02}:{:02} {}", t.hour, t.minute, t.second, format!($($arg)*));
    }};
}

pub const NAME: &str = "GlazeIndex";
const DISPLAY: &str = "GlazeIndex (file search index)";
const DESCRIPTION: &str = "Keeps an index of file and folder names and sizes on internal NTFS drives, up to date from the \
    NTFS change journal, for GlazeLauncher's file search and Zebar's storage view. Answers on \\\\.\\pipe\\GlazeIndex.";
const BASE_SDDL: &str = "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";
const DATA_SDDL: &str = "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

pub fn base_folder() -> PathBuf {
    PathBuf::from(std::env::var("ProgramData").unwrap_or_else(|_| r"C:\ProgramData".into())).join("GlazeIndex")
}

pub fn data_folder() -> PathBuf { base_folder().join("data") }

fn install_folder() -> PathBuf {
    let program_files = std::env::var("ProgramW6432").or_else(|_| std::env::var("ProgramFiles")).unwrap_or_else(|_| r"C:\Program Files".into());
    PathBuf::from(program_files).join("GlazeIndex")
}

fn create_dir_with(path: &PathBuf, sddl: &str) -> Result<(), String> {
    if path.is_dir() { return Ok(()); }
    let descriptor = Sddl::new(sddl).ok_or("bad SDDL")?;
    let attributes = descriptor.attributes();
    let wide = win::wide(&path.to_string_lossy());
    if unsafe { win::CreateDirectoryW(wide.as_ptr(), &attributes) } == 0 {
        return Err(format!("can't create {}: error {}", path.display(), win::last_error()));
    }
    Ok(())
}

pub fn ensure_folders() -> Result<(), String> {
    create_dir_with(&base_folder(), BASE_SDDL)?;
    create_dir_with(&data_folder(), DATA_SDDL)
}

/// Starts the engine and the pipe server; used by the service and by console mode.
pub fn run_engine(echo: bool) -> std::sync::Arc<Engine> {
    if let Err(e) = ensure_folders() { eprintln!("{e}"); }
    log::init(base_folder().join("index.log"), echo);
    log!("starting GlazeIndex {}, build of {} (pid {})", env!("CARGO_PKG_VERSION"), win::build_time(), std::process::id());
    let engine = Engine::start(base_folder(), data_folder());
    let for_pipe = engine.clone();
    std::thread::Builder::new().name("pipe".into()).spawn(move || pipe::serve(for_pipe)).expect("spawn pipe server");
    engine
}

// ---- running as a service -----------------------------------------------------------------------------------------

static STATUS_HANDLE: AtomicIsize = AtomicIsize::new(0);
static STOP: Mutex<Option<mpsc::Sender<()>>> = Mutex::new(None);

fn set_status(state: u32, wait_hint_ms: u32) {
    static CHECKPOINT: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
    let accepted = if state == win::SERVICE_RUNNING { win::SERVICE_ACCEPT_STOP | win::SERVICE_ACCEPT_SHUTDOWN } else { 0 };
    let pending = state == win::SERVICE_START_PENDING || state == win::SERVICE_STOP_PENDING;
    let status = ServiceStatus {
        service_type: win::SERVICE_WIN32_OWN_PROCESS,
        current_state: state,
        controls_accepted: accepted,
        check_point: if pending { CHECKPOINT.fetch_add(1, Ordering::Relaxed) + 1 } else { 0 },
        wait_hint: wait_hint_ms,
        ..Default::default()
    };
    unsafe { win::SetServiceStatus(STATUS_HANDLE.load(Ordering::SeqCst), &status) };
}

unsafe extern "system" fn handler(control: u32, _: u32, _: *mut std::ffi::c_void, _: *mut std::ffi::c_void) -> u32 {
    match control {
        win::SERVICE_CONTROL_STOP | win::SERVICE_CONTROL_SHUTDOWN => {
            if let Some(tx) = STOP.lock().unwrap().as_ref() { let _ = tx.send(()); }
            0
        }
        win::SERVICE_CONTROL_INTERROGATE => 0,
        _ => 120, // ERROR_CALL_NOT_IMPLEMENTED
    }
}

unsafe extern "system" fn service_main(_argc: u32, _argv: *mut *mut u16) {
    let name = win::wide(NAME);
    let handle = unsafe { win::RegisterServiceCtrlHandlerExW(name.as_ptr(), handler, null_mut()) };
    if handle == 0 { return; }
    STATUS_HANDLE.store(handle, Ordering::SeqCst);
    set_status(win::SERVICE_START_PENDING, 10_000);
    let (tx, rx) = mpsc::channel();
    *STOP.lock().unwrap() = Some(tx);
    let engine = run_engine(false);
    set_status(win::SERVICE_RUNNING, 0);
    let _ = rx.recv();
    set_status(win::SERVICE_STOP_PENDING, 30_000);
    log!("stop requested");
    engine.shutdown();
    set_status(win::SERVICE_STOPPED, 0);
}

/// Hands the process to the service control manager; returns when the service has stopped.
pub fn dispatch() -> Result<(), String> {
    let mut name = win::wide(NAME);
    let table = [
        win::ServiceTableEntry { name: name.as_mut_ptr(), proc_: Some(service_main) },
        win::ServiceTableEntry { name: null_mut(), proc_: None },
    ];
    if unsafe { win::StartServiceCtrlDispatcherW(table.as_ptr()) } == 0 {
        return Err(format!("not started by the service manager (error {}); use --console to run it in a terminal", win::last_error()));
    }
    Ok(())
}

// ---- install / uninstall --------------------------------------------------------------------------------------------

struct ScHandle(win::Handle);

impl Drop for ScHandle {
    fn drop(&mut self) {
        if self.0 != 0 { unsafe { win::CloseServiceHandle(self.0) }; }
    }
}

fn open_manager() -> Result<ScHandle, String> {
    let manager = unsafe { win::OpenSCManagerW(null(), null(), win::SC_MANAGER_ALL_ACCESS) };
    if manager == 0 {
        return Err(match win::last_error() {
            win::ERROR_ACCESS_DENIED => "access denied: run this from an elevated (admin) terminal".into(),
            e => format!("can't open the service manager: error {e}"),
        });
    }
    Ok(ScHandle(manager))
}

fn open_service(manager: &ScHandle) -> Option<ScHandle> {
    let name = win::wide(NAME);
    let service = unsafe { win::OpenServiceW(manager.0, name.as_ptr(), win::SERVICE_ALL_ACCESS) };
    (service != 0).then_some(ScHandle(service))
}

fn stop_and_wait(service: &ScHandle) -> Result<(), String> {
    let mut status = ServiceStatus::default();
    if unsafe { win::ControlService(service.0, win::SERVICE_CONTROL_STOP, &mut status) } == 0 {
        match win::last_error() {
            win::ERROR_SERVICE_NOT_ACTIVE => return Ok(()),
            // Already stopping (or starting): wait for it like any stop, and end it if it hangs.
            win::ERROR_SERVICE_CANNOT_ACCEPT_CTRL => say!("the service is already stopping or starting; waiting for it..."),
            e => return Err(format!("can't stop the service: error {e}")),
        }
    } else {
        say!("stopping the running service (it saves its snapshots first)...");
    }
    if wait_stopped(service, Duration::from_secs(20)) { return Ok(()); }

    // An older build could hang on stop (a journal read on a quiet drive). Ending it is safe: the next start loads the
    // last saved snapshot and catches up from the journal.
    let pid = service_pid(service);
    say!("it didn't stop within 20 s; ending the process (pid {pid})");
    if pid != 0 {
        unsafe {
            let process = win::OpenProcess(1 /* PROCESS_TERMINATE */, 0, pid);
            if process != 0 {
                win::TerminateProcess(process, 1);
                win::CloseHandle(process);
            }
        }
    }
    if wait_stopped(service, Duration::from_secs(15)) { return Ok(()); }
    Err("the service didn't stop, even after ending its process".into())
}

fn wait_stopped(service: &ScHandle, limit: Duration) -> bool {
    let mut status = ServiceStatus::default();
    let deadline = Instant::now() + limit;
    while Instant::now() < deadline {
        unsafe { win::QueryServiceStatus(service.0, &mut status) };
        if status.current_state == win::SERVICE_STOPPED { return true; }
        std::thread::sleep(Duration::from_millis(300));
    }
    false
}

/// SERVICE_STATUS_PROCESS.dwProcessId (SC_STATUS_PROCESS_INFO).
fn service_pid(service: &ScHandle) -> u32 {
    let mut buffer = [0u8; 64];
    let mut needed = 0u32;
    let ok = unsafe { win::QueryServiceStatusEx(service.0, 0, buffer.as_mut_ptr(), buffer.len() as u32, &mut needed) };
    if ok == 0 { 0 } else { u32::from_le_bytes(buffer[28..32].try_into().unwrap()) }
}

/// Copies this exe into Program Files and registers (or updates) and starts the service.
pub fn install() -> Result<(), String> {
    let source = std::env::current_exe().map_err(|e| e.to_string())?;
    let target = install_folder().join("GlazeIndex.exe");
    if same_file(&source, &target) {
        return Err(format!("this is the installed copy, so it can't update itself. Run --install from the new build instead, \
            e.g. C:\\codex\\glazewm\\indexer\\target\\release\\GlazeIndex.exe --install. (To just restart the service: \
            Restart-Service {NAME})"));
    }
    let manager = open_manager()?;
    say!("installing the build of {} from {}", win::build_time(), source.display());
    if target.exists() { say!("replacing the installed build of {}", win::file_time(&target)); }
    let existing = open_service(&manager);
    if let Some(service) = &existing { stop_and_wait(service)?; }

    let folder = install_folder();
    std::fs::create_dir_all(&folder).map_err(|e| format!("can't create {}: {e}", folder.display()))?;
    let mut copied = Err(String::new());
    for _ in 0..20 {
        copied = std::fs::copy(&source, &target).map(|_| ()).map_err(|e| e.to_string());
        if copied.is_ok() { break; }
        std::thread::sleep(Duration::from_millis(500)); // the old process may still be exiting
    }
    copied.map_err(|e| format!("can't copy to {}: {e}", target.display()))?;

    // A data folder someone else created could hold planted links; start it fresh with a protected ACL.
    let base = base_folder();
    if base.exists() && existing.is_none() { let _ = std::fs::remove_dir_all(&base); }
    ensure_folders()?;

    let service = match existing {
        Some(s) => s,
        None => {
            let name = win::wide(NAME);
            let display = win::wide(DISPLAY);
            let path = win::wide(&format!("\"{}\" --service", target.display()));
            let service = unsafe {
                win::CreateServiceW(manager.0, name.as_ptr(), display.as_ptr(), win::SERVICE_ALL_ACCESS, win::SERVICE_WIN32_OWN_PROCESS,
                    win::SERVICE_AUTO_START, win::SERVICE_ERROR_NORMAL, path.as_ptr(), null(), null_mut(), null(), null(), null())
            };
            if service == 0 { return Err(format!("can't create the service: error {}", win::last_error())); }
            ScHandle(service)
        }
    };
    let mut description = win::wide(DESCRIPTION);
    let info: *mut u16 = description.as_mut_ptr();
    let delayed: i32 = 1;
    unsafe {
        win::ChangeServiceConfig2W(service.0, win::SERVICE_CONFIG_DESCRIPTION, &info as *const *mut u16 as *const _);
        win::ChangeServiceConfig2W(service.0, win::SERVICE_CONFIG_DELAYED_AUTO_START_INFO, &delayed as *const i32 as *const _);
        if win::StartServiceW(service.0, 0, null()) == 0 && win::last_error() != win::ERROR_SERVICE_ALREADY_RUNNING {
            return Err(format!("installed, but it didn't start: error {}", win::last_error()));
        }
    }
    say!("GlazeIndex is installed in {} and running (it starts with Windows).", folder.display());
    say!("Log: {}", base.join("index.log").display());
    Ok(())
}

pub fn uninstall() -> Result<(), String> {
    let manager = open_manager()?;
    if let Some(service) = open_service(&manager) {
        stop_and_wait(&service)?;
        if unsafe { win::DeleteService(service.0) } == 0 { return Err(format!("can't delete the service: error {}", win::last_error())); }
        say!("service removed");
    } else {
        say!("the service isn't installed");
    }
    std::thread::sleep(Duration::from_millis(500));
    let folder = install_folder();
    if folder.exists() && std::fs::remove_dir_all(&folder).is_err() {
        say!("couldn't remove {} (in use?); delete it by hand", folder.display());
    }
    // Snapshots list every file name on the drives; don't leave them behind.
    let _ = std::fs::remove_dir_all(base_folder());
    say!("removed {} and {}. Change journals it created stay (harmless); remove one with: fsutil usn deletejournal /d X:",
        folder.display(), base_folder().display());
    Ok(())
}

fn same_file(a: &std::path::Path, b: &std::path::Path) -> bool {
    match (a.canonicalize(), b.canonicalize()) {
        (Ok(a), Ok(b)) => a == b,
        _ => false,
    }
}
