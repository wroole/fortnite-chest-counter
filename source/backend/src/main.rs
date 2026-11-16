//! Local, read-only command-line boundary around the audited replay parser.
use std::{ffi::OsString, path::Path, time::Instant};
use serde_json::{json, Value};

const MAX_FILE_BYTES: u64 = 256 * 1024 * 1024;

struct Progress { start: Instant, last: i32 }
impl fnreplay::Visitor for Progress {
    fn on_progress(&mut self, fraction: f32) {
        let value = (fraction * 100.0).round() as i32;
        if value != self.last { eprintln!("PROGRESS {value}"); self.last = value; }
    }
    fn should_stop(&self) -> bool { self.start.elapsed().as_secs() >= 110 }
}

fn summarize(path: &Path) -> Result<Value, String> {
    if !path.extension().is_some_and(|e| e.eq_ignore_ascii_case("replay")) {
        return Err("extension: Select a .replay file".into());
    }
    let before = std::fs::metadata(path).map_err(|e| format!("read: {e}"))?;
    if !before.is_file() || before.len() == 0 || before.len() > MAX_FILE_BYTES {
        return Err("size: Replay must be a non-empty file smaller than 256 MiB".into());
    }
    let bytes = std::fs::read(path).map_err(|e| format!("read: {e}"))?;
    let after = std::fs::metadata(path).map_err(|e| format!("read: {e}"))?;
    if before.len() != after.len() || before.modified().ok() != after.modified().ok() {
        return Err("incomplete: Replay is still being written".into());
    }
    let mut progress = Progress { start: Instant::now(), last: -1 };
    let (replay, analysis) = chests::analyze_replay(&bytes, &chests::BreakConfig::default(), &mut progress)
        .map_err(|e| format!("parse: {e}"))?;
    if replay.stopped_early { return Err("timeout: Parsing exceeded the time limit".into()); }
    if replay.stream.packets == 0 { return Err("unsupported: No network packets were decoded".into()); }
    let owner = analysis.owner.as_deref().ok_or("owner: Recording player could not be identified")?;
    let player = analysis.player(owner).ok_or("owner: Recording player was not found")?;
    let keep = |e: &&chests::ChestEvent| !e.from_loot;
    let opened = player.opened.iter().filter(keep).count();
    let broken = player.broken.len();
    let estimated = player.opened.iter().filter(|e| e.from_loot).count();
    let known_version = replay.header.major == Some(43) && matches!(replay.header.minor, Some(0 | 10));
    let partial = replay.packet_health() < 0.999 || replay.broken_chunks > 0 || !known_version;
    let events = player_events(player);
    let id = analysis.match_id.as_deref().map(str::to_owned)
        .or_else(|| replay.header.guid_hex()).ok_or("identity: Match identifier is missing")?;
    Ok(json!({
        "schema_version": 3,
        "match_id": id,
        "recording_id": replay.header.guid_hex(),
        "player": owner,
        "player_id": player.epic_id,
        "timestamp_ms": replay.info.timestamp_ms,
        "duration_seconds": replay.info.length_ms as f64 / 1000.0,
        "branch": replay.header.branch,
        "opened": opened, "broken": broken, "total": opened + broken,
        "estimated": estimated,
        "status": if partial { "partial" } else { "parsed" },
        "packets": replay.stream.packets,
        "failed_packets": replay.stream.failed_packets,
        "broken_chunks": replay.broken_chunks,
        "packet_health": replay.packet_health(),
        "events": events,
    }))
}

fn player_events(player: &chests::PlayerChests) -> Vec<Value> {
    player.in_order().into_iter().map(|(event, is_open)| json!({
        "seconds": event.time,
        "action": if event.from_loot { "estimated" } else if is_open { "opened" } else { "broken" },
        "kind": event.kind.label(),
    })).collect()
}

fn main() {
    std::panic::set_hook(Box::new(|_| {}));
    let Some(path): Option<OsString> = std::env::args_os().nth(1) else {
        println!("{}", json!({"error":"usage: replay-analyzer <file.replay>"}));
        std::process::exit(2);
    };
    match std::panic::catch_unwind(|| summarize(Path::new(&path))) {
        Ok(Ok(result)) => println!("{result}"),
        result => {
            let message = match result {
                Ok(Err(error)) => error,
                _ => "corrupt: The replay could not be read safely".into(),
            };
            println!("{}", json!({"error":message}));
            std::process::exit(1);
        }
    }
}
