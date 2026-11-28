//! GlazeIndex: an Everything-style index of file and folder names and sizes on the drives you turn on, kept current from
//! the NTFS change journal, served over `\\.\pipe\GlazeIndex`. See docs/indexer.md.
//!
//! Admin (from the build folder):
//!   GlazeIndex --install            copy to Program Files, register and start (or update) the service
//!   GlazeIndex --uninstall          stop and remove the service and its data
//!   GlazeIndex --console            run in this terminal instead of as a service (Enter stops it)
//! Any user (ask the running service; output is JSON):
//!   --status | --settings | --enable D: | --disable D: | --exclude <folder> | --unexclude <folder> | --rescan D:
//!   --search <text> | --children <folder> | --largest [folder] | --types [folder] | --recent [folder]
//!   --request '<json>'              any request, e.g. {"op":"children","path":"D:\\","sort":"name"}
//! The service manager runs it with --service.

mod api;
mod engine;
mod index;
mod log;
mod ntfs;
mod pipe;
mod query;
mod search;
mod service;
mod settings;
mod snapshot;
mod volumes;
mod win;

use std::io::{Read, Write};

use serde_json::{json, Value};

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

/// Sends a JSON request and prints the reply indented.
fn request(value: Value) -> Result<(), String> {
    let reply = ask(&value.to_string())?;
    match serde_json::from_str::<Value>(&reply) {
        Ok(v) => {
            println!("{}", serde_json::to_string_pretty(&v).unwrap_or(reply));
            if v["ok"] == json!(false) { return Err("the service refused the request".into()); }
            Ok(())
        }
        Err(_) => { println!("{reply}"); Ok(()) }
    }
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let arg = |i: usize| args.get(i).cloned();
    let rest = || args[1..].join(" ");
    let path_op = |op: &str| {
        let mut r = json!({ "op": op });
        if let Some(p) = arg(1) { r["path"] = json!(p); }
        request(r)
    };
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
        Some("--status") => request(json!({ "op": "status" })),
        Some("--settings") => request(json!({ "op": "settings" })),
        Some(flag @ ("--enable" | "--disable")) => match arg(1) {
            Some(d) => request(json!({ "op": "drive", "drive": d, "enabled": flag == "--enable" })),
            None => Err(format!("{flag} needs a drive, e.g. {flag} D:")),
        },
        Some(flag @ ("--exclude" | "--unexclude")) => match args.get(1) {
            Some(_) => request(json!({ "op": "exclude", "path": rest(), "excluded": flag == "--exclude" })),
            None => Err(format!("{flag} needs a folder")),
        },
        Some("--rescan") => match arg(1) {
            Some(d) => request(json!({ "op": "rescan", "drive": d })),
            None => Err("--rescan needs a drive".into()),
        },
        Some("--search") => request(json!({ "op": "search", "query": rest(), "limit": 20 })),
        Some("--children") => match args.get(1) {
            Some(_) => request(json!({ "op": "children", "path": rest(), "limit": 50 })),
            None => Err("--children needs a folder".into()),
        },
        Some("--largest") => path_op("largest"),
        Some("--types") => path_op("types"),
        Some("--recent") => path_op("recent"),
        Some("--request") => match serde_json::from_str::<Value>(&rest()) {
            Ok(v) => request(v),
            Err(e) => Err(format!("not JSON: {e}")),
        },
        _ => {
            println!("GlazeIndex --install | --uninstall | --console (admin)");
            println!("  --status | --settings | --enable D: | --disable D: | --exclude <folder> | --unexclude <folder> | --rescan D:");
            println!("  --search <text> | --children <folder> | --largest [folder] | --types [folder] | --recent [folder] | --request '<json>'");
            Ok(())
        }
    };
    if let Err(e) = result {
        eprintln!("GlazeIndex: {e}");
        std::process::exit(1);
    }
}
