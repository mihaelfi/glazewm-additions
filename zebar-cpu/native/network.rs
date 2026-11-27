//! Physical Wi-Fi and Ethernet throughput from Windows IP Helper counters.

use std::{collections::BTreeMap, mem, ptr, slice, time::Instant};

use serde::Serialize;
use windows::Win32::{
    Foundation::ERROR_SUCCESS,
    NetworkManagement::{
        IpHelper::{
            FreeMibTable, GetIfTable2, IF_TYPE_ETHERNET_CSMACD, IF_TYPE_IEEE80211, MIB_IF_ROW2,
            MIB_IF_TABLE2,
        },
        Ndis::IfOperStatusUp,
    },
};

// HardwareInterface, FilterInterface and NotMediaConnected in MIB_IF_ROW2.
const HARDWARE: u8 = 1 << 0;
const FILTER: u8 = 1 << 1;
const MEDIA_DISCONNECTED: u8 = 1 << 4;

#[derive(Clone, Copy, Serialize)]
pub struct Throughput {
    download: Option<u64>,
    upload: Option<u64>,
}

pub type Rates = BTreeMap<String, Throughput>;

#[derive(Serialize)]
#[serde(rename_all = "lowercase")]
enum Kind {
    Ethernet,
    Wifi,
}

#[derive(Serialize)]
pub struct Adapter {
    id: String,
    name: String,
    kind: Kind,
}

#[derive(Serialize)]
pub struct Snapshot {
    adapters: Vec<Adapter>,
    pub rates: Rates,
    total: Throughput,
}

#[derive(Default)]
pub struct Sampler {
    counters: BTreeMap<String, (u64, u64)>,
    sampled_at: Option<Instant>,
}

impl Sampler {
    pub fn sample(&mut self) -> Option<Snapshot> {
        let previous = mem::take(&mut self.counters);
        let sampled_at = self.sampled_at.take();
        let table = InterfaceTable::read()?;
        let now = Instant::now();
        self.sampled_at = Some(now);
        let seconds = sampled_at.map(|previous| now.duration_since(previous).as_secs_f64());
        let mut adapters = Vec::new();
        let mut rates = Rates::new();

        for row in table.rows() {
            let flags = row.InterfaceAndOperStatusFlags._bitfield;
            if row.OperStatus != IfOperStatusUp
                || flags & (HARDWARE | FILTER | MEDIA_DISCONNECTED) != HARDWARE
            {
                continue;
            }
            let kind = match row.Type {
                IF_TYPE_ETHERNET_CSMACD => Kind::Ethernet,
                IF_TYPE_IEEE80211 => Kind::Wifi,
                _ => continue,
            };
            let id = format!("{:?}", row.InterfaceGuid);
            let byte_rate = |current: u64, previous: u64| {
                let elapsed = seconds.filter(|value| *value > 0.0)?;
                Some((current.checked_sub(previous)? as f64 / elapsed).round() as u64)
            };
            let before = previous.get(&id);
            rates.insert(
                id.clone(),
                Throughput {
                    download: before.and_then(|&(received, _)| byte_rate(row.InOctets, received)),
                    upload: before.and_then(|&(_, sent)| byte_rate(row.OutOctets, sent)),
                },
            );
            self.counters
                .insert(id.clone(), (row.InOctets, row.OutOctets));
            let end = row
                .Alias
                .iter()
                .position(|&unit| unit == 0)
                .unwrap_or(row.Alias.len());
            adapters.push(Adapter {
                id,
                name: String::from_utf16_lossy(&row.Alias[..end]),
                kind,
            });
        }

        adapters.sort_by(|left, right| left.name.cmp(&right.name).then(left.id.cmp(&right.id)));
        let total = Throughput {
            download: rates
                .values()
                .try_fold(0u64, |sum, rate| Some(sum.saturating_add(rate.download?))),
            upload: rates
                .values()
                .try_fold(0u64, |sum, rate| Some(sum.saturating_add(rate.upload?))),
        };
        Some(Snapshot {
            adapters,
            rates,
            total,
        })
    }
}

struct InterfaceTable(*mut MIB_IF_TABLE2);

impl InterfaceTable {
    fn read() -> Option<Self> {
        let mut table = ptr::null_mut();
        let status = unsafe { GetIfTable2(&mut table) };
        (status == ERROR_SUCCESS && !table.is_null()).then(|| Self(table))
    }

    fn rows(&self) -> &[MIB_IF_ROW2] {
        // IP Helper allocates NumEntries rows starting at the C-layout Table member.
        unsafe {
            let count = (*self.0).NumEntries as usize;
            if count == 0 {
                return &[];
            }
            let rows = ptr::addr_of!((*self.0).Table).cast::<MIB_IF_ROW2>();
            slice::from_raw_parts(rows, count)
        }
    }
}

impl Drop for InterfaceTable {
    fn drop(&mut self) {
        unsafe { FreeMibTable(self.0.cast()) };
    }
}
