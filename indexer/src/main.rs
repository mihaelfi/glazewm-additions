//! GlazeIndex: an Everything-style index of file and folder names and sizes on the internal NTFS drives, kept current
//! from the NTFS change journal, served over `\\.\pipe\GlazeIndex`. See docs/indexer.md.
//!
//!   GlazeIndex --install          copy to Program Files, register and start the service (admin)
//!   GlazeIndex --uninstall        stop and remove the service and its data (admin)
//!   GlazeIndex --console          run in this terminal instead of as a service (admin; Enter stops it)
//!   GlazeIndex --status           ask the running service for its state (any user)
//!   GlazeIndex --search <text>    ask the running service to search (any user)
//!   GlazeIndex --service          what the service manager runs

mod engine;
mod index;
mod json;
mod log;
mod ntfs;
mod pipe;
mod search;
mod service;
mod snapshot;
mod volumes;
mod win;

use std::io::{Read, Write};

fn ask(request: &str) -> Result<String, String> {
    let mut pipe = std::fs::OpenOptions::new().read(true).write(true).open(pipe::NAME)
        .map_err(|e| format!("can't reach the service on {}: {e}", pipe::NAME))?;
    pipe.write_all(format!("{request}\n").as_bytes()).map_err(|e| e.to_string())?;
    // Read to the end; older servers end the connection with "not connected" (233) instead of a broken pipe.
    let mut reply = Vec::new();
    let mut buffer = [0u8; 64 * 1024];
    loop {
        match pipe.read(&mut buffer) {
            Ok(0) => break,
            Ok(n) => reply.extend_from_slice(&buffer[..n]),
            Err(e) if e.raw_os_error() == Some(233) => break,
            Err(e) => return Err(e.to_string()),
        }
    }
    Ok(String::from_utf8_lossy(&reply).into_owned())
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let result = match args.first().map(String::as_str) {
        Some("--service") => service::dispatch(),
        Some("--install") => service::install(),
        Some("--uninstall") => service::uninstall(),
        Some("--console") => {
            let engine = service::run_engine(true);
            println!("running; press Enter to save and stop");
            let _ = std::io::stdin().read_line(&mut String::new());
            engine.shutdown();
            Ok(())
        }
        Some("--status") => ask("status").map(|r| println!("{r}")),
        Some("--search") => ask(&format!("search\t20\t{}", args[1..].join(" "))).map(|r| println!("{r}")),
        _ => {
            println!("GlazeIndex --install | --uninstall | --console | --status | --search <text>");
            Ok(())
        }
    };
    if let Err(e) = result {
        eprintln!("GlazeIndex: {e}");
        std::process::exit(1);
    }
}
