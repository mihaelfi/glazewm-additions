//! The space-map and listing requests: a folder's children, the largest files, space by file type, recent files, and one
//! entry's details. Excluded folders are never opened or searched; their size still counts in their parents.

use std::collections::HashMap;

use serde_json::{json, Value};

use crate::index::{Index, DELETED, DIR, EXCLUDED, HIDDEN, NOISY, SYSTEM};
use crate::search::{extension_into, keep_top, merge, parallel, Hit};

pub fn unix_ms(filetime: u64) -> i64 {
    if filetime == 0 { return 0; }
    (filetime / 10_000) as i64 - 11_644_473_600_000
}

fn filetime_of_unix_ms(ms: i64) -> u64 {
    ((ms + 11_644_473_600_000).max(0) as u64) * 10_000
}

/// One file or folder as the pipe returns it.
pub fn item(index: &Index, i: usize) -> Value {
    let flags = index.flags[i];
    let name = if i as u32 == index.root { format!("{}:\\", index.letter) } else { index.name(i).to_string() };
    json!({
        "path": index.path(i),
        "name": name,
        "folder": flags & DIR != 0,
        "size": index.size[i],
        "files": index.files[i],
        "modified": unix_ms(index.modified[i]),
        "hidden": flags & (HIDDEN | SYSTEM) != 0,
        "noisy": flags & NOISY != 0,
        "excluded": flags & EXCLUDED != 0,
    })
}

pub fn stat(index: &Index, e: u32) -> Value {
    json!({ "ok": true, "item": item(index, e as usize) })
}

/// A folder's contents for the space map, biggest first by default (`sort`: size, name, modified).
pub fn children(index: &Index, e: u32, sort: &str, limit: usize, offset: usize) -> Value {
    let i = e as usize;
    if index.flags[i] & DIR == 0 { return json!({ "ok": false, "error": "not a folder" }); }
    if index.flags[i] & EXCLUDED != 0 { return json!({ "ok": false, "error": "this folder is excluded from the index" }); }
    let mut kids = index.children(e);
    match sort {
        "name" => kids.sort_by_cached_key(|&k| (index.flags[k as usize] & DIR == 0, index.name(k as usize).to_lowercase())),
        "modified" => kids.sort_by_key(|&k| std::cmp::Reverse(index.modified[k as usize])),
        _ => kids.sort_by_key(|&k| std::cmp::Reverse(index.size[k as usize])),
    }
    let total = kids.len();
    let items: Vec<Value> = kids.iter().skip(offset).take(limit).map(|&k| item(index, k as usize)).collect();
    json!({ "ok": true, "folder": item(index, i), "count": total, "offset": offset, "items": items })
}

/// The biggest files (under `scope`, or everywhere).
pub fn largest(indexes: &[&Index], scope: Option<(usize, u32)>, limit: usize) -> Value {
    let results = parallel(indexes, || (Vec::<Hit>::with_capacity(limit + 1), 0usize), |(best, count), v, index, i| {
        let flags = index.flags[i];
        if flags & (DIR | DELETED | EXCLUDED) != 0 { return; }
        let size = index.size[i] as i64;
        if best.len() == limit && size <= best[limit - 1].key { return; }
        if let Some((sv, se)) = scope { if sv != v || !index.is_under(i, se) { return; } }
        *count += 1;
        keep_top(best, Hit { key: size, score: 0, volume: v, entry: i as u32, highlight: (0, 0) }, limit);
    });
    let outcome = merge(results, limit);
    let items: Vec<Value> = outcome.hits.iter().map(|h| item(indexes[h.volume], h.entry as usize)).collect();
    json!({ "ok": true, "items": items })
}

/// Space by file extension (under `scope`, or everywhere), biggest first.
pub fn types(indexes: &[&Index], scope: Option<(usize, u32)>, limit: usize) -> Value {
    let results = parallel(indexes, HashMap::<Box<[u8]>, (u64, u64)>::new, |map, v, index, i| {
        let flags = index.flags[i];
        if flags & (DIR | DELETED | EXCLUDED) != 0 { return; }
        if let Some((sv, se)) = scope { if sv != v || !index.is_under(i, se) { return; } }
        let mut buffer = [0u8; 16];
        let ext = extension_into(index.name(i), &mut buffer);
        match map.get_mut(ext) {
            Some(slot) => { slot.0 += index.size[i]; slot.1 += 1; }
            None => { map.insert(ext.into(), (index.size[i], 1)); }
        }
    });
    let mut totals: HashMap<Box<[u8]>, (u64, u64)> = HashMap::new();
    for map in results {
        for (ext, (bytes, files)) in map {
            let slot = totals.entry(ext).or_insert((0, 0));
            slot.0 += bytes;
            slot.1 += files;
        }
    }
    let mut list: Vec<_> = totals.into_iter().collect();
    list.sort_by_key(|(_, (bytes, _))| std::cmp::Reverse(*bytes));
    let all_bytes: u64 = list.iter().map(|(_, (b, _))| b).sum();
    let all_files: u64 = list.iter().map(|(_, (_, f))| f).sum();
    let items: Vec<Value> = list.iter().take(limit).map(|(ext, (bytes, files))| json!({
        "ext": String::from_utf8_lossy(ext),
        "size": bytes,
        "files": files,
    })).collect();
    json!({ "ok": true, "size": all_bytes, "files": all_files, "types": list.len(), "items": items })
}

/// Files changed most recently (under `scope`, or everywhere), skipping noisy places and hidden/system files unless `noisy`.
pub fn recent(indexes: &[&Index], scope: Option<(usize, u32)>, limit: usize, since_ms: i64, noisy: bool) -> Value {
    let since = if since_ms > 0 { filetime_of_unix_ms(since_ms) } else { 0 };
    // Some files carry broken future dates (mods dated 2037); a day of slack allows for clock differences.
    let now_ms = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_millis() as i64).unwrap_or(0);
    let until = filetime_of_unix_ms(now_ms + 86_400_000);
    let results = parallel(indexes, || (Vec::<Hit>::with_capacity(limit + 1), 0usize), |(best, count), v, index, i| {
        let flags = index.flags[i];
        if flags & (DIR | DELETED | EXCLUDED) != 0 { return; }
        if !noisy && flags & (NOISY | HIDDEN | SYSTEM) != 0 { return; }
        let modified = index.modified[i];
        if modified < since || modified > until { return; }
        let key = modified as i64;
        if best.len() == limit && key <= best[limit - 1].key { return; }
        if let Some((sv, se)) = scope { if sv != v || !index.is_under(i, se) { return; } }
        *count += 1;
        keep_top(best, Hit { key, score: 0, volume: v, entry: i as u32, highlight: (0, 0) }, limit);
    });
    let outcome = merge(results, limit);
    let items: Vec<Value> = outcome.hits.iter().map(|h| item(indexes[h.volume], h.entry as usize)).collect();
    json!({ "ok": true, "items": items })
}
