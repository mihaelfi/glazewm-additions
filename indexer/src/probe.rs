//! index-probe: measures what an Everything-style index of this PC's NTFS volumes costs.
//!
//! For each volume it reads the master file table (MFT) directly, the way Everything and WizTree do, builds a compact
//! in-memory index (names, parents, sizes, modified times, folder totals), and reports time and memory. It also times the
//! simpler FSCTL_ENUM_USN_DATA enumeration (names only), saves and reloads a snapshot, times name searches, reports how
//! far back each USN change journal reaches, and with --watch follows the journals live.
//!
//! Reading a volume needs admin: run it from an elevated terminal. It only reads.
//!
//!   index-probe [C D E] [--query text]... [--watch seconds] [--out report.txt]

use std::ffi::c_void;
use std::ptr::null_mut;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::mpsc;
use std::time::{Duration, Instant};

// ---- Win32 ------------------------------------------------------------------------------------------------------

mod win {
    use std::ffi::c_void;

    pub type Handle = isize;
    pub const INVALID: Handle = -1;
    pub const GENERIC_READ: u32 = 0x8000_0000;
    pub const SHARE_READ_WRITE: u32 = 3;
    pub const OPEN_EXISTING: u32 = 3;
    pub const FSCTL_GET_NTFS_VOLUME_DATA: u32 = 0x0009_0064;
    pub const FSCTL_QUERY_USN_JOURNAL: u32 = 0x0009_00F4;
    pub const FSCTL_ENUM_USN_DATA: u32 = 0x0009_00B3;
    pub const FSCTL_READ_USN_JOURNAL: u32 = 0x0009_00BB;
    pub const ERROR_ACCESS_DENIED: u32 = 5;
    pub const ERROR_HANDLE_EOF: u32 = 38;
    pub const ERROR_JOURNAL_NOT_ACTIVE: u32 = 1179;

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
        pub fn SetFilePointerEx(handle: Handle, distance: i64, new_position: *mut i64, method: u32) -> i32;
        pub fn CloseHandle(handle: Handle) -> i32;
        pub fn GetLastError() -> u32;
        pub fn GetCurrentProcess() -> Handle;
        pub fn K32GetProcessMemoryInfo(process: Handle, counters: *mut MemoryCounters, size: u32) -> i32;
    }
}

struct Volume(win::Handle);

impl Drop for Volume {
    fn drop(&mut self) {
        unsafe { win::CloseHandle(self.0) };
    }
}

impl Volume {
    fn open(letter: char) -> Result<Volume, String> {
        let path: Vec<u16> = format!("\\\\.\\{letter}:").encode_utf16().chain([0]).collect();
        let handle = unsafe { win::CreateFileW(path.as_ptr(), win::GENERIC_READ, win::SHARE_READ_WRITE, null_mut(), win::OPEN_EXISTING, 0, 0) };
        if handle != win::INVALID {
            return Ok(Volume(handle));
        }
        Err(match unsafe { win::GetLastError() } {
            win::ERROR_ACCESS_DENIED => "access denied: run index-probe from an elevated (admin) terminal".into(),
            e => format!("can't open the volume: error {e}"),
        })
    }

    fn ioctl(&self, code: u32, input: &[u8], output: &mut [u8]) -> Result<usize, u32> {
        let mut returned = 0u32;
        let ok = unsafe {
            win::DeviceIoControl(self.0, code, input.as_ptr() as *const c_void, input.len() as u32,
                output.as_mut_ptr() as *mut c_void, output.len() as u32, &mut returned, null_mut())
        };
        if ok == 0 { Err(unsafe { win::GetLastError() }) } else { Ok(returned as usize) }
    }

    fn read_at(&self, offset: u64, buffer: &mut [u8]) -> Result<(), String> {
        let mut read = 0u32;
        let ok = unsafe {
            win::SetFilePointerEx(self.0, offset as i64, null_mut(), 0) != 0
                && win::ReadFile(self.0, buffer.as_mut_ptr(), buffer.len() as u32, &mut read, null_mut()) != 0
        };
        if !ok {
            return Err(format!("read at {offset} failed: error {}", unsafe { win::GetLastError() }));
        }
        if read as usize != buffer.len() {
            return Err(format!("short read at {offset}: {read} of {} bytes", buffer.len()));
        }
        Ok(())
    }
}

fn memory() -> (usize, usize) {
    let mut counters = win::MemoryCounters { cb: size_of::<win::MemoryCounters>() as u32, ..Default::default() };
    unsafe { win::K32GetProcessMemoryInfo(win::GetCurrentProcess(), &mut counters, counters.cb) };
    (counters.private_bytes, counters.peak_private_bytes)
}

fn u16_at(b: &[u8], o: usize) -> u16 { u16::from_le_bytes([b[o], b[o + 1]]) }
fn u32_at(b: &[u8], o: usize) -> u32 { u32::from_le_bytes(b[o..o + 4].try_into().unwrap()) }
fn u64_at(b: &[u8], o: usize) -> u64 { u64::from_le_bytes(b[o..o + 8].try_into().unwrap()) }
fn i64_at(b: &[u8], o: usize) -> i64 { u64_at(b, o) as i64 }

const RECORD_MASK: u64 = 0x0000_FFFF_FFFF_FFFF;

// ---- report -------------------------------------------------------------------------------------------------------

struct Report {
    text: String,
    path: String,
}

macro_rules! say {
    ($r:expr) => {{ println!(); $r.text.push('\n'); }};
    ($r:expr, $($arg:tt)*) => {{
        let line = format!($($arg)*);
        println!("{line}");
        $r.text.push_str(&line);
        $r.text.push('\n');
    }};
}

impl Report {
    fn save(&self) {
        if let Err(e) = std::fs::write(&self.path, &self.text) {
            eprintln!("can't write {}: {e}", self.path);
        }
    }
}

fn mb(bytes: usize) -> String { format!("{:.1} MB", bytes as f64 / 1_048_576.0) }
fn gb(bytes: u64) -> String { format!("{:.1} GB", bytes as f64 / 1_073_741_824.0) }
fn ms(d: Duration) -> String { format!("{:.0} ms", d.as_secs_f64() * 1000.0) }

fn filetime_utc(filetime: i64) -> String {
    let secs = filetime / 10_000_000 - 11_644_473_600;
    let (days, rem) = (secs.div_euclid(86_400), secs.rem_euclid(86_400));
    // Days since 1970-01-01 to a civil date (Howard Hinnant's algorithm).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + if m <= 2 { 1 } else { 0 };
    format!("{y:04}-{m:02}-{d:02} {:02}:{:02} UTC", rem / 3600, rem % 3600 / 60)
}

// ---- volume facts ---------------------------------------------------------------------------------------------------

struct NtfsInfo {
    bytes_per_cluster: u64,
    record_size: usize,
    mft_valid_length: u64,
    mft_start_lcn: u64,
    total_clusters: u64,
    free_clusters: u64,
}

fn ntfs_info(volume: &Volume) -> Result<NtfsInfo, String> {
    let mut buffer = [0u8; 256];
    volume.ioctl(win::FSCTL_GET_NTFS_VOLUME_DATA, &[], &mut buffer)
        .map_err(|e| format!("not NTFS or no access (FSCTL_GET_NTFS_VOLUME_DATA error {e})"))?;
    Ok(NtfsInfo {
        total_clusters: u64_at(&buffer, 16),
        free_clusters: u64_at(&buffer, 24),
        bytes_per_cluster: u32_at(&buffer, 44) as u64,
        record_size: u32_at(&buffer, 48) as usize,
        mft_valid_length: u64_at(&buffer, 56),
        mft_start_lcn: u64_at(&buffer, 64),
    })
}

struct Journal {
    id: u64,
    first_usn: i64,
    next_usn: i64,
    maximum_size: u64,
}

fn query_journal(volume: &Volume) -> Result<Journal, u32> {
    let mut buffer = [0u8; 80];
    volume.ioctl(win::FSCTL_QUERY_USN_JOURNAL, &[], &mut buffer)?;
    Ok(Journal { id: u64_at(&buffer, 0), first_usn: i64_at(&buffer, 8), next_usn: i64_at(&buffer, 16), maximum_size: u64_at(&buffer, 40) })
}

/// READ_USN_JOURNAL_DATA_V0: StartUsn, ReasonMask, ReturnOnlyOnClose, Timeout, BytesToWaitFor, UsnJournalID.
fn read_journal_input(start: i64, journal_id: u64, wait_bytes: u64, timeout: u64) -> [u8; 40] {
    let mut input = [0u8; 40];
    input[0..8].copy_from_slice(&start.to_le_bytes());
    input[8..12].copy_from_slice(&u32::MAX.to_le_bytes());
    input[16..24].copy_from_slice(&timeout.to_le_bytes());
    input[24..32].copy_from_slice(&wait_bytes.to_le_bytes());
    input[32..40].copy_from_slice(&journal_id.to_le_bytes());
    input
}

/// The timestamp of the oldest record still in the journal: how long an index can be offline and still catch up.
fn journal_oldest(volume: &Volume, journal: &Journal) -> Option<i64> {
    let input = read_journal_input(journal.first_usn, journal.id, 0, 0);
    let mut buffer = vec![0u8; 64 * 1024];
    let n = volume.ioctl(win::FSCTL_READ_USN_JOURNAL, &input, &mut buffer).ok()?;
    if n < 8 + 60 { return None; }
    Some(i64_at(&buffer, 8 + 32))
}

/// Enumerates every file and folder through FSCTL_ENUM_USN_DATA (names and parents, no sizes): the simpler route.
fn enum_usn_data(volume: &Volume, high_usn: i64) -> Result<(usize, usize, Duration), String> {
    let started = Instant::now();
    let mut input = [0u8; 24];
    input[16..24].copy_from_slice(&high_usn.to_le_bytes());
    let mut buffer = vec![0u8; 1 << 20];
    let (mut entries, mut folders) = (0usize, 0usize);
    loop {
        let n = match volume.ioctl(win::FSCTL_ENUM_USN_DATA, &input, &mut buffer) {
            Ok(n) => n,
            Err(win::ERROR_HANDLE_EOF) => break,
            Err(e) => return Err(format!("FSCTL_ENUM_USN_DATA error {e}")),
        };
        if n <= 8 { break; }
        input[0..8].copy_from_slice(&buffer[0..8]);
        let mut offset = 8;
        while offset + 60 <= n {
            let length = u32_at(&buffer, offset) as usize;
            if length == 0 { break; }
            entries += 1;
            if u32_at(&buffer, offset + 52) & 0x10 != 0 { folders += 1; }
            offset += length;
        }
    }
    Ok((entries, folders, started.elapsed()))
}

// ---- the index ------------------------------------------------------------------------------------------------------

const DIR: u8 = 1;
const HIDDEN: u8 = 2;
const SYSTEM: u8 = 4;

/// One volume's index. Entry i's name is names[name_start[i]..name_start[i + 1]]. Parents are entry numbers; the root
/// is its own parent. `size` is the file size, or for a folder the total of everything under it.
struct Index {
    letter: char,
    names: Vec<u8>,
    name_start: Vec<u32>,
    parent: Vec<u32>,
    size: Vec<u64>,
    modified: Vec<u64>,
    flags: Vec<u8>,
    /// MFT record number -> entry number (u32::MAX for none). Needed to apply journal changes.
    by_record: Vec<u32>,
    root: u32,
    /// Optional lowercase copy for faster case-insensitive search (measured separately).
    lower: Vec<u8>,
    lower_start: Vec<u32>,
}

impl Index {
    fn len(&self) -> usize { self.parent.len() }

    fn name(&self, i: usize) -> &str {
        let bytes = &self.names[self.name_start[i] as usize..self.name_start[i + 1] as usize];
        unsafe { std::str::from_utf8_unchecked(bytes) }
    }

    fn lower_name(&self, i: usize) -> &str {
        let bytes = &self.lower[self.lower_start[i] as usize..self.lower_start[i + 1] as usize];
        unsafe { std::str::from_utf8_unchecked(bytes) }
    }

    fn path(&self, i: usize) -> String {
        let mut parts = Vec::new();
        let mut at = i as u32;
        for _ in 0..512 {
            if at == self.root { break; }
            parts.push(self.name(at as usize));
            at = self.parent[at as usize];
        }
        parts.reverse();
        format!("{}:\\{}", self.letter, parts.join("\\"))
    }

    fn core_bytes(&self) -> usize {
        self.names.capacity() + 4 * self.name_start.capacity() + 4 * self.parent.capacity() + 8 * self.size.capacity()
            + 8 * self.modified.capacity() + self.flags.capacity() + 4 * self.by_record.capacity()
    }

    fn lower_bytes(&self) -> usize { self.lower.capacity() + 4 * self.lower_start.capacity() }

    fn build_lower(&mut self) {
        let mut lower = Vec::with_capacity(self.names.len());
        let mut starts = Vec::with_capacity(self.name_start.len());
        for i in 0..self.len() {
            starts.push(lower.len() as u32);
            let name = self.name(i);
            if name.is_ascii() { lower.extend(name.bytes().map(|b| b.to_ascii_lowercase())); } else { lower.extend(name.to_lowercase().bytes()); }
        }
        starts.push(lower.len() as u32);
        lower.shrink_to_fit();
        self.lower = lower;
        self.lower_start = starts;
    }
}

struct ScanStats {
    records: usize,
    in_use: usize,
    extension_records: usize,
    attribute_lists: usize,
    extra_hard_links: usize,
    orphans: usize,
    bad_records: usize,
    mft_bytes: u64,
    read_time: Duration,
    total_time: Duration,
}

/// Applies the update sequence array: the last two bytes of each stride were swapped out on disk.
fn fixup(record: &mut [u8]) -> bool {
    if &record[0..4] != b"FILE" { return false; }
    let (offset, count) = (u16_at(record, 4) as usize, u16_at(record, 6) as usize);
    if count < 2 || offset + count * 2 > record.len() { return false; }
    let stride = record.len() / (count - 1);
    let check = [record[offset], record[offset + 1]];
    for i in 1..count {
        let end = i * stride - 2;
        if record[end..end + 2] != check { return false; }
        record[end] = record[offset + i * 2];
        record[end + 1] = record[offset + i * 2 + 1];
    }
    true
}

/// Calls `f(type, attribute bytes)` for each attribute of a fixed-up record.
fn for_each_attribute(record: &[u8], mut f: impl FnMut(u32, &[u8])) {
    let used = (u32_at(record, 0x18) as usize).min(record.len());
    let mut offset = u16_at(record, 0x14) as usize;
    while offset + 16 <= used {
        let kind = u32_at(record, offset);
        if kind == 0xFFFF_FFFF { break; }
        let length = u32_at(record, offset + 4) as usize;
        if length < 16 || offset + length > used { break; }
        f(kind, &record[offset..offset + length]);
        offset += length;
    }
}

fn resident_value(attribute: &[u8]) -> Option<&[u8]> {
    if attribute[8] != 0 { return None; }
    let (length, offset) = (u32_at(attribute, 0x10) as usize, u16_at(attribute, 0x14) as usize);
    attribute.get(offset..offset + length)
}

/// Decodes a non-resident attribute's run list into (starting cluster, cluster count); sparse runs get -1.
fn run_list(attribute: &[u8]) -> Vec<(i64, u64)> {
    let mut runs = Vec::new();
    let mut p = u16_at(attribute, 0x20) as usize;
    let mut lcn = 0i64;
    while p < attribute.len() && attribute[p] != 0 {
        let header = attribute[p];
        let (length_size, offset_size) = ((header & 0x0F) as usize, (header >> 4) as usize);
        p += 1;
        if length_size == 0 || length_size > 8 || offset_size > 8 || p + length_size + offset_size > attribute.len() { break; }
        let mut length = 0u64;
        for i in 0..length_size { length |= (attribute[p + i] as u64) << (8 * i); }
        p += length_size;
        if offset_size == 0 {
            runs.push((-1, length));
            continue;
        }
        let mut delta = 0i64;
        for i in 0..offset_size { delta |= (attribute[p + i] as i64) << (8 * i); }
        let shift = 64 - 8 * offset_size as u32;
        delta = (delta << shift) >> shift;
        p += offset_size;
        lcn += delta;
        runs.push((lcn, length));
    }
    runs
}

/// Reads the whole MFT and builds the volume's index.
fn scan(volume: &Volume, letter: char, info: &NtfsInfo) -> Result<(Index, ScanStats), String> {
    let started = Instant::now();
    let record_size = info.record_size;
    let cluster = info.bytes_per_cluster;
    if record_size == 0 || cluster % record_size as u64 != 0 {
        return Err(format!("unsupported layout: {record_size}-byte records, {cluster}-byte clusters"));
    }

    // Record 0 is the MFT itself; its unnamed $DATA attribute says where the rest of the MFT lives.
    let mut first = vec![0u8; cluster as usize];
    volume.read_at(info.mft_start_lcn * cluster, &mut first)?;
    let first = &mut first[..record_size];
    if !fixup(first) { return Err("MFT record 0 is unreadable".into()); }
    let mut runs = Vec::new();
    for_each_attribute(first, |kind, a| {
        if kind == 0x80 && a[8] != 0 && a[9] == 0 && runs.is_empty() { runs = run_list(a); }
    });
    if runs.is_empty() { return Err("can't find the MFT's data runs".into()); }

    let total = (info.mft_valid_length / record_size as u64) as usize;
    let mut parent = vec![u32::MAX; total];
    let mut name_at = vec![0u32; total];
    let mut name_len = vec![0u16; total];
    let mut name_rank = vec![0u8; total];
    let mut size = vec![0u64; total];
    let mut modified = vec![0u64; total];
    let mut flags = vec![0u8; total];
    let mut in_use = vec![false; total];
    let mut names: Vec<u8> = Vec::with_capacity(total * 16);
    let mut stats = ScanStats { records: total, in_use: 0, extension_records: 0, attribute_lists: 0, extra_hard_links: 0,
        orphans: 0, bad_records: 0, mft_bytes: 0, read_time: Duration::ZERO, total_time: Duration::ZERO };

    const CHUNK: u64 = 8 << 20;
    let mut buffer = vec![0u8; CHUNK as usize];
    let mut record_index = 0usize;
    for (lcn, clusters) in runs {
        let run_bytes = clusters * cluster;
        if lcn < 0 {
            record_index += (run_bytes / record_size as u64) as usize;
            continue;
        }
        let mut done = 0u64;
        while done < run_bytes && record_index < total {
            let want = (run_bytes - done).min(CHUNK).min((total - record_index) as u64 * record_size as u64);
            let want = want.div_ceil(cluster) * cluster;
            let chunk = &mut buffer[..want as usize];
            let read_started = Instant::now();
            volume.read_at(lcn as u64 * cluster + done, chunk)?;
            stats.read_time += read_started.elapsed();
            stats.mft_bytes += want;
            done += want;

            for record in chunk.chunks_exact_mut(record_size) {
                let index = record_index;
                record_index += 1;
                if index >= total { break; }
                if &record[0..4] != b"FILE" { continue; }
                if !fixup(record) { stats.bad_records += 1; continue; }
                let header_flags = u16_at(record, 0x16);
                if header_flags & 1 == 0 { continue; }
                let base = (u64_at(record, 0x20) & RECORD_MASK) as usize;
                let target = if base != 0 { stats.extension_records += 1; base } else { index };
                if target >= total { continue; }
                if base == 0 {
                    in_use[target] = true;
                    stats.in_use += 1;
                    if header_flags & 2 != 0 { flags[target] |= DIR; }
                }
                for_each_attribute(record, |kind, a| match kind {
                    0x10 => if let Some(v) = resident_value(a).filter(|v| v.len() >= 0x24) {
                        modified[target] = u64_at(v, 0x08);
                        let attributes = u32_at(v, 0x20);
                        if attributes & 2 != 0 { flags[target] |= HIDDEN; }
                        if attributes & 4 != 0 { flags[target] |= SYSTEM; }
                    },
                    0x20 => stats.attribute_lists += 1,
                    0x30 => if let Some(v) = resident_value(a).filter(|v| v.len() >= 0x42) {
                        let length = v[0x40] as usize;
                        if v.len() < 0x42 + 2 * length { return; }
                        // Win32 (1) and Win32+DOS (3) names beat POSIX (0), which beats the short DOS name (2).
                        let rank = match v[0x41] { 1 | 3 => 3, 0 => 2, _ => 1 };
                        if rank >= 2 && name_rank[target] >= 2 { stats.extra_hard_links += 1; }
                        if rank <= name_rank[target] { return; }
                        let units: Vec<u16> = (0..length).map(|k| u16_at(v, 0x42 + 2 * k)).collect();
                        let name = String::from_utf16_lossy(&units);
                        name_at[target] = names.len() as u32;
                        name_len[target] = name.len() as u16;
                        names.extend_from_slice(name.as_bytes());
                        name_rank[target] = rank;
                        parent[target] = (u64_at(v, 0) & RECORD_MASK) as u32;
                    },
                    0x80 if a[9] == 0 => {
                        if a[8] == 0 { size[target] = u32_at(a, 0x10) as u64; }
                        else if a.len() >= 0x38 && u64_at(a, 0x10) == 0 { size[target] = u64_at(a, 0x30); }
                    },
                    _ => {}
                });
            }
        }
    }

    // Compact: keep named, in-use records; skip the NTFS metafiles (records 0-15 except the root, and $Extend's contents).
    const ROOT: usize = 5;
    const EXTEND: u32 = 11;
    let mut by_record = vec![u32::MAX; total];
    let mut count = 0u32;
    for r in 0..total {
        if !in_use[r] || name_rank[r] == 0 || (r < 16 && r != ROOT) || parent[r] == EXTEND { continue; }
        by_record[r] = count;
        count += 1;
    }
    let n = count as usize;
    let root = by_record.get(ROOT).copied().filter(|&e| e != u32::MAX).ok_or("the root folder is missing")?;
    let mut index = Index {
        letter,
        names: Vec::with_capacity(names.len()),
        name_start: Vec::with_capacity(n + 1),
        parent: Vec::with_capacity(n),
        size: Vec::with_capacity(n),
        modified: Vec::with_capacity(n),
        flags: Vec::with_capacity(n),
        by_record: Vec::new(),
        root,
        lower: Vec::new(),
        lower_start: Vec::new(),
    };
    for r in 0..total {
        if by_record[r] == u32::MAX { continue; }
        index.name_start.push(index.names.len() as u32);
        if r != ROOT { index.names.extend_from_slice(&names[name_at[r] as usize..name_at[r] as usize + name_len[r] as usize]); }
        let p = parent[r] as usize;
        let mapped = if r == ROOT { root } else { by_record.get(p).copied().unwrap_or(u32::MAX) };
        index.parent.push(if mapped == u32::MAX { stats.orphans += 1; root } else { mapped });
        index.size.push(if flags[r] & DIR != 0 { 0 } else { size[r] });
        index.modified.push(modified[r]);
        index.flags.push(flags[r]);
    }
    index.name_start.push(index.names.len() as u32);
    index.names.shrink_to_fit();
    index.by_record = by_record;
    stats.total_time = started.elapsed();
    Ok((index, stats))
}

/// Adds every file's size to all the folders above it.
fn folder_totals(index: &mut Index) -> Duration {
    let started = Instant::now();
    for i in 0..index.len() {
        if index.flags[i] & DIR != 0 { continue; }
        let bytes = index.size[i];
        if bytes == 0 { continue; }
        let mut at = index.parent[i];
        for _ in 0..512 {
            index.size[at as usize] += bytes;
            if at == index.root { break; }
            at = index.parent[at as usize];
        }
    }
    started.elapsed()
}

// ---- snapshot ------------------------------------------------------------------------------------------------------

fn as_bytes<T: Copy>(v: &[T]) -> &[u8] {
    unsafe { std::slice::from_raw_parts(v.as_ptr() as *const u8, std::mem::size_of_val(v)) }
}

/// Writes the index arrays to a file and reads them back: roughly what a service start from a snapshot costs.
fn snapshot_round_trip(index: &Index, folder: &std::path::Path) -> Result<(u64, Duration, Duration), String> {
    let path = folder.join(format!("snapshot-{}.bin", index.letter));
    let started = Instant::now();
    {
        use std::io::Write;
        let mut file = std::fs::File::create(&path).map_err(|e| e.to_string())?;
        for part in [as_bytes(&index.names), as_bytes(&index.name_start), as_bytes(&index.parent), as_bytes(&index.size),
            as_bytes(&index.modified), as_bytes(&index.flags), as_bytes(&index.by_record)] {
            file.write_all(&(part.len() as u64).to_le_bytes()).map_err(|e| e.to_string())?;
            file.write_all(part).map_err(|e| e.to_string())?;
        }
        file.sync_all().map_err(|e| e.to_string())?;
    }
    let write = started.elapsed();
    let started = Instant::now();
    let data = std::fs::read(&path).map_err(|e| e.to_string())?;
    let read = started.elapsed();
    let bytes = data.len() as u64;
    drop(data);
    // The snapshot holds every file name on the drive; don't leave it lying around.
    let _ = std::fs::remove_file(&path);
    Ok((bytes, write, read))
}

// ---- search --------------------------------------------------------------------------------------------------------

#[derive(Clone, Copy)]
struct Hit {
    score: i32,
    volume: usize,
    entry: u32,
}

fn score(name: &str, terms: &[String], is_dir: bool) -> Option<i32> {
    let mut first = 0usize;
    for (k, term) in terms.iter().enumerate() {
        let at = name.find(term.as_str())?;
        if k == 0 { first = at; }
    }
    let t = &terms[0];
    let mut s = if terms.len() == 1 && name.len() == t.len() { 1000 }
        else if first == 0 { 800 }
        else if !name.as_bytes()[first - 1].is_ascii_alphanumeric() { 600 }
        else { 400 };
    s -= name.len().min(200) as i32;
    if is_dir { s += 50; }
    Some(s)
}

fn is_subsequence(name: &str, query: &str) -> bool {
    let mut q = query.chars().filter(|c| !c.is_whitespace()).peekable();
    for c in name.chars() {
        if q.peek() == Some(&c) { q.next(); }
        if q.peek().is_none() { return true; }
    }
    q.peek().is_none()
}

/// Scans every name on all volumes in parallel. `pre_lowered` uses the lowercase copy instead of lowering on the fly.
fn search(indexes: &[Index], query: &str, pre_lowered: bool, top: usize) -> (Vec<Hit>, usize) {
    let terms: Vec<String> = query.to_lowercase().split_whitespace().map(str::to_owned).collect();
    if terms.is_empty() { return (Vec::new(), 0); }
    let threads = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4);
    let mut tasks = Vec::new();
    for (v, index) in indexes.iter().enumerate() {
        let step = index.len().div_ceil(threads * 4).max(1);
        let mut start = 0;
        while start < index.len() {
            tasks.push((v, start, (start + step).min(index.len())));
            start += step;
        }
    }
    let next = AtomicUsize::new(0);
    let results: Vec<(Vec<Hit>, usize)> = std::thread::scope(|scope| {
        let workers: Vec<_> = (0..threads).map(|_| scope.spawn(|| {
            let mut best: Vec<Hit> = Vec::with_capacity(top + 1);
            let mut matches = 0usize;
            let mut buffer = String::new();
            loop {
                let t = next.fetch_add(1, Ordering::Relaxed);
                let Some(&(v, start, end)) = tasks.get(t) else { break };
                let index = &indexes[v];
                for i in start..end {
                    let name: &str = if pre_lowered { index.lower_name(i) } else {
                        let raw = index.name(i);
                        buffer.clear();
                        if raw.is_ascii() { buffer.push_str(raw); buffer.make_ascii_lowercase(); } else { buffer.push_str(&raw.to_lowercase()); }
                        &buffer
                    };
                    let Some(s) = score(name, &terms, index.flags[i] & DIR != 0) else { continue };
                    matches += 1;
                    if best.len() < top || s > best[best.len() - 1].score {
                        let at = best.partition_point(|h| h.score >= s);
                        best.insert(at, Hit { score: s, volume: v, entry: i as u32 });
                        best.truncate(top);
                    }
                }
            }
            (best, matches)
        })).collect();
        workers.into_iter().map(|w| w.join().unwrap()).collect()
    });
    let mut all: Vec<Hit> = results.iter().flat_map(|(b, _)| b.iter().copied()).collect();
    all.sort_by(|a, b| b.score.cmp(&a.score));
    all.truncate(top);
    (all, results.iter().map(|(_, m)| m).sum())
}

fn count_subsequence(indexes: &[Index], query: &str) -> usize {
    let q = query.to_lowercase();
    indexes.iter().map(|index| (0..index.len()).filter(|&i| is_subsequence(index.lower_name(i), &q)).count()).sum()
}

fn median_time(mut f: impl FnMut(), runs: usize) -> Duration {
    f();
    let mut times: Vec<Duration> = (0..runs).map(|_| { let t = Instant::now(); f(); t.elapsed() }).collect();
    times.sort();
    times[runs / 2]
}

// ---- journal watch -------------------------------------------------------------------------------------------------

struct Change {
    volume: usize,
    reason: u32,
    parent_record: u64,
    name: String,
    folder: bool,
}

fn reasons(reason: u32) -> String {
    const NAMES: [(u32, &str); 9] = [(0x100, "create"), (0x200, "delete"), (0x1000, "rename-from"), (0x2000, "rename-to"),
        (0x1, "overwrite"), (0x2, "extend"), (0x4, "truncate"), (0x8000, "attributes"), (0x8000_0000, "close")];
    let parts: Vec<&str> = NAMES.iter().filter(|(bit, _)| reason & bit != 0).map(|(_, n)| *n).collect();
    if parts.is_empty() { format!("0x{reason:x}") } else { parts.join("+") }
}

fn watch(indexes: &[Index], volumes: Vec<(usize, Volume, Journal)>, seconds: u64, report: &mut Report) {
    say!(report, "Following the change journals for {seconds} s. Create, rename or delete a file now to see it here.");
    let (tx, rx) = mpsc::channel::<Change>();
    for (v, volume, journal) in volumes {
        let tx = tx.clone();
        // Detached: a blocked journal read can't be interrupted, so these end with the process.
        std::thread::spawn(move || {
            let mut start = journal.next_usn;
            let mut buffer = vec![0u8; 64 * 1024];
            loop {
                // Wait for at least one record, giving up after the timeout so the loop stays responsive.
                let input = read_journal_input(start, journal.id, 1, 1);
                let n = match volume.ioctl(win::FSCTL_READ_USN_JOURNAL, &input, &mut buffer) {
                    Ok(n) => n,
                    Err(_) => return,
                };
                if n < 8 { continue; }
                let next = i64_at(&buffer, 0);
                if next == start { std::thread::sleep(Duration::from_millis(50)); continue; }
                start = next;
                let mut offset = 8;
                while offset + 60 <= n {
                    let length = u32_at(&buffer, offset) as usize;
                    if length == 0 { break; }
                    let r = &buffer[offset..offset + length];
                    let (name_length, name_offset) = (u16_at(r, 56) as usize, u16_at(r, 58) as usize);
                    let units: Vec<u16> = (0..name_length / 2).map(|k| u16_at(r, name_offset + 2 * k)).collect();
                    let change = Change { volume: v, reason: u32_at(r, 40), parent_record: u64_at(r, 16) & RECORD_MASK,
                        name: String::from_utf16_lossy(&units), folder: u32_at(r, 52) & 0x10 != 0 };
                    if tx.send(change).is_err() { return; }
                    offset += length;
                }
            }
        });
    }
    drop(tx);
    let deadline = Instant::now() + Duration::from_secs(seconds);
    let (mut total, mut shown) = (0usize, 0usize);
    while let Some(left) = deadline.checked_duration_since(Instant::now()) {
        let Ok(change) = rx.recv_timeout(left) else { break };
        total += 1;
        // Closes summarize what happened to a file; show those (and deletes/renames, which are final).
        if change.reason & (0x8000_0000 | 0x200 | 0x1000) == 0 || shown >= 60 { continue; }
        shown += 1;
        let index = &indexes[change.volume];
        let folder = index.by_record.get(change.parent_record as usize).copied().filter(|&e| e != u32::MAX)
            .map(|e| index.path(e as usize)).unwrap_or_else(|| format!("{}:\\?", index.letter));
        let sep = if folder.ends_with('\\') { "" } else { "\\" };
        say!(report, "  {:<28} {folder}{sep}{}{}", reasons(change.reason), change.name, if change.folder { "\\" } else { "" });
    }
    say!(report, "{total} journal records in {seconds} s ({shown} shown).");
}

// ---- main ----------------------------------------------------------------------------------------------------------

fn main() {
    let mut letters: Vec<char> = Vec::new();
    let mut queries: Vec<String> = Vec::new();
    let mut watch_seconds = 0u64;
    let mut out = String::from("probe-report.txt");
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--query" => queries.extend(args.next()),
            "--watch" => watch_seconds = args.next().and_then(|s| s.parse().ok()).unwrap_or(30),
            "--out" => out = args.next().unwrap_or(out),
            "-h" | "--help" => {
                println!("index-probe [C D E] [--query text]... [--watch seconds] [--out report.txt]  (run elevated)");
                return;
            }
            a => letters.extend(a.chars().next().filter(char::is_ascii_alphabetic).map(|c| c.to_ascii_uppercase())),
        }
    }
    if letters.is_empty() { letters = vec!['C', 'D', 'E']; }
    if queries.is_empty() {
        queries = ["tooltips", "dragon age", "readme", "glaze", "s"].map(String::from).to_vec();
    }
    let out_path = std::path::PathBuf::from(&out);
    let out_folder = out_path.parent().filter(|p| !p.as_os_str().is_empty()).map(|p| p.to_path_buf()).unwrap_or_else(|| ".".into());
    let mut report = Report { text: String::new(), path: out.clone() };

    say!(report, "index-probe: drives {}", letters.iter().map(|c| format!("{c}:")).collect::<Vec<_>>().join(" "));
    say!(report, "threads: {}", std::thread::available_parallelism().map(|n| n.get()).unwrap_or(0));
    let (private_start, _) = memory();

    let mut indexes: Vec<Index> = Vec::new();
    let mut journals: Vec<(usize, Volume, Journal)> = Vec::new();
    for &letter in &letters {
        say!(report);
        say!(report, "== {letter}: ==");
        let volume = match Volume::open(letter) {
            Ok(v) => v,
            Err(e) => { say!(report, "  skipped: {e}"); continue; }
        };
        let info = match ntfs_info(&volume) {
            Ok(i) => i,
            Err(e) => { say!(report, "  skipped: {e}"); continue; }
        };
        let used = (info.total_clusters - info.free_clusters) * info.bytes_per_cluster;
        say!(report, "  volume: {} used of {}, {}-byte clusters, {}-byte MFT records, MFT {}",
            gb(used), gb(info.total_clusters * info.bytes_per_cluster), info.bytes_per_cluster, info.record_size,
            gb(info.mft_valid_length));

        let journal = query_journal(&volume);
        match &journal {
            Ok(j) => {
                let oldest = journal_oldest(&volume, j).map(filetime_utc).unwrap_or_else(|| "unknown".into());
                say!(report, "  USN journal: max {} , {} in use, oldest record {oldest}",
                    mb(j.maximum_size as usize), mb((j.next_usn - j.first_usn).max(0) as usize));
            }
            Err(win::ERROR_JOURNAL_NOT_ACTIVE) => say!(report, "  USN journal: not active on this volume"),
            Err(e) => say!(report, "  USN journal: query failed, error {e}"),
        }

        if let Ok(j) = &journal {
            match enum_usn_data(&volume, j.next_usn) {
                Ok((entries, folders, time)) => say!(report, "  FSCTL_ENUM_USN_DATA (names only): {entries} entries ({folders} folders) in {}", ms(time)),
                Err(e) => say!(report, "  FSCTL_ENUM_USN_DATA: {e}"),
            }
        }

        let (private_before, _) = memory();
        match scan(&volume, letter, &info) {
            Ok((mut index, stats)) => {
                let totals = folder_totals(&mut index);
                let files = index.flags.iter().filter(|f| *f & DIR == 0).count();
                let hidden = index.flags.iter().filter(|f| *f & (HIDDEN | SYSTEM) != 0).count();
                say!(report, "  MFT read: {} records ({} in use, {} extension, {} with attribute lists, {} unreadable), {} read",
                    stats.records, stats.in_use, stats.extension_records, stats.attribute_lists, stats.bad_records, gb(stats.mft_bytes));
                say!(report, "    time {} (disk reads {}, {:.1} GB/s), folder totals {}",
                    ms(stats.total_time), ms(stats.read_time),
                    stats.mft_bytes as f64 / 1_073_741_824.0 / stats.read_time.as_secs_f64().max(1e-9), ms(totals));
                say!(report, "  index: {} entries = {files} files + {} folders; {hidden} hidden/system; {} extra hard-link names; {} orphans",
                    index.len(), index.len() - files, stats.extra_hard_links, stats.orphans);
                let root_total = index.size[index.root as usize];
                say!(report, "    files add up to {} ({:.0}% of the used space; the rest is metadata, slack, compression, sparse files)",
                    gb(root_total), root_total as f64 * 100.0 / used.max(1) as f64);
                say!(report, "    names {} (avg {:.1} bytes), core arrays {} = {:.0} bytes per entry",
                    mb(index.names.len()), index.names.len() as f64 / index.len().max(1) as f64, mb(index.core_bytes()),
                    index.core_bytes() as f64 / index.len().max(1) as f64);
                let (private_after, _) = memory();
                say!(report, "    process private memory +{} for this volume", mb(private_after.saturating_sub(private_before)));
                match snapshot_round_trip(&index, &out_folder) {
                    Ok((bytes, write, read)) => say!(report, "  snapshot: {} written in {}, read back in {} (deleted again)", mb(bytes as usize), ms(write), ms(read)),
                    Err(e) => say!(report, "  snapshot: failed: {e}"),
                }
                let v = indexes.len();
                indexes.push(index);
                if let Ok(j) = journal { journals.push((v, volume, j)); }
            }
            Err(e) => say!(report, "  MFT read failed: {e}"),
        }
        report.save();
    }

    if indexes.is_empty() {
        say!(report);
        say!(report, "Nothing indexed.");
        report.save();
        return;
    }

    say!(report);
    say!(report, "== All volumes ==");
    let entries: usize = indexes.iter().map(Index::len).sum();
    let core: usize = indexes.iter().map(Index::core_bytes).sum();
    let (private_now, peak) = memory();
    say!(report, "  {entries} entries, core arrays {}, process private {} (peak {} while scanning; started at {})",
        mb(core), mb(private_now), mb(peak), mb(private_start));
    let lower_started = Instant::now();
    for index in &mut indexes { index.build_lower(); }
    let lower: usize = indexes.iter().map(Index::lower_bytes).sum();
    say!(report, "  lowercase copy for search: {} more, built in {}", mb(lower), ms(lower_started.elapsed()));

    say!(report);
    say!(report, "== Search (all volumes, median of 5) ==");
    for query in &queries {
        let mut hits = (Vec::new(), 0);
        let live = median_time(|| hits = search(&indexes, query, false, 20), 5);
        let lowered = median_time(|| { search(&indexes, query, true, 20); }, 5);
        let subsequence = if query.len() > 2 { count_subsequence(&indexes, query).to_string() } else { "-".into() };
        say!(report, "  \"{query}\": {} matches; {} lowering on the fly, {} with the lowercase copy; loose subsequence matches: {subsequence}",
            hits.1, ms(live), ms(lowered));
        for hit in hits.0.iter().take(if query == &queries[0] { 10 } else { 5 }) {
            let index = &indexes[hit.volume];
            let i = hit.entry as usize;
            let kind = if index.flags[i] & DIR != 0 { "dir " } else { "file" };
            say!(report, "    {kind} {:>10}  {}", gb(index.size[i]), index.path(i));
        }
    }
    report.save();

    if watch_seconds > 0 && !journals.is_empty() {
        say!(report);
        watch(&indexes, journals, watch_seconds, &mut report);
    }
    say!(report);
    say!(report, "Report saved to {out}");
    report.save();
}
