//! Logical disk throughput in bytes per second, keyed by drive letter.

use std::collections::BTreeMap;

use serde::Serialize;
use windows::{
    Win32::System::Performance::{PDH_FMT, PDH_FMT_LARGE, PDH_HCOUNTER},
    core::w,
};

use crate::pdh::{Query, for_each_value};

// pdh.h flags missing from the windows bindings. Keep the rate in bytes/sec.
const PDH_FMT_NOSCALE: u32 = 0x1000;
const PDH_FMT_NOCAP100: u32 = 0x8000;
const RATE_FORMAT: PDH_FMT = PDH_FMT(PDH_FMT_LARGE.0 | PDH_FMT_NOSCALE | PDH_FMT_NOCAP100);

#[derive(Default, Serialize)]
pub struct DriveRates {
    read: Option<u64>,
    write: Option<u64>,
}

pub type Rates = BTreeMap<char, DriveRates>;

pub struct Sampler {
    query: Query,
    read: PDH_HCOUNTER,
    write: PDH_HCOUNTER,
    buffer: Vec<u64>,
}

impl Sampler {
    pub fn new() -> Option<Self> {
        let query = Query::open()?;
        let read = query.add(w!("\\LogicalDisk(*)\\Disk Read Bytes/sec"))?;
        let write = query.add(w!("\\LogicalDisk(*)\\Disk Write Bytes/sec"))?;
        // The first reading is the baseline for the first one-second sample.
        query.collect();
        Some(Self {
            query,
            read,
            write,
            buffer: Vec::new(),
        })
    }

    pub fn sample(&mut self, rates: &mut Rates) {
        rates.clear();
        if !self.query.collect() {
            return;
        }

        for_each_value(self.read, RATE_FORMAT, &mut self.buffer, |name, value| {
            let rate = u64::try_from(unsafe { value.Anonymous.largeValue }).ok();
            if let Some(letter) = drive_letter(name) {
                rates.entry(letter).or_default().read = rate;
            }
        });
        for_each_value(self.write, RATE_FORMAT, &mut self.buffer, |name, value| {
            let rate = u64::try_from(unsafe { value.Anonymous.largeValue }).ok();
            if let Some(letter) = drive_letter(name) {
                rates.entry(letter).or_default().write = rate;
            }
        });
    }
}

fn drive_letter(instance: &str) -> Option<char> {
    match instance.as_bytes() {
        [letter, b':'] if letter.is_ascii_alphabetic() => Some(letter.to_ascii_uppercase().into()),
        _ => None,
    }
}
