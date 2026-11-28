//! One volume's index: flat arrays, one slot per file or folder.
//!
//! Entry i's name is `names[name_off[i] .. name_off[i] + name_len[i]]` (UTF-8). Parents are entry numbers; the root is its
//! own parent. `size` is a file's size, or for a folder the total of everything under it; `files` is 1 for a file, or for
//! a folder the number of files under it. Both are kept current as changes come in. Deleted entries stay as tombstones
//! (DELETED) until the next compaction.

use std::collections::HashSet;

use crate::ntfs::{Change, EXTEND_RECORD, REASON_CLOSE, REASON_CREATE, REASON_DELETE, REASON_RENAME_NEW, ROOT_RECORD};
use crate::settings::Rules;

pub const DIR: u8 = 1;
pub const HIDDEN: u8 = 2;
pub const SYSTEM: u8 = 4;
/// Inside a place search pushes down (the settings' demoted names: node_modules, .git, AppData, Windows, ...).
pub const NOISY: u8 = 8;
pub const DELETED: u8 = 16;
/// Inside an excluded folder (or the excluded folder itself): never listed or found; its size still counts above it.
pub const EXCLUDED: u8 = 32;

const NONE: u32 = u32::MAX;

pub struct Index {
    pub letter: char,
    pub names: Vec<u8>,
    pub name_off: Vec<u32>,
    pub name_len: Vec<u16>,
    pub parent: Vec<u32>,
    pub size: Vec<u64>,
    pub files: Vec<u32>,
    pub modified: Vec<u64>,
    pub flags: Vec<u8>,
    /// Entry -> MFT record number.
    pub record: Vec<u32>,
    /// MFT record number -> entry (NONE for none). Rebuilt from `record`, so not saved.
    pub by_record: Vec<u32>,
    pub root: u32,
    /// Name bytes and entries no longer in use; compaction reclaims them.
    pub garbage: usize,
    pub deleted: usize,
}

impl Index {
    pub fn with_capacity(letter: char, entries: usize, name_bytes: usize, root: u32) -> Index {
        Index {
            letter,
            names: Vec::with_capacity(name_bytes),
            name_off: Vec::with_capacity(entries),
            name_len: Vec::with_capacity(entries),
            parent: Vec::with_capacity(entries),
            size: Vec::with_capacity(entries),
            files: Vec::with_capacity(entries),
            modified: Vec::with_capacity(entries),
            flags: Vec::with_capacity(entries),
            record: Vec::with_capacity(entries),
            by_record: Vec::new(),
            root,
            garbage: 0,
            deleted: 0,
        }
    }

    /// Grows a full array by about 3% instead of letting Vec double it: right after a scan every array is exactly full,
    /// and doubling them on the first new file would add over half the index's memory.
    fn make_room(&mut self, name_bytes: usize) {
        let n = self.parent.len();
        if n == self.parent.capacity() {
            let more = n / 32 + 4096;
            self.name_off.reserve_exact(more);
            self.name_len.reserve_exact(more);
            self.parent.reserve_exact(more);
            self.size.reserve_exact(more);
            self.files.reserve_exact(more);
            self.modified.reserve_exact(more);
            self.flags.reserve_exact(more);
            self.record.reserve_exact(more);
        }
        if self.names.len() + name_bytes > self.names.capacity() {
            self.names.reserve_exact(name_bytes.max(self.names.len() / 32 + 65_536));
        }
    }

    pub fn push(&mut self, name: &[u8], parent: u32, size: u64, modified: u64, flags: u8, record: u32) -> u32 {
        self.make_room(name.len());
        let entry = self.parent.len() as u32;
        self.name_off.push(self.names.len() as u32);
        self.name_len.push(name.len() as u16);
        self.names.extend_from_slice(name);
        self.parent.push(parent);
        self.size.push(size);
        self.files.push(if flags & DIR != 0 { 0 } else { 1 });
        self.modified.push(modified);
        self.flags.push(flags);
        self.record.push(record);
        entry
    }

    pub fn set_record_map(&mut self, by_record: Vec<u32>) {
        self.by_record = by_record;
    }

    pub fn rebuild_record_map(&mut self) {
        let max = self.record.iter().copied().max().unwrap_or(0) as usize;
        let mut map = vec![NONE; max + 1];
        for (entry, &r) in self.record.iter().enumerate() {
            if self.flags[entry] & DELETED == 0 { map[r as usize] = entry as u32; }
        }
        self.by_record = map;
    }

    pub fn len(&self) -> usize { self.parent.len() }

    pub fn live(&self) -> usize { self.len() - self.deleted }

    pub fn name(&self, i: usize) -> &str {
        let start = self.name_off[i] as usize;
        let bytes = &self.names[start..start + self.name_len[i] as usize];
        // Names are only ever written from Rust strings.
        unsafe { std::str::from_utf8_unchecked(bytes) }
    }

    pub fn path(&self, i: usize) -> String {
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

    pub fn depth(&self, i: usize) -> u32 {
        let (mut at, mut depth) = (i as u32, 0);
        while at != self.root && depth < 512 {
            at = self.parent[at as usize];
            depth += 1;
        }
        depth
    }

    /// Whether entry `i` is `ancestor` or below it.
    pub fn is_under(&self, i: usize, ancestor: u32) -> bool {
        if ancestor == self.root { return true; }
        let mut at = i as u32;
        for _ in 0..512 {
            if at == ancestor { return true; }
            if at == self.root { return false; }
            at = self.parent[at as usize];
        }
        false
    }

    pub fn entry_for_record(&self, record: u64) -> Option<u32> {
        self.by_record.get(record as usize).copied().filter(|&e| e != NONE)
    }

    /// The live entries directly inside folder `e` (a scan of the parent array: a few ms).
    pub fn children(&self, e: u32) -> Vec<u32> {
        (0..self.len() as u32)
            .filter(|&i| self.parent[i as usize] == e && i != self.root && self.flags[i as usize] & DELETED == 0)
            .collect()
    }

    /// The entry for a path on this drive (`D:\Games\Mods`, any case); the root for `D:\`.
    pub fn resolve(&self, path: &str) -> Option<u32> {
        let p = path.trim().replace('/', "\\");
        let mut chars = p.chars();
        if !chars.next()?.eq_ignore_ascii_case(&self.letter) || chars.next()? != ':' { return None; }
        let mut at = self.root;
        for component in p[2..].split('\\').filter(|c| !c.is_empty()) {
            at = (0..self.len() as u32).find(|&i| {
                self.parent[i as usize] == at && i != self.root && self.flags[i as usize] & DELETED == 0
                    && self.name(i as usize).eq_ignore_ascii_case(component)
            }).or_else(|| (0..self.len() as u32).find(|&i| {
                // Names outside ASCII: compare lower-cased.
                self.parent[i as usize] == at && i != self.root && self.flags[i as usize] & DELETED == 0
                    && self.name(i as usize).to_lowercase() == component.to_lowercase()
            }))?;
        }
        Some(at)
    }

    pub fn memory(&self) -> usize {
        self.names.capacity() + 4 * self.name_off.capacity() + 2 * self.name_len.capacity() + 4 * self.parent.capacity()
            + 8 * self.size.capacity() + 4 * self.files.capacity() + 8 * self.modified.capacity() + self.flags.capacity()
            + 4 * self.record.capacity() + 4 * self.by_record.capacity()
    }

    /// After a scan, a load, or a settings change: the noisy and excluded flags (inherited down the tree), folder totals
    /// and file counts.
    pub fn refresh(&mut self, rules: &Rules) {
        let n = self.len();
        let excluded: HashSet<u32> = rules.excluded_on(self.letter).filter_map(|p| self.resolve(p)).collect();
        // Per entry: 0 unknown, else 1 | NOISY | EXCLUDED. Walk up to the first known ancestor, then assign down the walk.
        let mut state = vec![0u8; n];
        state[self.root as usize] = 1;
        let mut chain = Vec::new();
        for i in 0..n {
            let mut at = i as u32;
            chain.clear();
            while state[at as usize] == 0 && chain.len() < 512 {
                chain.push(at);
                at = self.parent[at as usize];
            }
            let mut inherited = state[at as usize] & (NOISY | EXCLUDED);
            for &e in chain.iter().rev() {
                let e = e as usize;
                if self.flags[e] & DIR != 0 && rules.demotes(self.name(e), self.parent[e] == self.root) { inherited |= NOISY; }
                if excluded.contains(&(e as u32)) { inherited |= EXCLUDED; }
                state[e] = 1 | inherited;
            }
        }
        for i in 0..n {
            self.flags[i] = (self.flags[i] & !(NOISY | EXCLUDED)) | (state[i] & (NOISY | EXCLUDED));
        }

        for i in 0..n {
            if self.flags[i] & DIR != 0 { self.size[i] = 0; self.files[i] = 0; } else { self.files[i] = 1; }
        }
        for i in 0..n {
            if self.flags[i] & (DIR | DELETED) != 0 { continue; }
            let bytes = self.size[i] as i64;
            self.add_to_ancestors(self.parent[i], bytes, 1);
        }
    }

    /// Adds to a folder's and every folder above it's totals.
    fn add_to_ancestors(&mut self, from: u32, bytes: i64, files: i64) {
        if bytes == 0 && files == 0 { return; }
        let mut at = from;
        for _ in 0..512 {
            let i = at as usize;
            self.size[i] = self.size[i].wrapping_add_signed(bytes);
            self.files[i] = (self.files[i] as i64 + files).max(0) as u32;
            if at == self.root { break; }
            at = self.parent[i];
        }
    }

    fn set_name(&mut self, entry: usize, name: &str) {
        if self.name(entry) == name { return; }
        self.garbage += self.name_len[entry] as usize;
        self.make_room(name.len());
        self.name_off[entry] = self.names.len() as u32;
        self.name_len[entry] = name.len() as u16;
        self.names.extend_from_slice(name.as_bytes());
    }

    /// The folder entry for a parent record, or None when it isn't indexed (e.g. `\$Extend\$Deleted`, where NTFS parks
    /// files that are deleted while still open; their names are hex file ids).
    fn parent_entry(&self, record: u64) -> Option<u32> {
        if record as usize == ROOT_RECORD { return Some(self.root); }
        self.entry_for_record(record).filter(|&e| self.flags[e as usize] & DIR != 0)
    }

    /// The noisy and excluded flags an entry gets from its parent, its own name and (folders) the excluded paths.
    fn inherited_flags(&self, entry: usize, rules: &Rules) -> u8 {
        let parent = self.parent[entry];
        let mut flags = self.flags[parent as usize] & (NOISY | EXCLUDED);
        if self.flags[entry] & DIR != 0 {
            if rules.demotes(self.name(entry), parent == self.root) { flags |= NOISY; }
            if flags & EXCLUDED == 0 && rules.excluded_on(self.letter).next().is_some() {
                let path = self.path(entry).to_lowercase();
                if rules.excluded_on(self.letter).any(|p| p == path) { flags |= EXCLUDED; }
            }
        }
        flags
    }

    /// Applies one journal record. Returns whether the index changed.
    pub fn apply(&mut self, change: &Change, rules: &Rules) -> bool {
        let record = change.record;
        if (record as usize) < 16 && record as usize != ROOT_RECORD { return false; }
        if change.parent as u32 == EXTEND_RECORD || ((change.parent as usize) < 16 && change.parent as usize != ROOT_RECORD) { return false; }

        if change.reason & REASON_DELETE != 0 {
            return self.remove_record(record);
        }

        let is_dir = change.attributes & 0x10 != 0;
        let entry = match self.entry_for_record(record) {
            Some(e) => {
                let e = e as usize;
                if change.reason & (REASON_RENAME_NEW | REASON_CREATE) != 0 {
                    // Moved somewhere we don't index (a delete of an open file moves it into \$Extend\$Deleted).
                    let Some(new_parent) = self.parent_entry(change.parent) else { return self.remove_record(record) };
                    if new_parent != self.parent[e] && new_parent as usize != e {
                        let (bytes, files) = (self.size[e] as i64, self.files[e] as i64);
                        self.add_to_ancestors(self.parent[e], -bytes, -files);
                        self.parent[e] = new_parent;
                        self.add_to_ancestors(new_parent, bytes, files);
                    }
                    self.set_name(e, &change.name);
                    let inherited = self.inherited_flags(e, rules);
                    self.flags[e] = (self.flags[e] & !(NOISY | EXCLUDED)) | inherited;
                }
                e
            }
            None => {
                // A file we haven't seen: created, renamed in, or its create came before our scan position.
                if change.reason & (REASON_CREATE | REASON_RENAME_NEW | REASON_CLOSE) == 0 { return false; }
                let Some(parent) = self.parent_entry(change.parent) else { return false };
                let e = self.push(change.name.as_bytes(), parent, 0, 0, if is_dir { DIR } else { 0 }, record as u32) as usize;
                let inherited = self.inherited_flags(e, rules);
                self.flags[e] |= inherited;
                if !is_dir { self.add_to_ancestors(parent, 0, 1); }
                if record as usize >= self.by_record.len() {
                    let needed = record as usize + 1;
                    if needed > self.by_record.capacity() {
                        self.by_record.reserve_exact(needed - self.by_record.len() + self.by_record.len() / 32 + 4096);
                    }
                    self.by_record.resize(needed, NONE);
                }
                self.by_record[record as usize] = e as u32;
                e
            }
        };

        let attributes = change.info.map(|(_, _, a)| a).unwrap_or(change.attributes);
        let mut flags = self.flags[entry] & !(HIDDEN | SYSTEM);
        if attributes & 2 != 0 { flags |= HIDDEN; }
        if attributes & 4 != 0 { flags |= SYSTEM; }
        self.flags[entry] = flags;

        if let Some((size, modified, _)) = change.info {
            self.modified[entry] = modified;
            if flags & DIR == 0 {
                let delta = size as i64 - self.size[entry] as i64;
                self.size[entry] = size;
                self.add_to_ancestors(self.parent[entry], delta, 0);
            }
        }
        true
    }

    fn remove_record(&mut self, record: u64) -> bool {
        let Some(e) = self.entry_for_record(record) else { return false };
        let e = e as usize;
        let (bytes, files) = (self.size[e] as i64, self.files[e] as i64);
        self.add_to_ancestors(self.parent[e], -bytes, -files);
        self.flags[e] |= DELETED;
        self.by_record[record as usize] = NONE;
        self.garbage += self.name_len[e] as usize;
        self.deleted += 1;
        true
    }

    /// Whether compaction would pay off (a tenth of entries or names are dead).
    pub fn needs_compaction(&self) -> bool {
        self.deleted * 10 > self.len() || self.garbage * 10 > self.names.len()
    }

    /// Drops tombstones and dead name bytes, renumbering entries.
    pub fn compact(&mut self) {
        let n = self.len();
        let mut remap = vec![NONE; n];
        let mut count = 0u32;
        for i in 0..n {
            if self.flags[i] & DELETED == 0 { remap[i] = count; count += 1; }
        }
        let mut next = Index::with_capacity(self.letter, count as usize, self.names.len() - self.garbage, remap[self.root as usize]);
        for i in 0..n {
            if remap[i] == NONE { continue; }
            let p = remap[self.parent[i] as usize];
            let parent = if p == NONE { next.root } else { p };
            let start = self.name_off[i] as usize;
            let name = &self.names[start..start + self.name_len[i] as usize];
            let e = next.push(name, parent, self.size[i], self.modified[i], self.flags[i], self.record[i]);
            next.files[e as usize] = self.files[i];
        }
        next.rebuild_record_map();
        *self = next;
    }
}
