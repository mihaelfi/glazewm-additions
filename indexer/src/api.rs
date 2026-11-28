//! The pipe's requests: one JSON object per line in, one JSON object out (`"ok": true|false`). See docs/indexer.md.
//!
//! The older text form `search<TAB>limit<TAB>text` (GlazeLauncher's) and `status` still work.

use std::sync::Arc;
use std::time::Instant;

use serde_json::{json, Value};

use crate::engine::Engine;
use crate::index::Index;
use crate::query::{self, item};
use crate::search::{self, Kind, Sort};
use crate::settings::{normalize_path, Settings};
use crate::volumes;

pub fn handle(engine: &Arc<Engine>, line: &str, client: &dyn Fn() -> String) -> String {
    let line = line.trim();
    let reply = if let Some(rest) = line.strip_prefix("search\t") {
        let mut parts = rest.splitn(2, '\t');
        let limit = parts.next().and_then(|l| l.parse().ok()).unwrap_or(20);
        let text = parts.next().unwrap_or("");
        search_reply(engine, &json!({ "query": text, "limit": limit }))
    } else if line == "status" {
        engine.status()
    } else if line == "ping" {
        json!({ "ok": true })
    } else {
        match serde_json::from_str::<Value>(line) {
            Ok(request) => dispatch(engine, &request, client).unwrap_or_else(|e| json!({ "ok": false, "error": e })),
            Err(e) => json!({ "ok": false, "error": format!("not a JSON request: {e}") }),
        }
    };
    reply.to_string()
}

fn dispatch(engine: &Arc<Engine>, r: &Value, client: &dyn Fn() -> String) -> Result<Value, String> {
    let op = r["op"].as_str().ok_or("missing \"op\"")?;
    match op {
        "ping" => Ok(json!({ "ok": true, "version": env!("CARGO_PKG_VERSION") })),
        "status" => Ok(engine.status()),
        "settings" => Ok(json!({ "ok": true, "settings": engine.settings() })),
        "settings.set" => {
            // Only the keys present are replaced; lists are replaced whole.
            let patch = r["settings"].as_object().ok_or("\"settings\" must be an object")?;
            let mut current = serde_json::to_value(engine.settings()).map_err(|e| e.to_string())?;
            for (key, value) in patch {
                if current.get(key).is_none() { return Err(format!("unknown setting \"{key}\"")); }
                current[key] = value.clone();
            }
            let new: Settings = serde_json::from_value(current).map_err(|e| format!("invalid settings: {e}"))?;
            settings_reply(engine.update_settings(new, &client()))
        }
        "drive" => {
            let enabled = r["enabled"].as_bool().ok_or("\"enabled\" (true or false) is required")?;
            let wanted = r["drive"].as_str().or(r["serial"].as_str()).ok_or("\"drive\" (\"D:\") or \"serial\" is required")?;
            let drives = volumes::detect();
            let drive = drives.iter().find(|d| {
                wanted.trim_end_matches(['\\', ':']).eq_ignore_ascii_case(&d.letter.to_string())
                    || wanted.eq_ignore_ascii_case(&crate::settings::serial_text(d.serial))
            }).ok_or_else(|| format!("no drive \"{wanted}\" (see status)"))?;
            if enabled && let Some(reason) = &drive.unsupported { return Err(format!("{}: {reason}", drive.letter)); }
            let mut new = engine.settings();
            new.set_drive(drive.serial, drive.letter, enabled);
            settings_reply(engine.update_settings(new, &client()))
        }
        "exclude" => {
            let mut path = normalize_path(r["path"].as_str().ok_or("\"path\" is required")?)
                .ok_or("\"path\" must be a folder like D:\\Private")?;
            // Store the folder's real capitalization when it's indexed (d:\dragon age mods -> D:\Dragon Age Mods).
            if let Ok(real) = engine.with_indexes(|indexes| resolve(indexes, &path).map(|(v, e)| indexes[v].path(e as usize))) {
                path = real;
            }
            let excluded = r["excluded"].as_bool().unwrap_or(true);
            let mut new = engine.settings();
            new.excluded.retain(|p| !p.eq_ignore_ascii_case(&path));
            if excluded { new.excluded.push(path); }
            settings_reply(engine.update_settings(new, &client()))
        }
        "rescan" => {
            let drive = r["drive"].as_str().ok_or("\"drive\" is required")?;
            let letter = drive.chars().next().ok_or("\"drive\" is empty")?;
            engine.rescan(letter)?;
            Ok(json!({ "ok": true }))
        }
        "search" => Ok(search_reply(engine, r)),
        "children" => {
            let path = r["path"].as_str().ok_or("\"path\" is required")?;
            let sort = r["sort"].as_str().unwrap_or("size");
            let (limit, offset) = (limit_of(r, 200, 5000), r["offset"].as_u64().unwrap_or(0) as usize);
            engine.with_indexes(|indexes| {
                let (v, e) = resolve(indexes, path)?;
                Ok(query::children(indexes[v], e, sort, limit, offset))
            })
        }
        "stat" => {
            let path = r["path"].as_str().ok_or("\"path\" is required")?;
            engine.with_indexes(|indexes| {
                let (v, e) = resolve(indexes, path)?;
                Ok(query::stat(indexes[v], e))
            })
        }
        "largest" | "types" | "recent" => {
            let limit = limit_of(r, if op == "types" { 50 } else { 50 }, 2000);
            engine.with_indexes(|indexes| {
                let scope = match r["path"].as_str() { Some(p) => Some(resolve(indexes, p)?), None => None };
                Ok(match op {
                    "largest" => query::largest(indexes, scope, limit),
                    "types" => query::types(indexes, scope, limit),
                    _ => query::recent(indexes, scope, limit, r["since"].as_i64().unwrap_or(0), r["noisy"].as_bool().unwrap_or(false)),
                })
            })
        }
        _ => Err(format!("unknown op \"{op}\"")),
    }
}

fn settings_reply(result: Result<Settings, String>) -> Result<Value, String> {
    result.map(|s| json!({ "ok": true, "settings": s }))
}

fn limit_of(r: &Value, default: usize, max: usize) -> usize {
    (r["limit"].as_u64().map(|l| l as usize).unwrap_or(default)).clamp(1, max)
}

/// A path to (index number, entry). `D:` and `D:\` are the drive's root.
fn resolve(indexes: &[&Index], path: &str) -> Result<(usize, u32), String> {
    let letter = path.trim().chars().next().ok_or("empty path")?;
    let v = indexes.iter().position(|i| i.letter.eq_ignore_ascii_case(&letter))
        .ok_or_else(|| format!("{}: isn't indexed (turn the drive on, or it's still loading)", letter.to_ascii_uppercase()))?;
    let e = indexes[v].resolve(path).ok_or_else(|| format!("not found: {path}"))?;
    Ok((v, e))
}

/// `{"query","limit","kind","ext","in","sort","hidden"}`; filters may also be written into the query text.
fn search_reply(engine: &Arc<Engine>, r: &Value) -> Value {
    let started = Instant::now();
    let text = r["query"].as_str().unwrap_or("");
    let limit = limit_of(r, 20, 500);
    let hidden = r["hidden"].as_bool().unwrap_or_else(|| engine.settings().search_hidden);
    let mut request = search::parse(text, hidden);
    match r["kind"].as_str() {
        Some("file") => request.kind = Kind::File,
        Some("folder") => request.kind = Kind::Folder,
        _ => {}
    }
    if let Some(exts) = r["ext"].as_array() {
        request.exts.extend(exts.iter().filter_map(Value::as_str).map(|e| e.trim_start_matches('.').to_lowercase()));
    }
    if let Some(scope) = r["in"].as_str() { request.scope = Some(scope.to_string()); }
    match r["sort"].as_str() {
        Some("size") => request.sort = Sort::Size,
        Some("modified") => request.sort = Sort::Modified,
        _ => {}
    }
    let ready = engine.all_ready();
    engine.with_indexes(|indexes| {
        let scope = match &request.scope {
            Some(path) => match resolve(indexes, path) {
                Ok(s) => Some(s),
                Err(e) => return json!({ "ok": false, "error": format!("in: {e}"), "query": text, "results": [], "matches": 0, "ready": ready }),
            },
            None => None,
        };
        let outcome = search::search(indexes, &request, scope, limit);
        let results: Vec<Value> = outcome.hits.iter().map(|h| {
            let mut v = item(indexes[h.volume], h.entry as usize);
            v["score"] = json!(h.score);
            v["highlight"] = json!([h.highlight.0, h.highlight.1]);
            v
        }).collect();
        json!({
            "ok": true, "query": text, "matches": outcome.matches, "ready": ready,
            "ms": (started.elapsed().as_secs_f64() * 10_000.0).round() / 10.0,
            "results": results,
        })
    })
}
