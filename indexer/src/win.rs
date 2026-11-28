//! The Win32 calls the index service uses, declared by hand (no dependencies).
#![allow(non_snake_case, dead_code, clippy::upper_case_acronyms)]

use std::ffi::c_void;

pub type Handle = isize;
pub const INVALID_HANDLE: Handle = -1;

pub const GENERIC_READ: u32 = 0x8000_0000;
pub const GENERIC_WRITE: u32 = 0x4000_0000;
pub const FILE_SHARE_ALL: u32 = 7;
pub const OPEN_EXISTING: u32 = 3;
pub const FILE_READ_ATTRIBUTES: u32 = 0x80;
pub const FILE_FLAG_BACKUP_SEMANTICS: u32 = 0x0200_0000;

pub const FSCTL_GET_NTFS_VOLUME_DATA: u32 = 0x0009_0064;
pub const FSCTL_QUERY_USN_JOURNAL: u32 = 0x0009_00F4;
pub const FSCTL_CREATE_USN_JOURNAL: u32 = 0x0009_00E7;
pub const FSCTL_READ_USN_JOURNAL: u32 = 0x0009_00BB;
pub const IOCTL_STORAGE_QUERY_PROPERTY: u32 = 0x002D_1400;

pub const ERROR_ACCESS_DENIED: u32 = 5;
pub const ERROR_PIPE_CONNECTED: u32 = 535;
pub const ERROR_SERVICE_DOES_NOT_EXIST: u32 = 1060;
pub const ERROR_SERVICE_NOT_ACTIVE: u32 = 1062;
pub const ERROR_SERVICE_CANNOT_ACCEPT_CTRL: u32 = 1061;
pub const ERROR_SERVICE_ALREADY_RUNNING: u32 = 1056;
pub const ERROR_JOURNAL_DELETE_IN_PROGRESS: u32 = 1178;
pub const ERROR_JOURNAL_NOT_ACTIVE: u32 = 1179;
pub const ERROR_JOURNAL_ENTRY_DELETED: u32 = 1181;

pub const DRIVE_REMOVABLE: u32 = 2;
pub const DRIVE_FIXED: u32 = 3;
pub const BUS_TYPE_USB: u32 = 7;

pub const PIPE_ACCESS_DUPLEX: u32 = 3;
pub const PIPE_REJECT_REMOTE_CLIENTS: u32 = 8;
pub const PIPE_UNLIMITED_INSTANCES: u32 = 255;

pub const THREAD_MODE_BACKGROUND_BEGIN: i32 = 0x0001_0000;

pub const SERVICE_WIN32_OWN_PROCESS: u32 = 0x10;
pub const SERVICE_AUTO_START: u32 = 2;
pub const SERVICE_ERROR_NORMAL: u32 = 1;
pub const SC_MANAGER_ALL_ACCESS: u32 = 0xF003F;
pub const SERVICE_ALL_ACCESS: u32 = 0xF01FF;
pub const SERVICE_CONTROL_STOP: u32 = 1;
pub const SERVICE_CONTROL_INTERROGATE: u32 = 4;
pub const SERVICE_CONTROL_SHUTDOWN: u32 = 5;
pub const SERVICE_ACCEPT_STOP: u32 = 1;
pub const SERVICE_ACCEPT_SHUTDOWN: u32 = 4;
pub const SERVICE_STOPPED: u32 = 1;
pub const SERVICE_START_PENDING: u32 = 2;
pub const SERVICE_STOP_PENDING: u32 = 3;
pub const SERVICE_RUNNING: u32 = 4;
pub const SERVICE_CONFIG_DESCRIPTION: u32 = 1;
pub const SERVICE_CONFIG_DELAYED_AUTO_START_INFO: u32 = 3;

#[repr(C)]
#[derive(Default, Clone, Copy)]
pub struct ServiceStatus {
    pub service_type: u32,
    pub current_state: u32,
    pub controls_accepted: u32,
    pub win32_exit_code: u32,
    pub service_specific_exit_code: u32,
    pub check_point: u32,
    pub wait_hint: u32,
}

#[repr(C)]
pub struct ServiceTableEntry {
    pub name: *mut u16,
    pub proc_: Option<unsafe extern "system" fn(u32, *mut *mut u16)>,
}

pub type HandlerEx = unsafe extern "system" fn(u32, u32, *mut c_void, *mut c_void) -> u32;

#[repr(C)]
pub struct SecurityAttributes {
    pub length: u32,
    pub descriptor: *mut c_void,
    pub inherit: i32,
}

#[repr(C)]
#[derive(Default)]
pub struct SystemTime {
    pub year: u16,
    pub month: u16,
    pub day_of_week: u16,
    pub day: u16,
    pub hour: u16,
    pub minute: u16,
    pub second: u16,
    pub milliseconds: u16,
}

#[repr(C)]
pub struct Luid {
    pub low: u32,
    pub high: i32,
}

#[repr(C)]
pub struct TokenPrivileges {
    pub count: u32,
    pub luid: Luid,
    pub attributes: u32,
}

/// FILE_ID_DESCRIPTOR with a 64-bit file ID (Type 0); the union is 16 bytes.
#[repr(C)]
pub struct FileIdDescriptor {
    pub size: u32,
    pub kind: u32,
    pub id: u64,
    pub pad: u64,
}

#[repr(C)]
#[derive(Default)]
pub struct MemoryCounters {
    pub cb: u32,
    pub page_fault_count: u32,
    pub peak_working_set: usize,
    pub working_set: usize,
    pub quota_peak_paged_pool: usize,
    pub quota_paged_pool: usize,
    pub quota_peak_non_paged_pool: usize,
    pub quota_non_paged_pool: usize,
    pub private_bytes: usize,
    pub peak_private_bytes: usize,
}

#[link(name = "kernel32")]
unsafe extern "system" {
    pub fn CreateFileW(name: *const u16, access: u32, share: u32, security: *mut c_void, disposition: u32, flags: u32, template: Handle) -> Handle;
    pub fn DeviceIoControl(handle: Handle, code: u32, input: *const c_void, input_size: u32, output: *mut c_void, output_size: u32, returned: *mut u32, overlapped: *mut c_void) -> i32;
    pub fn ReadFile(handle: Handle, buffer: *mut u8, size: u32, read: *mut u32, overlapped: *mut c_void) -> i32;
    pub fn WriteFile(handle: Handle, buffer: *const u8, size: u32, written: *mut u32, overlapped: *mut c_void) -> i32;
    pub fn SetFilePointerEx(handle: Handle, distance: i64, new_position: *mut i64, method: u32) -> i32;
    pub fn CloseHandle(handle: Handle) -> i32;
    pub fn GetLastError() -> u32;
    pub fn GetLogicalDrives() -> u32;
    pub fn GetDriveTypeW(root: *const u16) -> u32;
    pub fn GetVolumeInformationW(root: *const u16, name: *mut u16, name_size: u32, serial: *mut u32, max_component: *mut u32, flags: *mut u32, fs_name: *mut u16, fs_name_size: u32) -> i32;
    pub fn OpenFileById(volume: Handle, id: *const FileIdDescriptor, access: u32, share: u32, security: *mut c_void, flags: u32) -> Handle;
    pub fn GetFileInformationByHandleEx(handle: Handle, class: u32, info: *mut c_void, size: u32) -> i32;
    pub fn CreateNamedPipeW(name: *const u16, open_mode: u32, pipe_mode: u32, max_instances: u32, out_size: u32, in_size: u32, timeout: u32, security: *const SecurityAttributes) -> Handle;
    pub fn ConnectNamedPipe(handle: Handle, overlapped: *mut c_void) -> i32;
    pub fn DisconnectNamedPipe(handle: Handle) -> i32;
    pub fn FlushFileBuffers(handle: Handle) -> i32;
    pub fn GetLocalTime(time: *mut SystemTime);
    pub fn GetCurrentThread() -> Handle;
    pub fn CancelSynchronousIo(thread: Handle) -> i32;
    pub fn OpenProcess(access: u32, inherit: i32, pid: u32) -> Handle;
    pub fn GetDiskFreeSpaceExW(root: *const u16, available: *mut u64, total: *mut u64, free: *mut u64) -> i32;
    pub fn GetNamedPipeClientProcessId(pipe: Handle, pid: *mut u32) -> i32;
    pub fn QueryFullProcessImageNameW(process: Handle, flags: u32, name: *mut u16, size: *mut u32) -> i32;
    pub fn FileTimeToLocalFileTime(utc: *const u64, local: *mut u64) -> i32;
    pub fn FileTimeToSystemTime(filetime: *const u64, time: *mut SystemTime) -> i32;
    pub fn TerminateProcess(process: Handle, exit_code: u32) -> i32;
    pub fn SetThreadPriority(thread: Handle, priority: i32) -> i32;
    pub fn GetCurrentProcess() -> Handle;
    pub fn LocalFree(memory: *mut c_void) -> *mut c_void;
    pub fn CreateDirectoryW(path: *const u16, security: *const SecurityAttributes) -> i32;
    pub fn K32GetProcessMemoryInfo(process: Handle, counters: *mut MemoryCounters, size: u32) -> i32;
}

#[link(name = "advapi32")]
unsafe extern "system" {
    pub fn StartServiceCtrlDispatcherW(table: *const ServiceTableEntry) -> i32;
    pub fn RegisterServiceCtrlHandlerExW(name: *const u16, handler: HandlerEx, context: *mut c_void) -> Handle;
    pub fn SetServiceStatus(handle: Handle, status: *const ServiceStatus) -> i32;
    pub fn OpenSCManagerW(machine: *const u16, database: *const u16, access: u32) -> Handle;
    pub fn CreateServiceW(manager: Handle, name: *const u16, display: *const u16, access: u32, kind: u32, start: u32, error: u32, path: *const u16, group: *const u16, tag: *mut u32, dependencies: *const u16, account: *const u16, password: *const u16) -> Handle;
    pub fn OpenServiceW(manager: Handle, name: *const u16, access: u32) -> Handle;
    pub fn StartServiceW(service: Handle, argc: u32, argv: *const *const u16) -> i32;
    pub fn ControlService(service: Handle, control: u32, status: *mut ServiceStatus) -> i32;
    pub fn QueryServiceStatus(service: Handle, status: *mut ServiceStatus) -> i32;
    pub fn QueryServiceStatusEx(service: Handle, level: u32, buffer: *mut u8, size: u32, needed: *mut u32) -> i32;
    pub fn DeleteService(service: Handle) -> i32;
    pub fn CloseServiceHandle(handle: Handle) -> i32;
    pub fn ChangeServiceConfig2W(service: Handle, level: u32, info: *const c_void) -> i32;
    pub fn ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl: *const u16, revision: u32, descriptor: *mut *mut c_void, size: *mut u32) -> i32;
    pub fn OpenProcessToken(process: Handle, access: u32, token: *mut Handle) -> i32;
    pub fn LookupPrivilegeValueW(system: *const u16, name: *const u16, luid: *mut Luid) -> i32;
    pub fn GetTokenInformation(token: Handle, class: u32, info: *mut c_void, length: u32, returned: *mut u32) -> i32;
    pub fn LookupAccountSidW(system: *const u16, sid: *mut c_void, name: *mut u16, name_size: *mut u32, domain: *mut u16, domain_size: *mut u32, kind: *mut u32) -> i32;
    pub fn AdjustTokenPrivileges(token: Handle, disable_all: i32, new: *const TokenPrivileges, length: u32, previous: *mut c_void, returned: *mut u32) -> i32;
}

pub fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain([0]).collect()
}

pub fn last_error() -> u32 {
    unsafe { GetLastError() }
}

/// Owns a kernel handle and closes it.
pub struct OwnedHandle(pub Handle);

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        if self.0 != INVALID_HANDLE && self.0 != 0 {
            unsafe { CloseHandle(self.0) };
        }
    }
}

/// A security descriptor from SDDL, freed with LocalFree.
pub struct Sddl(pub *mut c_void);

impl Sddl {
    pub fn new(sddl: &str) -> Option<Sddl> {
        let mut descriptor = std::ptr::null_mut();
        let text = wide(sddl);
        let ok = unsafe { ConvertStringSecurityDescriptorToSecurityDescriptorW(text.as_ptr(), 1, &mut descriptor, std::ptr::null_mut()) };
        (ok != 0).then_some(Sddl(descriptor))
    }

    pub fn attributes(&self) -> SecurityAttributes {
        SecurityAttributes { length: size_of::<SecurityAttributes>() as u32, descriptor: self.0, inherit: 0 }
    }
}

impl Drop for Sddl {
    fn drop(&mut self) {
        unsafe { LocalFree(self.0) };
    }
}

unsafe impl Send for Sddl {}
unsafe impl Sync for Sddl {}

/// "DOMAIN\user via program.exe (pid N)" for the process on the other end of a pipe, for the settings log.
pub fn pipe_client(pipe: Handle) -> String {
    let mut pid = 0u32;
    if unsafe { GetNamedPipeClientProcessId(pipe, &mut pid) } == 0 { return "unknown client".into(); }
    let process = unsafe { OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, 0, pid) };
    if process == 0 { return format!("pid {pid}"); }
    let process = OwnedHandle(process);
    let mut exe = [0u16; 512];
    let mut exe_len = exe.len() as u32;
    let program = if unsafe { QueryFullProcessImageNameW(process.0, 0, exe.as_mut_ptr(), &mut exe_len) } != 0 {
        let full = String::from_utf16_lossy(&exe[..exe_len as usize]);
        full.rsplit('\\').next().unwrap_or(&full).to_string()
    } else { "?".into() };
    let mut user = String::from("?");
    unsafe {
        let mut token = 0;
        if OpenProcessToken(process.0, 0x8 /* TOKEN_QUERY */, &mut token) != 0 {
            let token = OwnedHandle(token);
            let mut buffer = [0u8; 256];
            let mut returned = 0u32;
            if GetTokenInformation(token.0, 1 /* TokenUser */, buffer.as_mut_ptr() as *mut c_void, buffer.len() as u32, &mut returned) != 0 {
                let sid = *(buffer.as_ptr() as *const *mut c_void);
                let (mut name, mut domain) = ([0u16; 128], [0u16; 128]);
                let (mut name_len, mut domain_len, mut kind) = (128u32, 128u32, 0u32);
                if LookupAccountSidW(std::ptr::null(), sid, name.as_mut_ptr(), &mut name_len, domain.as_mut_ptr(), &mut domain_len, &mut kind) != 0 {
                    user = format!("{}\\{}", String::from_utf16_lossy(&domain[..domain_len as usize]), String::from_utf16_lossy(&name[..name_len as usize]));
                }
            }
        }
    }
    format!("{user} via {program} (pid {pid})")
}

/// The local time of a file's last write, "2026-11-28 11:31", to tell builds apart.
pub fn file_time(path: &std::path::Path) -> String {
    use std::os::windows::fs::MetadataExt;
    let Ok(meta) = std::fs::metadata(path) else { return "unknown".into() };
    let utc = meta.last_write_time();
    let (mut local, mut t) = (0u64, SystemTime::default());
    unsafe {
        if FileTimeToLocalFileTime(&utc, &mut local) == 0 || FileTimeToSystemTime(&local, &mut t) == 0 { return "unknown".into(); }
    }
    format!("{:04}-{:02}-{:02} {:02}:{:02}", t.year, t.month, t.day, t.hour, t.minute)
}

/// The running exe's build time (its file time).
pub fn build_time() -> String {
    std::env::current_exe().map(|p| file_time(&p)).unwrap_or_else(|_| "unknown".into())
}

pub fn private_memory() -> usize {
    let mut counters = MemoryCounters { cb: size_of::<MemoryCounters>() as u32, ..Default::default() };
    unsafe { K32GetProcessMemoryInfo(GetCurrentProcess(), &mut counters, counters.cb) };
    counters.private_bytes
}

/// Lets the service open any file by ID for its size, whatever the file's ACL says (LocalSystem holds the privilege).
pub fn enable_backup_privilege() -> bool {
    unsafe {
        let mut token = 0;
        if OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, &mut token) == 0 { return false; }
        let token = OwnedHandle(token);
        let mut luid = Luid { low: 0, high: 0 };
        let name = wide("SeBackupPrivilege");
        if LookupPrivilegeValueW(std::ptr::null(), name.as_ptr(), &mut luid) == 0 { return false; }
        let privileges = TokenPrivileges { count: 1, luid, attributes: 2 };
        AdjustTokenPrivileges(token.0, 0, &privileges, 0, std::ptr::null_mut(), std::ptr::null_mut()) != 0 && last_error() == 0
    }
}
