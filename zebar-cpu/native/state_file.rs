use std::{
    fs::{self, File},
    io::{self, Write},
    path::{Path, PathBuf},
};

use serde::{Serialize, de::DeserializeOwned};

pub fn beside_executable(name: &str) -> Option<PathBuf> {
    std::env::current_exe()
        .ok()
        .map(|exe| exe.with_file_name(name))
}

pub fn read<T: DeserializeOwned>(path: Option<&Path>) -> Option<T> {
    let text = fs::read_to_string(path?).ok()?;
    serde_json::from_str(&text).ok()
}

pub fn write(path: Option<&Path>, value: &impl Serialize) -> io::Result<()> {
    let path = path.ok_or_else(|| io::Error::other("state file path unknown"))?;
    let temp = path.with_extension("json.tmp");
    let mut file = File::create(&temp)?;
    serde_json::to_writer_pretty(&mut file, value)?;
    file.write_all(b"\n")?;
    file.sync_all()?;
    drop(file);
    fs::rename(temp, path)
}
