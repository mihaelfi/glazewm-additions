//! One volume's index: flat arrays, one slot per file or folder.
//!
//! Entry i's name is `names[name_off[i] .. name_off[i] + name_len[i]]` (UTF-8). Parents are entry numbers; the root is its
//! own parent. `size` is a file's size, or for a folder the total of everything under it, kept current as changes come in.
//! Deleted entries stay as tombstones (DELETED) until the next compaction.

use crate::ntfs::{Change, EXTEND_RECORD, REASON_CLOSE, REASON_CREATE, REASON_DELETE, REASON_RENAME_NEW, ROOT_RECORD};

pub const DIR: u8 = 1;
pub const HIDDEN: u8 = 2;
pub const SYSTEM: u8 = 4;
/// Inside a place search should push down: node_modules, .git, AppData, Windows, caches of package managers, ...
pub const NOISY: u8 = 8;
pub const DELETED: u8 = 16;

const NONE: u32 = u32::MAX;

/// Folder names that make everything below them noisy, anywhere on the drive.
const NOISY_ANYWHERE: &[&str] = &["node_modules", ".git", ".svn", ".hg", "appdata", "__pycache__", "site-packages", ".cargo",
    ".rustup", ".nuget", ".npm", ".gradle", ".m2", ".vs", ".venv", "$recycle.bin", "system volume information"];
/// Folder names that are noisy only directly under the drive root.
const NOISY_AT_ROOT: &[&str] = &["windows", "programdata", "recovery", "$windows.~ws", "$windows.~bt", "$winreagent", "msocache"];

pub struct Index {
    pub letter: char,
    pub names: Vec<u8>,
    pub name_off: Vec<u32>,
    pub name_len: Vec<u16>,
    pub parent: Vec<u32>,
    pub size: Vec<u64>,
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

    pub fn entry_for_record(&self, record: u64) -> Option<u32> {
        self.by_record.get(record as usize).copied().filter(|&e| e != NONE)
    }

    pub fn memory(&self) -> usize {
        self.names.capacity() + 4 * self.name_off.capacity() + 2 * self.name_len.capacity() + 4 * self.parent.capacity()
            + 8 * self.size.capacity() + 8 * self.modified.capacity() + self.flags.capacity() + 4 * self.record.capacity()
            + 4 * self.by_record.capacity()
    }

    fn own_name_noisy(&self, i: usize) -> bool {
        let name = self.name(i);
        let at_root = self.parent[i] == self.root;
        NOISY_ANYWHERE.iter().any(|n| name.eq_ignore_ascii_case(n))
            || (at_root && NOISY_AT_ROOT.iter().any(|n| name.eq_ignore_ascii_case(n)))
    }

    /// After a scan or load: noisy flags (inherited down the tree) and folder totals.
    pub fn finish(&mut self) {
        // Noisy: 0 unknown, 1 noisy, 2 clean. Walk up to the first known ancestor, then assign down the walked path.
        let n = self.len();
        let mut state = vec![0u8; n];
        state[self.root as usize] = 2;
        let mut chain = Vec::new();
        for i in 0..n {
            let mut at = i as u32;
            chain.clear();
            while state[at as usize] == 0 && chain.len() < 512 {
                chain.push(at);
                at = self.parent[at as usize];
            }
            let mut noisy = state[at as usize] == 1;
            for &e in chain.iter().rev() {
                noisy = noisy || self.own_name_noisy(e as usize);
                state[e as usize] = if noisy { 1 } else { 2 };
            }
        }
        for i in 0..n {
            if state[i] == 1 { self.flags[i] |= NOISY; } else { self.flags[i] &= !NOISY; }
        }

        for i in 0..n {
            if self.flags[i] & DIR != 0 { self.size[i] = 0; }
        }
        for i in 0..n {
            if self.flags[i] & (DIR | DELETED) != 0 || self.size[i] == 0 { continue; }
            let bytes = self.size[i] as i64;
            self.add_to_ancestors(self.parent[i], bytes);
        }
    }

    /// Adds `delta` to a folder and every folder above it.
    fn add_to_ancestors(&mut self, from: u32, delta: i64) {
        if delta == 0 { return; }
        let mut at = from;
        for _ in 0..512 {
            let slot = &mut self.size[at as usize];
            *slot = slot.wrapping_add_signed(delta);
            if at == self.root { break; }
            at = self.parent[at as usize];
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

    fn parent_entry(&self, record: u64) -> u32 {
        self.entry_for_record(record).filter(|&e| self.flags[e as usize] & DIR != 0).unwrap_or(self.root)
    }

    fn noisy_for(&self, parent: u32, name: &str) -> bool {
        self.flags[parent as usize] & NOISY != 0
            || NOISY_ANYWHERE.iter().any(|n| name.eq_ignore_ascii_case(n))
            || (parent == self.root && NOISY_AT_ROOT.iter().any(|n| name.eq_ignore_ascii_case(n)))
    }

    /// Applies one journal record. Returns whether the index changed.
    pub fn apply(&mut self, change: &Change) -> bool {
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
                    let new_parent = self.parent_entry(change.parent);
                    if new_parent != self.parent[e] && new_parent as usize != e {
                        let total = self.size[e] as i64;
                        self.add_to_ancestors(self.parent[e], -total);
                        self.parent[e] = new_parent;
                        self.add_to_ancestors(new_parent, total);
                    }
                    self.set_name(e, &change.name);
                    let noisy = self.noisy_for(self.parent[e], &change.name);
                    if noisy { self.flags[e] |= NOISY; } else { self.flags[e] &= !NOISY; }
                }
                e
            }
            None => {
                // A file we haven't seen: created, renamed in, or its create came before our scan position.
                if change.reason & (REASON_CREATE | REASON_RENAME_NEW | REASON_CLOSE) == 0 { return false; }
                let parent = self.parent_entry(change.parent);
                let mut flags = if is_dir { DIR } else { 0 };
                if self.noisy_for(parent, &change.name) { flags |= NOISY; }
                let e = self.push(change.name.as_bytes(), parent, 0, 0, flags, record as u32);
                if record as usize >= self.by_record.len() {
                    let needed = record as usize + 1;
                    if needed > self.by_record.capacity() {
                        self.by_record.reserve_exact(needed - self.by_record.len() + self.by_record.len() / 32 + 4096);
                    }
                    self.by_record.resize(needed, NONE);
                }
                self.by_record[record as usize] = e;
                e as usize
            }
        };

        let attributes = change.info.map(|(_, _, a)| a).unwrap_or(change.attributes);
        let mut flags = self.flags[entry] & !(HIDDEN | SYSTEM | DIR);
        if attributes & 2 != 0 { flags |= HIDDEN; }
        if attributes & 4 != 0 { flags |= SYSTEM; }
        if attributes & 0x10 != 0 { flags |= DIR; }
        self.flags[entry] = flags;

        if let Some((size, modified, _)) = change.info {
            self.modified[entry] = modified;
            if flags & DIR == 0 {
                let delta = size as i64 - self.size[entry] as i64;
                self.size[entry] = size;
                self.add_to_ancestors(self.parent[entry], delta);
            }
        }
        true
    }

    fn remove_record(&mut self, record: u64) -> bool {
        let Some(e) = self.entry_for_record(record) else { return false };
        let e = e as usize;
        let total = self.size[e] as i64;
        self.add_to_ancestors(self.parent[e], -total);
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
            next.push(name, parent, self.size[i], self.modified[i], self.flags[i], self.record[i]);
        }
        next.rebuild_record_map();
        *self = next;
    }
}
