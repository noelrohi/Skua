//! A fake Engine: a Unix socket server in a temp data folder that speaks the Control Surface's framing, and fake DTOs.

#![allow(dead_code)]

use std::io::BufReader;
use std::os::unix::net::UnixListener;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::thread;

use serde_json::{Value, json};
use skua_tui::rpc::{read_message, write_message};

pub type Handler = dyn Fn(&str, &Value) -> Result<Value, (i64, String)> + Send + Sync;

/// A Skua data folder of its own, removed on drop.
pub fn skua_dir() -> tempfile::TempDir {
    // macOS's sun_path holds 104 bytes, so the folder sits in /tmp rather than the long per-user TMPDIR.
    tempfile::Builder::new().prefix("skua-tui-").tempdir_in("/tmp").unwrap()
}

pub fn write_manager_file(skua_dir: &Path, contents: Value) {
    std::fs::write(skua_dir.join("Skua.manager.json"), contents.to_string()).unwrap();
}

/// An Engine named `name` under `skua_dir`, answering every call with `handler`. It records each call as `(method, params)`.
pub struct FakeEngine {
    pub socket: PathBuf,
    pub calls: Arc<Mutex<Vec<(String, Value)>>>,
}

impl FakeEngine {
    pub fn start(
        skua_dir: &Path,
        name: &str,
        handler: impl Fn(&str, &Value) -> Result<Value, (i64, String)> + Send + Sync + 'static,
    ) -> FakeEngine {
        let engines = skua_dir.join("engines");
        std::fs::create_dir_all(&engines).unwrap();
        let socket = engines.join(format!("{name}.sock"));
        let listener = UnixListener::bind(&socket).unwrap();
        let calls = Arc::new(Mutex::new(Vec::new()));
        let handler: Arc<Handler> = Arc::new(handler);
        let recorded = calls.clone();
        thread::spawn(move || {
            for stream in listener.incoming() {
                let Ok(stream) = stream else { return };
                let (handler, recorded) = (handler.clone(), recorded.clone());
                thread::spawn(move || {
                    let mut writer = stream.try_clone().unwrap();
                    let mut reader = BufReader::new(stream);
                    while let Ok(Some(body)) = read_message(&mut reader) {
                        let request: Value = serde_json::from_slice(&body).unwrap();
                        let method = request["method"].as_str().unwrap().to_owned();
                        let params = request.get("params").cloned().unwrap_or(json!([]));
                        recorded.lock().unwrap().push((method.clone(), params.clone()));
                        let reply = match handler(&method, &params) {
                            Ok(result) => json!({ "jsonrpc": "2.0", "id": request["id"], "result": result }),
                            Err((code, message)) => {
                                json!({ "jsonrpc": "2.0", "id": request["id"], "error": { "code": code, "message": message } })
                            }
                        };
                        if write_message(&mut writer, reply.to_string().as_bytes()).is_err() {
                            return;
                        }
                    }
                });
            }
        });
        FakeEngine { socket, calls }
    }

    pub fn methods(&self) -> Vec<String> {
        self.calls.lock().unwrap().iter().map(|(m, _)| m.clone()).collect()
    }

    pub fn params_of(&self, method: &str) -> Vec<Value> {
        self.calls
            .lock()
            .unwrap()
            .iter()
            .filter(|(m, _)| m == method)
            .map(|(_, p)| p.clone())
            .collect()
    }
}

/// An Engine of `protocol` that answers `status` with `status` and refuses what it doesn't know.
pub fn engine_of(
    protocol: i64,
    name: &str,
    status: Value,
) -> impl Fn(&str, &Value) -> Result<Value, (i64, String)> + Send + Sync + 'static {
    let name = name.to_owned();
    move |method, _| match method {
        "hello" => Ok(hello(protocol, &name)),
        "status" => Ok(status.clone()),
        "logs" => Ok(json!({ "entries": [], "next": "0", "gap": false })),
        _ => Err((-32601, format!("no method {method}"))),
    }
}

pub fn hello(protocol: i64, name: &str) -> Value {
    json!({ "protocol": protocol, "build": "1.4.4.4+abc1234", "engineName": name, "pid": 4242, "host": "engine" })
}

pub fn player(name: &str) -> Value {
    json!({
        "name": name, "level": 100, "class": "Chaos Avenger", "hp": 2400, "maxHp": 3000, "mp": 80, "maxMp": 100, "gold": 1234567,
        "map": "battleon", "cell": "Enter", "pad": "Spawn", "alive": true, "inCombat": true, "xp": 0, "requiredXp": 0, "xpPercent": null
    })
}

/// A `status` reply: playing with `script` running when it is set, else at the login screen.
pub fn status(name: &str, playing: bool, script: Option<&str>) -> Value {
    let run = script.map(|s| {
        json!({ "number": 3, "script": s, "startedAt": "2026-10-02T14:00:00Z", "relogins": 0, "reloggingIn": false,
                "dialogs": "ask", "dialogTimeoutSec": 300, "elapsedSec": 754.2 })
    });
    json!({
        "engine": { "name": name, "build": "1.4.4.4+abc1234", "protocol": 13, "uptimeSec": 3725.0, "pid": 4242, "host": "engine" },
        "game": {
            "gameHostUp": true,
            "state": if playing { "playing" } else { "loginScreen" },
            "server": if playing { json!("Artix") } else { Value::Null },
            "player": if playing { player(name) } else { Value::Null },
            "playerAgeSec": null
        },
        "script": { "state": if run.is_some() { "running" } else { "idle" }, "run": run, "lastRun": null },
        "pendingDialogs": []
    })
}

pub fn log_entry(seq: i64, text: &str) -> Value {
    json!({ "seq": seq, "ts": 1_791_036_000_000i64 + seq * 1000, "kind": "script", "run": 3, "text": text })
}

pub fn event(seq: i64, event_type: &str, data: Value) -> Value {
    json!({ "seq": seq, "ts": 1_791_036_000_000i64 + seq * 1000, "kind": "events", "run": null, "type": event_type, "data": data })
}
