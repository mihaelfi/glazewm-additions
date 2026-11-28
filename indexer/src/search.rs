//! Name search over every volume: a parallel scan, case-insensitive, substring and word-start matches only.
//!
//! Every query word must appear in the name or in a folder above it, and at least one in the name itself, so
//! "dragon tooltips" finds `D:\Dragon Age Mods\Detailed Tooltips-248`. Ranking (from the probe's findings): exact name,
//! then name start, word start, anywhere; folders first; noisy places (node_modules, AppData, Windows, ...) and
//! hidden/system entries pushed down; shorter names and shallower paths first.
//!
//! Filters can come as fields or inside the query text: `*.zip` / `ext:zip,rar`, `file:` / `folder:`, `in:D:\Mods`
//! (`in:"D:\My Mods"` with spaces), and `sort:size` / `sort:modified`.

use std::sync::atomic::{AtomicUsize, Ordering};

use crate::index::{Index, DELETED, DIR, EXCLUDED, HIDDEN, NOISY, SYSTEM};

#[derive(Clone, Copy)]
pub struct Hit {
    /// What the results are sorted by (the score, a size or a time).
    pub key: i64,
    pub score: i32,
    pub volume: usize,
    pub entry: u32,
    /// The best-matching word's position in the name, in UTF-16 units, for highlighting.
    pub highlight: (u32, u32),
}

pub struct Outcome {
    pub hits: Vec<Hit>,
    pub matches: usize,
}

/// Queries shorter than this (letters, spaces not counted) return nothing unless a filter narrows them: at 3.8 M entries
/// they're all noise.
pub const MIN_QUERY: usize = 3;
const MAX_TERMS: usize = 8;

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Kind { Any, File, Folder }

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Sort { Score, Size, Modified }

pub struct Request {
    pub terms: Vec<String>,
    pub kind: Kind,
    /// Lower-case extensions without the dot.
    pub exts: Vec<String>,
    /// A folder to search under (a path); resolved by the engine.
    pub scope: Option<String>,
    pub sort: Sort,
    pub hidden: bool,
}

/// Splits query text into words and the filters written into it.
pub fn parse(text: &str, hidden: bool) -> Request {
    let mut request = Request { terms: Vec::new(), kind: Kind::Any, exts: Vec::new(), scope: None, sort: Sort::Score, hidden };
    for token in tokens(text) {
        let lower = token.to_lowercase();
        if let Some(list) = lower.strip_prefix("ext:") {
            request.exts.extend(list.split(',').map(|e| e.trim_start_matches('.').to_string()).filter(|e| !e.is_empty()));
        } else if let Some(ext) = lower.strip_prefix("*.") {
            if !ext.is_empty() && !ext.contains(['*', '?']) { request.exts.push(ext.to_string()); }
        } else if lower == "file:" || lower == "kind:file" {
            request.kind = Kind::File;
        } else if lower == "folder:" || lower == "kind:folder" {
            request.kind = Kind::Folder;
        } else if lower.starts_with("in:") && token.len() > 3 {
            request.scope = Some(token[3..].trim_matches('"').to_string());
        } else if let Some(sort) = lower.strip_prefix("sort:") {
            request.sort = match sort { "size" => Sort::Size, "modified" | "date" => Sort::Modified, _ => Sort::Score };
        } else if request.terms.len() < MAX_TERMS {
            request.terms.push(lower);
        }
    }
    // The longest word first: it filters best and decides the highlight.
    request.terms.sort_by_key(|t| std::cmp::Reverse(t.len()));
    request
}

/// Whitespace-separated tokens; `"…"` keeps spaces (only `in:"…"` needs it).
fn tokens(text: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut current = String::new();
    let mut quoted = false;
    for c in text.chars() {
        match c {
            '"' => { quoted = !quoted; current.push(c); }
            c if c.is_whitespace() && !quoted => { if !current.is_empty() { out.push(std::mem::take(&mut current)); } }
            c => current.push(c),
        }
    }
    if !current.is_empty() { out.push(current); }
    out
}

pub fn lower_into(buffer: &mut String, name: &str) {
    buffer.clear();
    if name.is_ascii() {
        buffer.push_str(name);
        buffer.make_ascii_lowercase();
    } else {
        for c in name.chars() { buffer.extend(c.to_lowercase()); }
    }
}

fn ancestors_lower(index: &Index, entry: usize, out: &mut String) {
    out.clear();
    let mut at = index.parent[entry];
    for _ in 0..512 {
        if at == index.root { break; }
        out.push('\\');
        for c in index.name(at as usize).chars() { out.extend(c.to_lowercase()); }
        at = index.parent[at as usize];
    }
}

fn utf16_len(s: &str) -> u32 { s.encode_utf16().count() as u32 }

/// A file name's extension, lower-cased into `buffer` (empty when there is none).
pub fn extension_into<'a>(name: &str, buffer: &'a mut [u8; 16]) -> &'a [u8] {
    let Some(dot) = name.rfind('.') else { return &buffer[..0] };
    let ext = &name.as_bytes()[dot + 1..];
    if ext.is_empty() || ext.len() > buffer.len() || dot == 0 { return &buffer[..0]; }
    for (k, b) in ext.iter().enumerate() { buffer[k] = b.to_ascii_lowercase(); }
    &buffer[..ext.len()]
}

/// Runs `body(state, volume, index, entry)` over every entry of every index on all CPU threads; returns each thread's state.
pub fn parallel<T: Send>(indexes: &[&Index], init: impl Fn() -> T + Sync, body: impl Fn(&mut T, usize, &Index, usize) + Sync) -> Vec<T> {
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
    std::thread::scope(|scope| {
        let workers: Vec<_> = (0..threads.min(tasks.len()).max(1)).map(|_| scope.spawn(|| {
            let mut state = init();
            loop {
                let t = next.fetch_add(1, Ordering::Relaxed);
                let Some(&(v, start, end)) = tasks.get(t) else { break };
                for i in start..end { body(&mut state, v, indexes[v], i); }
            }
            state
        })).collect();
        workers.into_iter().map(|w| w.join().unwrap()).collect()
    })
}

/// Keeps the `limit` hits with the highest keys, sorted.
pub fn keep_top(best: &mut Vec<Hit>, hit: Hit, limit: usize) {
    if best.len() < limit || hit.key > best[best.len() - 1].key {
        let pos = best.partition_point(|h| h.key >= hit.key);
        best.insert(pos, hit);
        best.truncate(limit);
    }
}

pub fn merge(results: Vec<(Vec<Hit>, usize)>, limit: usize) -> Outcome {
    let mut hits: Vec<Hit> = results.iter().flat_map(|(b, _)| b.iter().copied()).collect();
    hits.sort_by(|a, b| b.key.cmp(&a.key).then(a.volume.cmp(&b.volume)).then(a.entry.cmp(&b.entry)));
    hits.truncate(limit);
    Outcome { hits, matches: results.iter().map(|(_, m)| m).sum() }
}

/// `scope`: only under this (volume, entry).
pub fn search(indexes: &[&Index], request: &Request, scope: Option<(usize, u32)>, limit: usize) -> Outcome {
    let terms = &request.terms;
    let letters: usize = terms.iter().map(|t| t.chars().count()).sum();
    let filtered = !request.exts.is_empty() || scope.is_some();
    if limit == 0 || (letters < MIN_QUERY && !filtered) || (terms.is_empty() && !filtered) {
        return Outcome { hits: Vec::new(), matches: 0 };
    }
    // Without words there's no relevance; list the biggest first.
    let sort = if terms.is_empty() && request.sort == Sort::Score { Sort::Size } else { request.sort };

    let results = parallel(indexes, || (Vec::<Hit>::with_capacity(limit + 1), 0usize, String::new(), String::new()),
        |(best, matches, name, path), v, index, i| {
            let flags = index.flags[i];
            if flags & (DELETED | EXCLUDED) != 0 || i as u32 == index.root { return; }
            if !request.hidden && flags & (HIDDEN | SYSTEM) != 0 { return; }
            let is_dir = flags & DIR != 0;
            match request.kind {
                Kind::File if is_dir => return,
                Kind::Folder if !is_dir => return,
                _ => {}
            }
            if !request.exts.is_empty() {
                if is_dir { return; }
                let mut buffer = [0u8; 16];
                let ext = extension_into(index.name(i), &mut buffer);
                if !request.exts.iter().any(|e| e.as_bytes() == ext) { return; }
            }
            if let Some((sv, se)) = scope {
                if sv != v || !index.is_under(i, se) { return; }
            }

            let mut score = 0i32;
            let mut highlight = (0, 0);
            if !terms.is_empty() {
                lower_into(name, index.name(i));
                let mut first: Option<(usize, usize)> = None;
                let mut missing = 0u32;
                for (k, term) in terms.iter().enumerate() {
                    match name.find(term.as_str()) {
                        Some(at) => if first.is_none() { first = Some((k, at)); },
                        None => missing |= 1 << k,
                    }
                }
                let Some((k, at)) = first else { return };
                if missing != 0 {
                    ancestors_lower(index, i, path);
                    if terms.iter().enumerate().any(|(j, term)| missing & (1 << j) != 0 && !path.contains(term.as_str())) { return; }
                }
                let term = &terms[k];
                let bytes = name.as_bytes();
                score = if name.len() == term.len() { 1000 }
                    else if at == 0 { 800 }
                    else if !bytes[at - 1].is_ascii_alphanumeric() { 600 }
                    else { 400 };
                if missing == 0 && terms.len() > 1 { score += 100; }
                score -= (name.len().min(120) / 2) as i32;
                highlight = (utf16_len(&name[..at]), utf16_len(term));
            }
            *matches += 1;
            if is_dir { score += 100; }
            if flags & NOISY != 0 { score -= 350; }
            if flags & (HIDDEN | SYSTEM) != 0 { score -= 200; }

            let key = match sort {
                Sort::Score => {
                    if best.len() == limit && (score as i64) <= best[limit - 1].key { return; }
                    score -= 4 * index.depth(i).min(30) as i32;
                    score as i64
                }
                Sort::Size => index.size[i] as i64,
                Sort::Modified => index.modified[i] as i64,
            };
            keep_top(best, Hit { key, score, volume: v, entry: i as u32, highlight }, limit);
        });
    merge(results.into_iter().map(|(b, m, _, _)| (b, m)).collect(), limit)
}
