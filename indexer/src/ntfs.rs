//! NTFS access: the volume handle, reading the master file table (MFT) into an index, and the USN change journal.

use std::ffi::c_void;
use std::ptr::null_mut;
use std::time::{Duration, Instant};

use crate::index::{Index, DIR, HIDDEN, SYSTEM};
use crate::win::{self, OwnedHandle};

pub const RECORD_MASK: u64 = 0x0000_FFFF_FFFF_FFFF;
pub const ROOT_RECORD: usize = 5;
pub const EXTEND_RECORD: u32 = 11;

fn u16_at(b: &[u8], o: usize) -> u16 { u16::from_le_bytes([b[o], b[o + 1]]) }
fn u32_at(b: &[u8], o: usize) -> u32 { u32::from_le_bytes(b[o..o + 4].try_into().unwrap()) }
fn u64_at(b: &[u8], o: usize) -> u64 { u64::from_le_bytes(b[o..o + 8].try_into().unwrap()) }

pub struct Volume {
    pub letter: char,
    handle: OwnedHandle,
}

// The handle is only used for synchronous ioctls and reads, which are safe from any thread.
unsafe impl Send for Volume {}
unsafe impl Sync for Volume {}

impl Volume {
    pub fn open(letter: char) -> Result<Volume, String> {
        Self::open_with(letter, win::GENERIC_READ)
    }

    fn open_with(letter: char, access: u32) -> Result<Volume, String> {
        let path = win::wide(&format!("\\\\.\\{letter}:"));
        let handle = unsafe { win::CreateFileW(path.as_ptr(), access, win::FILE_SHARE_ALL, null_mut(), win::OPEN_EXISTING, 0, 0) };
        if handle == win::INVALID_HANDLE {
            return Err(match win::last_error() {
                win::ERROR_ACCESS_DENIED => "access denied (needs admin)".into(),
                e => format!("can't open the volume: error {e}"),
            });
        }
        Ok(Volume { letter, handle: OwnedHandle(handle) })
    }

    pub fn ioctl(&self, code: u32, input: &[u8], output: &mut [u8]) -> Result<usize, u32> {
        let mut returned = 0u32;
        let ok = unsafe {
            win::DeviceIoControl(self.handle.0, code, input.as_ptr() as *const c_void, input.len() as u32,
                output.as_mut_ptr() as *mut c_void, output.len() as u32, &mut returned, null_mut())
        };
        if ok == 0 { Err(win::last_error()) } else { Ok(returned as usize) }
    }

    fn read_at(&self, offset: u64, buffer: &mut [u8]) -> Result<(), String> {
        let mut read = 0u32;
        let ok = unsafe {
            win::SetFilePointerEx(self.handle.0, offset as i64, null_mut(), 0) != 0
                && win::ReadFile(self.handle.0, buffer.as_mut_ptr(), buffer.len() as u32, &mut read, null_mut()) != 0
        };
        if !ok { return Err(format!("read at {offset} failed: error {}", win::last_error())); }
        if read as usize != buffer.len() { return Err(format!("short read at {offset}")); }
        Ok(())
    }

    /// A file's size, last-write time and attributes, by its full 64-bit file reference number. None if it's gone.
    pub fn file_info(&self, file_reference: u64) -> Option<(u64, u64, u32)> {
        let id = win::FileIdDescriptor { size: 24, kind: 0, id: file_reference, pad: 0 };
        let handle = unsafe {
            win::OpenFileById(self.handle.0, &id, win::FILE_READ_ATTRIBUTES, win::FILE_SHARE_ALL, null_mut(), win::FILE_FLAG_BACKUP_SEMANTICS)
        };
        if handle == win::INVALID_HANDLE { return None; }
        let handle = OwnedHandle(handle);
        let mut basic = [0u8; 40];
        let mut standard = [0u8; 24];
        unsafe {
            if win::GetFileInformationByHandleEx(handle.0, 0, basic.as_mut_ptr() as *mut c_void, 40) == 0 { return None; }
            if win::GetFileInformationByHandleEx(handle.0, 1, standard.as_mut_ptr() as *mut c_void, 24) == 0 { return None; }
        }
        Some((u64_at(&standard, 8), u64_at(&basic, 16), u32_at(&basic, 32)))
    }
}

// ---- volume facts ---------------------------------------------------------------------------------------------------

pub struct NtfsInfo {
    pub bytes_per_cluster: u64,
    pub record_size: usize,
    pub mft_valid_length: u64,
    pub mft_start_lcn: u64,
}

pub fn ntfs_info(volume: &Volume) -> Result<NtfsInfo, String> {
    let mut buffer = [0u8; 256];
    volume.ioctl(win::FSCTL_GET_NTFS_VOLUME_DATA, &[], &mut buffer)
        .map_err(|e| format!("FSCTL_GET_NTFS_VOLUME_DATA error {e}"))?;
    Ok(NtfsInfo {
        bytes_per_cluster: u32_at(&buffer, 44) as u64,
        record_size: u32_at(&buffer, 48) as usize,
        mft_valid_length: u64_at(&buffer, 56),
        mft_start_lcn: u64_at(&buffer, 64),
    })
}

// ---- journal --------------------------------------------------------------------------------------------------------

#[derive(Clone, Copy)]
pub struct Journal {
    pub id: u64,
    pub first_usn: i64,
    pub next_usn: i64,
}

pub fn query_journal(volume: &Volume) -> Result<Journal, u32> {
    let mut buffer = [0u8; 80];
    volume.ioctl(win::FSCTL_QUERY_USN_JOURNAL, &[], &mut buffer)?;
    Ok(Journal { id: u64_at(&buffer, 0), first_usn: u64_at(&buffer, 8) as i64, next_usn: u64_at(&buffer, 16) as i64 })
}

/// Returns the volume's journal, creating one (32 MB, 8 MB growth, as Windows does for the system drive) when it has
/// none. An existing journal is never changed.
pub fn ensure_journal(volume: &Volume, create: bool) -> Result<Journal, String> {
    for attempt in 0..10 {
        match query_journal(volume) {
            Ok(j) => return Ok(j),
            Err(win::ERROR_JOURNAL_DELETE_IN_PROGRESS) => std::thread::sleep(Duration::from_secs(1 + attempt)),
            Err(win::ERROR_JOURNAL_NOT_ACTIVE) if !create => return Err("no USN change journal, and creating one is turned off (create_journals)".into()),
            Err(win::ERROR_JOURNAL_NOT_ACTIVE) => {
                let mut input = [0u8; 16];
                input[0..8].copy_from_slice(&(32u64 << 20).to_le_bytes());
                input[8..16].copy_from_slice(&(8u64 << 20).to_le_bytes());
                let created = volume.ioctl(win::FSCTL_CREATE_USN_JOURNAL, &input, &mut []).or_else(|e| {
                    if e != win::ERROR_ACCESS_DENIED { return Err(e); }
                    let writable = Volume::open_with(volume.letter, win::GENERIC_READ | win::GENERIC_WRITE).map_err(|_| e)?;
                    writable.ioctl(win::FSCTL_CREATE_USN_JOURNAL, &input, &mut [])
                });
                match created {
                    Ok(_) => crate::log!("{}: created a USN change journal (32 MB); undo with: fsutil usn deletejournal /d {}:", volume.letter, volume.letter),
                    Err(e) => return Err(format!("no journal, and creating one failed: error {e}")),
                }
            }
            Err(e) => return Err(format!("journal query failed: error {e}")),
        }
    }
    Err("the journal stayed in a delete".into())
}

/// One change from the journal (USN_RECORD_V2).
pub struct Change {
    pub file_reference: u64,
    pub record: u64,
    pub parent: u64,
    pub reason: u32,
    pub attributes: u32,
    pub name: String,
    /// Size, last-write time and attributes looked up after a close, for files whose content may have changed.
    pub info: Option<(u64, u64, u32)>,
}

pub const REASON_DATA: u32 = 0x1 | 0x2 | 0x4 | 0x10 | 0x20 | 0x40;
pub const REASON_CREATE: u32 = 0x100;
pub const REASON_DELETE: u32 = 0x200;
pub const REASON_RENAME_NEW: u32 = 0x2000;
pub const REASON_BASIC_INFO: u32 = 0x8000;
pub const REASON_CLOSE: u32 = 0x8000_0000;

pub enum ReadResult {
    Changes(Vec<Change>, i64),
    /// The saved position is no longer in the journal (it wrapped, or was deleted): rescan.
    Lost(String),
}

/// Waits up to `timeout_s` for changes after `start` and returns them with the next position.
pub fn read_journal(volume: &Volume, journal_id: u64, start: i64, timeout_s: u64, buffer: &mut [u8]) -> ReadResult {
    let mut input = [0u8; 40];
    input[0..8].copy_from_slice(&start.to_le_bytes());
    input[8..12].copy_from_slice(&u32::MAX.to_le_bytes());
    input[16..24].copy_from_slice(&timeout_s.to_le_bytes());
    input[24..32].copy_from_slice(&1u64.to_le_bytes());
    input[32..40].copy_from_slice(&journal_id.to_le_bytes());
    let n = match volume.ioctl(win::FSCTL_READ_USN_JOURNAL, &input, buffer) {
        Ok(n) => n,
        Err(e @ (win::ERROR_JOURNAL_ENTRY_DELETED | win::ERROR_JOURNAL_NOT_ACTIVE | win::ERROR_JOURNAL_DELETE_IN_PROGRESS)) =>
            return ReadResult::Lost(format!("journal error {e}")),
        Err(e) => return ReadResult::Lost(format!("journal read failed: error {e}")),
    };
    if n < 8 { return ReadResult::Changes(Vec::new(), start); }
    let next = u64_at(buffer, 0) as i64;
    let mut changes = Vec::new();
    let mut offset = 8;
    while offset + 60 <= n {
        let length = u32_at(buffer, offset) as usize;
        if length < 60 || offset + length > n { break; }
        let r = &buffer[offset..offset + length];
        if u16_at(r, 4) == 2 {
            let (name_length, name_offset) = (u16_at(r, 56) as usize, u16_at(r, 58) as usize);
            if name_offset + name_length <= length {
                let units: Vec<u16> = (0..name_length / 2).map(|k| u16_at(r, name_offset + 2 * k)).collect();
                let file_reference = u64_at(r, 8);
                changes.push(Change {
                    file_reference,
                    record: file_reference & RECORD_MASK,
                    parent: u64_at(r, 16) & RECORD_MASK,
                    reason: u32_at(r, 40),
                    attributes: u32_at(r, 52),
                    name: String::from_utf16_lossy(&units),
                    info: None,
                });
            }
        }
        offset += length;
    }
    ReadResult::Changes(changes, next)
}

// ---- MFT scan ---------------------------------------------------------------------------------------------------------

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
        if offset_size == 0 { runs.push((-1, length)); continue; }
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

pub struct ScanStats {
    pub entries: usize,
    pub time: Duration,
}

/// Reads the whole MFT and builds the volume's index (folder totals and flags included).
pub fn scan(volume: &Volume, rules: &crate::settings::Rules) -> Result<(Index, ScanStats), String> {
    let started = Instant::now();
    let info = ntfs_info(volume)?;
    let (record_size, cluster) = (info.record_size, info.bytes_per_cluster);
    if record_size == 0 || cluster % record_size as u64 != 0 {
        return Err(format!("unsupported layout: {record_size}-byte records, {cluster}-byte clusters"));
    }

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
    let mut names: Vec<u8> = Vec::with_capacity(total * 24);

    const CHUNK: u64 = 8 << 20;
    let mut buffer = vec![0u8; CHUNK as usize];
    let mut record_index = 0usize;
    for (lcn, clusters) in runs {
        let run_bytes = clusters * cluster;
        if lcn < 0 { record_index += (run_bytes / record_size as u64) as usize; continue; }
        let mut done = 0u64;
        while done < run_bytes && record_index < total {
            let want = (run_bytes - done).min(CHUNK).min((total - record_index) as u64 * record_size as u64);
            let want = want.div_ceil(cluster) * cluster;
            let chunk = &mut buffer[..want as usize];
            volume.read_at(lcn as u64 * cluster + done, chunk)?;
            done += want;
            for record in chunk.chunks_exact_mut(record_size) {
                let index = record_index;
                record_index += 1;
                if index >= total { break; }
                if &record[0..4] != b"FILE" || !fixup(record) { continue; }
                let header_flags = u16_at(record, 0x16);
                if header_flags & 1 == 0 { continue; }
                let base = (u64_at(record, 0x20) & RECORD_MASK) as usize;
                let target = if base != 0 { base } else { index };
                if target >= total { continue; }
                if base == 0 {
                    in_use[target] = true;
                    if header_flags & 2 != 0 { flags[target] |= DIR; }
                }
                for_each_attribute(record, |kind, a| match kind {
                    0x10 => if let Some(v) = resident_value(a).filter(|v| v.len() >= 0x24) {
                        modified[target] = u64_at(v, 0x08);
                        let attributes = u32_at(v, 0x20);
                        if attributes & 2 != 0 { flags[target] |= HIDDEN; }
                        if attributes & 4 != 0 { flags[target] |= SYSTEM; }
                    },
                    0x30 => if let Some(v) = resident_value(a).filter(|v| v.len() >= 0x42) {
                        let length = v[0x40] as usize;
                        if v.len() < 0x42 + 2 * length { return; }
                        let rank = match v[0x41] { 1 | 3 => 3, 0 => 2, _ => 1 };
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

    // Keep named, in-use records; skip the NTFS metafiles (records 0-15 except the root) and everything under $Extend,
    // e.g. \$Extend\$Deleted, where files deleted while open wait under hex names.
    let mut metadata = vec![0u8; total]; // 0 unknown, 1 normal, 2 under $Extend
    let mut chain = Vec::new();
    for r in 0..total {
        if metadata[r] != 0 { continue; }
        let mut at = r;
        let verdict = loop {
            if at == ROOT_RECORD || at >= total || !in_use[at] || chain.len() > 512 { break 1; }
            if metadata[at] != 0 { break metadata[at]; }
            if at == EXTEND_RECORD as usize { break 2; }
            chain.push(at);
            at = parent[at] as usize;
        };
        for &c in &chain { metadata[c] = verdict; }
        chain.clear();
        if metadata[r] == 0 { metadata[r] = verdict; }
    }
    let mut by_record = vec![u32::MAX; total];
    let mut count = 0u32;
    for r in 0..total {
        if !in_use[r] || name_rank[r] == 0 || (r < 16 && r != ROOT_RECORD) || metadata[r] == 2 { continue; }
        by_record[r] = count;
        count += 1;
    }
    let root = by_record.get(ROOT_RECORD).copied().filter(|&e| e != u32::MAX).ok_or("the root folder is missing")?;
    let n = count as usize;
    let mut index = Index::with_capacity(volume.letter, n, names.len(), root);
    for r in 0..total {
        if by_record[r] == u32::MAX { continue; }
        let name = if r == ROOT_RECORD { &[][..] } else { &names[name_at[r] as usize..name_at[r] as usize + name_len[r] as usize] };
        let p = parent[r] as usize;
        let mapped = if r == ROOT_RECORD { root } else { by_record.get(p).copied().unwrap_or(u32::MAX) };
        let parent_entry = if mapped == u32::MAX { root } else { mapped };
        let file_size = if flags[r] & DIR != 0 { 0 } else { size[r] };
        index.push(name, parent_entry, file_size, modified[r], flags[r], r as u32);
    }
    drop((parent, name_at, name_len, name_rank, size, modified, flags, in_use, names));
    index.set_record_map(by_record);
    index.refresh(rules);
    Ok((index, ScanStats { entries: n, time: started.elapsed() }))
}
