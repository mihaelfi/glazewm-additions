//! The drives this PC has, found at runtime so it works on any PC: what each is, and whether it can be indexed (NTFS).
//! Which ones actually are is the user's choice in the settings (nothing by default).

use std::ptr::null_mut;

use crate::ntfs::Volume;
use crate::win;

#[derive(Clone)]
pub struct Drive {
    pub letter: char,
    pub serial: u32,
    pub label: String,
    pub file_system: String,
    pub usb: bool,
    pub removable: bool,
    pub total: u64,
    pub free: u64,
    /// Why it can't be indexed (not NTFS), or None.
    pub unsupported: Option<String>,
}

/// Fixed and removable drives (not network, optical or RAM disks).
pub fn detect() -> Vec<Drive> {
    let mask = unsafe { win::GetLogicalDrives() };
    let mut out = Vec::new();
    for bit in 0..26 {
        if mask & (1 << bit) == 0 { continue; }
        let letter = (b'A' + bit as u8) as char;
        let root = win::wide(&format!("{letter}:\\"));
        let kind = unsafe { win::GetDriveTypeW(root.as_ptr()) };
        if kind != win::DRIVE_FIXED && kind != win::DRIVE_REMOVABLE { continue; }
        let mut serial = 0u32;
        let mut label = [0u16; 64];
        let mut fs = [0u16; 32];
        let ok = unsafe {
            win::GetVolumeInformationW(root.as_ptr(), label.as_mut_ptr(), label.len() as u32, &mut serial, null_mut(), null_mut(), fs.as_mut_ptr(), fs.len() as u32)
        };
        if ok == 0 { continue; } // no medium (an empty card reader)
        let text = |b: &[u16]| String::from_utf16_lossy(&b[..b.iter().position(|&c| c == 0).unwrap_or(b.len())]);
        let file_system = text(&fs);
        let (mut available, mut total, mut free) = (0u64, 0u64, 0u64);
        unsafe { win::GetDiskFreeSpaceExW(root.as_ptr(), &mut available, &mut total, &mut free) };
        let usb = bus_type(letter) == Some(win::BUS_TYPE_USB);
        let unsupported = (file_system != "NTFS").then(|| format!("{file_system} isn't supported yet (NTFS only)"));
        out.push(Drive { letter, serial, label: text(&label), file_system, usb, removable: kind == win::DRIVE_REMOVABLE, total, free, unsupported });
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
