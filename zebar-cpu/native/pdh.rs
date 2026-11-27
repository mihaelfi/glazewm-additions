//! Shared Windows performance counter queries for the GPU and disks.

use std::slice;

use windows::{
    Win32::System::Performance::{
        PDH_FMT, PDH_FMT_COUNTERVALUE, PDH_FMT_COUNTERVALUE_ITEM_W, PDH_HCOUNTER, PDH_HQUERY,
        PDH_MORE_DATA, PdhAddEnglishCounterW, PdhCloseQuery, PdhCollectQueryData,
        PdhGetFormattedCounterArrayW, PdhOpenQueryW,
    },
    core::PCWSTR,
};

const PDH_CSTATUS_VALID_DATA: u32 = 0;
const PDH_CSTATUS_NEW_DATA: u32 = 1;

pub struct Query(PDH_HQUERY);

impl Query {
    pub fn open() -> Option<Self> {
        let mut handle = PDH_HQUERY::default();
        (unsafe { PdhOpenQueryW(PCWSTR::null(), 0, &mut handle) } == 0).then_some(Self(handle))
    }

    pub fn add(&self, path: PCWSTR) -> Option<PDH_HCOUNTER> {
        let mut counter = PDH_HCOUNTER::default();
        (unsafe { PdhAddEnglishCounterW(self.0, path, 0, &mut counter) } == 0).then_some(counter)
    }

    pub fn collect(&self) -> bool {
        unsafe { PdhCollectQueryData(self.0) == 0 }
    }
}

impl Drop for Query {
    fn drop(&mut self) {
        unsafe { PdhCloseQuery(self.0) };
    }
}

/// Reads every instance of a wildcard counter. The caller reuses `buffer` between calls.
pub fn for_each_value(
    counter: PDH_HCOUNTER,
    format: PDH_FMT,
    buffer: &mut Vec<u64>,
    mut visit: impl FnMut(&str, &PDH_FMT_COUNTERVALUE),
) {
    for _ in 0..3 {
        let mut size = (buffer.len() * size_of::<u64>()) as u32;
        let mut count = 0;
        let items = buffer.as_mut_ptr().cast::<PDH_FMT_COUNTERVALUE_ITEM_W>();
        let status = unsafe {
            PdhGetFormattedCounterArrayW(
                counter,
                format,
                &mut size,
                &mut count,
                (!buffer.is_empty()).then_some(items),
            )
        };
        if status == PDH_MORE_DATA {
            buffer.resize((size as usize).div_ceil(size_of::<u64>()) + 64, 0);
            continue;
        }
        if status != 0 || count == 0 {
            return;
        }
        let items = unsafe { slice::from_raw_parts(items, count as usize) };
        for item in items {
            let status = item.FmtValue.CStatus;
            if status != PDH_CSTATUS_VALID_DATA && status != PDH_CSTATUS_NEW_DATA {
                continue;
            }
            let Ok(name) = (unsafe { item.szName.to_string() }) else {
                continue;
            };
            visit(&name, &item.FmtValue);
        }
        return;
    }
}
