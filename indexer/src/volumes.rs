//! Which drives get indexed: fixed, NTFS, and not on a USB bus. Found at runtime, so it works on any PC.

use std::ptr::null_mut;

use crate::ntfs::Volume;
use crate::win;

pub struct Candidate {
    pub letter: char,
    pub serial: u32,
}

/// Every drive letter with the reason it was skipped, or a candidate.
pub fn find() -> Vec<Result<Candidate, String>> {
    let mask = unsafe { win::GetLogicalDrives() };
    let mut out = Vec::new();
    for bit in 0..26 {
        if mask & (1 << bit) == 0 { continue; }
        let letter = (b'A' + bit as u8) as char;
        let root = win::wide(&format!("{letter}:\\"));
        if unsafe { win::GetDriveTypeW(root.as_ptr()) } != win::DRIVE_FIXED { continue; }
        let mut serial = 0u32;
        let mut fs = [0u16; 32];
        let ok = unsafe {
            win::GetVolumeInformationW(root.as_ptr(), null_mut(), 0, &mut serial, null_mut(), null_mut(), fs.as_mut_ptr(), fs.len() as u32)
        };
        if ok == 0 { out.push(Err(format!("{letter}: skipped: no volume information (error {})", win::last_error()))); continue; }
        let fs_name = String::from_utf16_lossy(&fs[..fs.iter().position(|&c| c == 0).unwrap_or(fs.len())]);
        if fs_name != "NTFS" { out.push(Err(format!("{letter}: skipped: {fs_name}, not NTFS"))); continue; }
        match bus_type(letter) {
            Some(win::BUS_TYPE_USB) => { out.push(Err(format!("{letter}: skipped: USB drive"))); continue; }
            None => { out.push(Err(format!("{letter}: skipped: can't read its bus type"))); continue; }
            _ => {}
        }
        out.push(Ok(Candidate { letter, serial }));
    }
    out
}

/// STORAGE_DEVICE_DESCRIPTOR.BusType through IOCTL_STORAGE_QUERY_PROPERTY (StorageDeviceProperty).
fn bus_type(letter: char) -> Option<u32> {
    let volume = Volume::open(letter).ok()?;
    let query = [0u8; 12];
    let mut out = [0u8; 1024];
    let n = volume.ioctl(win::IOCTL_STORAGE_QUERY_PROPERTY, &query, &mut out).ok()?;
    (n >= 32).then(|| u32::from_le_bytes(out[28..32].try_into().unwrap()))
}
