//! Name search over every volume: a parallel scan, case-insensitive, substring and word-start matches only.
//!
//! Every query word must appear in the name or in a folder above it, and at least one in the name itself, so
//! "dragon tooltips" finds `D:\Dragon Age Mods\Detailed Tooltips-248`. Ranking (from the probe's findings): exact name,
//! then name start, word start, anywhere; folders first; noisy places (node_modules, AppData, Windows, ...) and
//! hidden/system entries pushed down; shorter names and shallower paths first.

use std::sync::atomic::{AtomicUsize, Ordering};

use crate::index::{Index, DELETED, DIR, HIDDEN, NOISY, SYSTEM};

#[derive(Clone, Copy)]
pub struct Hit {
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

/// Queries shorter than this (letters, spaces not counted) return nothing: at 3.8 M entries they're all noise.
pub const MIN_QUERY: usize = 3;
const MAX_TERMS: usize = 8;

pub fn terms_of(query: &str) -> Vec<String> {
    let lower = query.to_lowercase();
    let mut terms: Vec<String> = lower.split_whitespace().take(MAX_TERMS).map(str::to_owned).collect();
    // The longest word first: it filters best and decides the highlight.
    terms.sort_by_key(|t| std::cmp::Reverse(t.len()));
    terms
}

fn lower_into(buffer: &mut String, name: &str) {
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

pub fn search(indexes: &[&Index], query: &str, limit: usize) -> Outcome {
    let terms = terms_of(query);
    let letters: usize = terms.iter().map(|t| t.chars().count()).sum();
    if terms.is_empty() || letters < MIN_QUERY || limit == 0 { return Outcome { hits: Vec::new(), matches: 0 }; }

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
        let workers: Vec<_> = (0..threads.min(tasks.len().max(1))).map(|_| scope.spawn(|| {
            let mut best: Vec<Hit> = Vec::with_capacity(limit + 1);
            let mut matches = 0usize;
            let mut name = String::new();
            let mut path = String::new();
            let mut in_name = [false; MAX_TERMS];
            loop {
                let t = next.fetch_add(1, Ordering::Relaxed);
                let Some(&(v, start, end)) = tasks.get(t) else { break };
                let index = indexes[v];
                for i in start..end {
                    let flags = index.flags[i];
                    if flags & DELETED != 0 || i as u32 == index.root { continue; }
                    lower_into(&mut name, index.name(i));
                    let mut first: Option<(usize, usize)> = None;
                    let mut all = true;
                    for (k, term) in terms.iter().enumerate() {
                        match name.find(term.as_str()) {
                            Some(at) => { in_name[k] = true; if first.is_none() { first = Some((k, at)); } }
                            None => { in_name[k] = false; all = false; }
                        }
                    }
                    let Some((k, at)) = first else { continue };
                    if !all {
                        ancestors_lower(index, i, &mut path);
                        if terms.iter().enumerate().any(|(j, term)| !in_name[j] && !path.contains(term.as_str())) { continue; }
                    }
                    matches += 1;

                    let term = &terms[k];
                    let bytes = name.as_bytes();
                    let mut score = if name.len() == term.len() { 1000 }
                        else if at == 0 { 800 }
                        else if !bytes[at - 1].is_ascii_alphanumeric() { 600 }
                        else { 400 };
                    if all && terms.len() > 1 { score += 100; }
                    if flags & DIR != 0 { score += 100; }
                    if flags & NOISY != 0 { score -= 350; }
                    if flags & (HIDDEN | SYSTEM) != 0 { score -= 200; }
                    score -= (name.len().min(120) / 2) as i32;
                    if best.len() == limit && score + 4 <= best[limit - 1].score { continue; }
                    score -= 4 * index.depth(i).min(30) as i32;
                    if best.len() < limit || score > best[best.len() - 1].score {
                        let highlight = (utf16_len(&name[..at]), utf16_len(term));
                        let pos = best.partition_point(|h| h.score >= score);
                        best.insert(pos, Hit { score, volume: v, entry: i as u32, highlight });
                        best.truncate(limit);
                    }
                }
            }
            (best, matches)
        })).collect();
        workers.into_iter().map(|w| w.join().unwrap()).collect()
    });
    let mut hits: Vec<Hit> = results.iter().flat_map(|(b, _)| b.iter().copied()).collect();
    hits.sort_by(|a, b| b.score.cmp(&a.score).then(a.entry.cmp(&b.entry)));
    hits.truncate(limit);
    Outcome { hits, matches: results.iter().map(|(_, m)| m).sum() }
}
