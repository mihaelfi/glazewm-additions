//! Saves an index with the journal position it's current to, so a restart replays only what changed since.
//! Files are `vol-<serial>.glix` in the data folder (keyed by volume serial, so a changed drive letter doesn't matter).

use std::io::{Read, Write};
use std::path::{Path, PathBuf};

use crate::index::Index;

const MAGIC: &[u8; 4] = b"GLIX";
const VERSION: u32 = 1;

pub struct Saved {
    pub index: Index,
    pub journal_id: u64,
    pub next_usn: i64,
}

pub fn path_for(folder: &Path, serial: u32) -> PathBuf {
    folder.join(format!("vol-{serial:08X}.glix"))
}

fn bytes_of<T: Copy>(v: &[T]) -> &[u8] {
    unsafe { std::slice::from_raw_parts(v.as_ptr() as *const u8, std::mem::size_of_val(v)) }
}

fn vec_of<T: Copy + Default>(bytes: &[u8]) -> Option<Vec<T>> {
    let size = size_of::<T>();
    if bytes.len() % size != 0 { return None; }
    let mut v = vec![T::default(); bytes.len() / size];
    unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), v.as_mut_ptr() as *mut u8, bytes.len()) };
    Some(v)
}

pub fn save(folder: &Path, serial: u32, index: &Index, journal_id: u64, next_usn: i64) -> std::io::Result<u64> {
    let path = path_for(folder, serial);
    let temp = path.with_extension("tmp");
    {
        let mut file = std::io::BufWriter::with_capacity(1 << 20, std::fs::File::create(&temp)?);
        file.write_all(MAGIC)?;
        file.write_all(&VERSION.to_le_bytes())?;
        file.write_all(&(index.letter as u32).to_le_bytes())?;
        file.write_all(&serial.to_le_bytes())?;
        file.write_all(&journal_id.to_le_bytes())?;
        file.write_all(&next_usn.to_le_bytes())?;
        file.write_all(&index.root.to_le_bytes())?;
        file.write_all(&(index.garbage as u64).to_le_bytes())?;
        file.write_all(&(index.deleted as u64).to_le_bytes())?;
        for part in [bytes_of(&index.names), bytes_of(&index.name_off), bytes_of(&index.name_len), bytes_of(&index.parent),
            bytes_of(&index.size), bytes_of(&index.modified), bytes_of(&index.flags), bytes_of(&index.record)] {
            file.write_all(&(part.len() as u64).to_le_bytes())?;
            file.write_all(part)?;
        }
        file.into_inner().map_err(|e| e.into_error())?.sync_all()?;
    }
    std::fs::rename(&temp, &path)?;
    Ok(std::fs::metadata(&path)?.len())
}

pub fn load(folder: &Path, serial: u32, letter: char) -> Option<Saved> {
    let mut data = Vec::new();
    std::fs::File::open(path_for(folder, serial)).ok()?.read_to_end(&mut data).ok()?;
    let mut at = 0usize;
    let mut take = |n: usize| -> Option<&[u8]> {
        let slice = data.get(at..at + n)?;
        at += n;
        Some(slice)
    };
    if take(4)? != MAGIC { return None; }
    let u32_of = |b: &[u8]| u32::from_le_bytes(b.try_into().unwrap());
    let u64_of = |b: &[u8]| u64::from_le_bytes(b.try_into().unwrap());
    if u32_of(take(4)?) != VERSION { return None; }
    let _letter = u32_of(take(4)?);
    if u32_of(take(4)?) != serial { return None; }
    let journal_id = u64_of(take(8)?);
    let next_usn = u64_of(take(8)?) as i64;
    let root = u32_of(take(4)?);
    let garbage = u64_of(take(8)?) as usize;
    let deleted = u64_of(take(8)?) as usize;
    let mut part = || -> Option<&[u8]> { let n = u64_of(take(8)?) as usize; take(n) };
    let names: Vec<u8> = part()?.to_vec();
    let name_off: Vec<u32> = vec_of(part()?)?;
    let name_len: Vec<u16> = vec_of(part()?)?;
    let parent: Vec<u32> = vec_of(part()?)?;
    let size: Vec<u64> = vec_of(part()?)?;
    let modified: Vec<u64> = vec_of(part()?)?;
    let flags: Vec<u8> = part()?.to_vec();
    let record: Vec<u32> = vec_of(part()?)?;
    let n = parent.len();
    if [name_off.len(), name_len.len(), size.len(), modified.len(), flags.len(), record.len()].iter().any(|&l| l != n) { return None; }
    if root as usize >= n || parent.iter().any(|&p| p as usize >= n) { return None; }
    if name_off.iter().zip(&name_len).any(|(&o, &l)| o as usize + l as usize > names.len()) { return None; }
    if std::str::from_utf8(&names).is_err() { return None; }
    drop(data);
    let mut index = Index { letter, names, name_off, name_len, parent, size, modified, flags, record, by_record: Vec::new(), root, garbage, deleted };
    index.rebuild_record_map();
    Some(Saved { index, journal_id, next_usn })
}
