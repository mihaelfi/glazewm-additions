//! The named pipe `\\.\pipe\GlazeIndex`. One request line per connection, one JSON reply, then the server closes it.
//!
//!   search<TAB>limit<TAB>text   ->  {"query":..,"matches":..,"ms":..,"ready":..,"results":[{"path","name","folder","size","modified","score","highlight":[start,len]}]}
//!   status                      ->  {"volumes":[{"drive","state","entries","memory"}],"skipped":[..],"private_memory":..,"min_query":..}
//!
//! Only SYSTEM, administrators and interactive (logged-on, local) users may connect; remote clients are refused.

use std::ptr::null_mut;
use std::sync::Arc;

use crate::engine::Engine;
use crate::win::{self, OwnedHandle, Sddl};

pub const NAME: &str = r"\\.\pipe\GlazeIndex";
const SDDL: &str = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)";

pub fn serve(engine: Arc<Engine>) {
    let Some(descriptor) = Sddl::new(SDDL) else {
        crate::log!("pipe: can't build its security descriptor (error {})", win::last_error());
        return;
    };
    let name = win::wide(NAME);
    loop {
        let attributes = descriptor.attributes();
        let handle = unsafe {
            win::CreateNamedPipeW(name.as_ptr(), win::PIPE_ACCESS_DUPLEX, win::PIPE_REJECT_REMOTE_CLIENTS,
                win::PIPE_UNLIMITED_INSTANCES, 64 * 1024, 4 * 1024, 0, &attributes)
        };
        if handle == win::INVALID_HANDLE {
            crate::log!("pipe: CreateNamedPipe failed (error {}); retrying in 5 s", win::last_error());
            std::thread::sleep(std::time::Duration::from_secs(5));
            continue;
        }
        let pipe = OwnedHandle(handle);
        let connected = unsafe { win::ConnectNamedPipe(pipe.0, null_mut()) } != 0 || win::last_error() == win::ERROR_PIPE_CONNECTED;
        if !connected { continue; }
        let engine = engine.clone();
        let _ = std::thread::Builder::new().name("pipe client".into()).spawn(move || answer(&engine, pipe));
    }
}

fn answer(engine: &Engine, pipe: OwnedHandle) {
    let Some(request) = read_line(&pipe) else { return };
    let reply = handle(engine, &request);
    let bytes = reply.as_bytes();
    let mut written_total = 0usize;
    while written_total < bytes.len() {
        let mut written = 0u32;
        let chunk = &bytes[written_total..];
        let ok = unsafe { win::WriteFile(pipe.0, chunk.as_ptr(), chunk.len().min(1 << 20) as u32, &mut written, null_mut()) };
        if ok == 0 || written == 0 { break; }
        written_total += written as usize;
    }
    // Wait until the client has read everything, then close (dropping `pipe`). Closing rather than
    // DisconnectNamedPipe gives the client a normal end of data (broken pipe) instead of "not connected".
    unsafe { win::FlushFileBuffers(pipe.0) };
}

fn read_line(pipe: &OwnedHandle) -> Option<String> {
    let mut data = Vec::new();
    let mut buffer = [0u8; 1024];
    while data.len() < 8192 {
        let mut read = 0u32;
        let ok = unsafe { win::ReadFile(pipe.0, buffer.as_mut_ptr(), buffer.len() as u32, &mut read, null_mut()) };
        if ok == 0 || read == 0 { break; }
        data.extend_from_slice(&buffer[..read as usize]);
        if data.contains(&b'\n') { break; }
    }
    let end = data.iter().position(|&b| b == b'\n').unwrap_or(data.len());
    let line = String::from_utf8_lossy(&data[..end]).trim_end_matches('\r').to_owned();
    (!line.is_empty()).then_some(line)
}

fn handle(engine: &Engine, request: &str) -> String {
    let mut parts = request.splitn(3, '\t');
    match parts.next() {
        Some("search") => {
            let limit = parts.next().and_then(|l| l.parse().ok()).unwrap_or(20);
            engine.search_json(parts.next().unwrap_or(""), limit)
        }
        Some("status") => engine.status_json(),
        Some("ping") => "{\"ok\":true}".into(),
        _ => "{\"error\":\"unknown request\"}".into(),
    }
}
