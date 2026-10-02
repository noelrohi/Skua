mod common;

use std::io::{BufRead, BufReader, Read, Write};
use std::os::unix::net::UnixListener;
use std::time::Duration;

use common::*;
use serde_json::json;
use skua_tui::app::Tab;
use skua_tui::dto::GameState;
use skua_tui::engine::{Engine, Error, NOT_LOGGED_IN, PROTOCOL};
use skua_tui::poller::{EngineView, Focus, LOG_TAIL, Poller};

const TIMEOUT: Duration = Duration::from_secs(5);

#[test]
fn hello_carries_this_protocol_and_status_reads_the_engine() {
    let dir = skua_dir();
    let engine = FakeEngine::start(
        dir.path(),
        "alice",
        engine_of(PROTOCOL, "alice", status("alice", true, Some("Farm/AtlasGold.cs"))),
    );

    let mut connection = Engine::connect(&engine.socket, TIMEOUT).unwrap();
    let status = connection.status().unwrap();

    assert_eq!(engine.params_of("hello"), vec![json!([PROTOCOL])]);
    assert_eq!(connection.hello.engine_name, "alice");
    assert_eq!(status.game.state, GameState::Playing);
    assert_eq!(status.game.player.unwrap().name, "alice");
    assert_eq!(status.script.run.unwrap().script, "Farm/AtlasGold.cs");
}

#[test]
fn an_engine_of_another_protocol_is_refused_after_hello_and_nothing_else_is_asked() {
    let dir = skua_dir();
    let engine = FakeEngine::start(dir.path(), "bob", engine_of(11, "bob", status("bob", true, None)));

    let error = Engine::connect(&engine.socket, TIMEOUT).err().expect("a mismatch");

    assert!(
        matches!(&error, Error::ProtocolMismatch { protocol: 11, engine, .. } if engine == "bob"),
        "{error:?}"
    );
    let message = error.to_string();
    assert!(
        message.contains("Engine 'bob'") && message.contains("speaks protocol 11") && message.contains("speaks 16"),
        "{message}"
    );
    assert_eq!(engine.methods(), vec!["hello"]);
}

#[test]
fn requests_are_framed_with_a_content_length_header_as_streamjsonrpc_reads_them() {
    let dir = skua_dir();
    let socket = dir.path().join("raw.sock");
    let listener = UnixListener::bind(&socket).unwrap();
    let server = std::thread::spawn(move || {
        let (stream, _) = listener.accept().unwrap();
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let mut header = String::new();
        reader.read_line(&mut header).unwrap();
        let mut blank = String::new();
        reader.read_line(&mut blank).unwrap();
        let length: usize = header
            .trim_end()
            .strip_prefix("Content-Length: ")
            .unwrap()
            .parse()
            .unwrap();
        let mut body = vec![0; length];
        reader.read_exact(&mut body).unwrap();
        // StreamJsonRpc may send other headers; a reply with a Content-Type must still be read.
        let reply = json!({ "jsonrpc": "2.0", "id": 1, "result": hello(PROTOCOL, "raw") }).to_string();
        let mut writer = stream;
        write!(
            writer,
            "Content-Type: application/vscode-jsonrpc; charset=utf-8\r\nContent-Length: {}\r\n\r\n{reply}",
            reply.len()
        )
        .unwrap();
        (
            header,
            blank,
            serde_json::from_slice::<serde_json::Value>(&body).unwrap(),
        )
    });

    let connection = Engine::connect(&socket, TIMEOUT).unwrap();
    let (header, blank, body) = server.join().unwrap();

    assert!(header.ends_with("\r\n") && blank == "\r\n");
    assert_eq!(body["jsonrpc"], "2.0");
    assert_eq!(body["method"], "hello");
    assert_eq!(connection.hello.engine_name, "raw");
}

#[test]
fn a_refused_call_carries_the_engines_error_code() {
    let dir = skua_dir();
    let engine = FakeEngine::start(dir.path(), "alice", |method, _| match method {
        "hello" => Ok(hello(PROTOCOL, "alice")),
        _ => Err((NOT_LOGGED_IN, "Not logged in.".into())),
    });

    let error = Engine::connect(&engine.socket, TIMEOUT)
        .unwrap()
        .inventory()
        .err()
        .unwrap();

    assert_eq!(
        error,
        Error::Remote {
            code: NOT_LOGGED_IN,
            message: "Not logged in.".into()
        }
    );
    assert_eq!(engine.params_of("inventory"), vec![json!(["inventory"])]);
}

#[test]
fn the_poller_lists_each_socket_as_up_mismatched_or_offline() {
    let dir = skua_dir();
    let _alice = FakeEngine::start(
        dir.path(),
        "alice",
        engine_of(PROTOCOL, "alice", status("alice", false, None)),
    );
    let _bob = FakeEngine::start(dir.path(), "bob", engine_of(11, "bob", status("bob", false, None)));
    // A stale socket: bound, then nobody listens.
    drop(UnixListener::bind(dir.path().join("engines/carol.sock")).unwrap());
    std::fs::write(dir.path().join("engines/Not-Valid.sock"), "").unwrap();

    let snapshot = Poller::new(dir.path().to_owned(), TIMEOUT).poll(&Focus::default());

    assert_eq!(snapshot.engines.keys().collect::<Vec<_>>(), ["alice", "bob", "carol"]);
    assert!(matches!(snapshot.engines["alice"], EngineView::Up { .. }));
    assert!(matches!(
        snapshot.engines["bob"],
        EngineView::Failed(Error::ProtocolMismatch { protocol: 11, .. })
    ));
    assert!(matches!(snapshot.engines["carol"], EngineView::Offline));
}

#[test]
fn logs_start_with_the_tail_then_follow_from_the_cursor() {
    let dir = skua_dir();
    let engine = FakeEngine::start(dir.path(), "alice", |method, params| match method {
        "hello" => Ok(hello(PROTOCOL, "alice")),
        "status" => Ok(status("alice", true, None)),
        "logs" if params[1].is_null() => {
            Ok(json!({ "entries": [log_entry(1, "first"), log_entry(2, "second")], "next": "c2", "gap": false }))
        }
        "logs" => Ok(
            json!({ "entries": [event(3, "map.joined", json!({ "map": "battleon", "cell": "Enter" }))], "next": "c3", "gap": false }),
        ),
        "inventory" => Ok(json!({ "kind": "inventory", "usedSlots": 1, "totalSlots": 120, "items": [] })),
        _ => Err((-32601, "no".into())),
    });
    let mut poller = Poller::new(dir.path().to_owned(), TIMEOUT);
    let focus = Focus {
        engine: Some("alice".into()),
        tab: Tab::Inventory,
    };

    poller.poll(&focus);
    let snapshot = poller.poll(&focus);

    assert_eq!(
        engine.params_of("logs"),
        vec![
            json!(["events", null, null, LOG_TAIL]),
            json!(["all", null, null, LOG_TAIL]),
            json!(["all", "c2", null, null])
        ]
    );
    let texts: Vec<_> = snapshot
        .detail
        .logs
        .iter()
        .map(|e| e.text.clone().or(e.event_type.clone()).unwrap())
        .collect();
    assert_eq!(texts, ["first", "second", "map.joined"]);
    assert_eq!(snapshot.detail.inventory.unwrap().unwrap().total_slots, Some(120));
}

#[test]
fn hook_runs_come_from_the_newest_events_then_from_the_logs_as_they_arrive() {
    let dir = skua_dir();
    let ran = |seq, hook: &str| {
        event(
            seq,
            "hook.ran",
            json!({ "hook": hook, "eventSeq": seq - 1, "startedAt": 0, "durationMs": 5, "exitCode": 0, "output": "" }),
        )
    };
    let (first, second, third) = (ran(5, "player.death"), ran(9, "inventory.full"), ran(12, "player.afk"));
    let _engine = FakeEngine::start(dir.path(), "alice", move |method, params| match method {
        "hello" => Ok(hello(PROTOCOL, "alice")),
        "status" => Ok(status("alice", true, None)),
        "logs" if params[0] == "events" => {
            Ok(json!({ "entries": [event(4, "player.death", json!({})), first, second], "next": "c9", "gap": false }))
        }
        "logs" if params[1].is_null() => {
            Ok(json!({ "entries": [log_entry(8, "farming"), second], "next": "c9", "gap": false }))
        }
        "logs" => Ok(json!({ "entries": [log_entry(10, "banked"), third], "next": "c12", "gap": false })),
        _ => Err((-32601, "no".into())),
    });
    let mut poller = Poller::new(dir.path().to_owned(), TIMEOUT);
    let focus = Focus {
        engine: Some("alice".into()),
        tab: Tab::Hooks,
    };

    poller.poll(&focus);
    let snapshot = poller.poll(&focus);

    let runs: Vec<_> = snapshot.detail.hook_runs.iter().map(|e| e.seq).collect();
    assert_eq!(runs, [5, 9, 12]);
    assert!(!snapshot.hook_runner);
}

#[test]
fn the_manager_file_is_read_and_left_as_it_was() {
    let dir = skua_dir();
    let contents = json!({
        "accounts": [{ "name": "alice", "username": "Alice", "displayName": "Alice", "tags": [] }, { "name": "Bad Name", "username": "x", "tags": [] }],
        "groups": [{ "name": "Farm", "usernames": ["alice"] }],
        "lastServer": "Artix"
    });
    write_manager_file(dir.path(), contents.clone());

    let snapshot = Poller::new(dir.path().to_owned(), TIMEOUT).poll(&Focus::default());

    let accounts = snapshot.accounts.unwrap();
    assert_eq!(
        accounts.accounts.iter().map(|a| a.name.as_str()).collect::<Vec<_>>(),
        ["alice"]
    );
    assert_eq!(accounts.groups[0].usernames, ["alice"]);
    assert_eq!(
        std::fs::read_to_string(dir.path().join("Skua.manager.json")).unwrap(),
        contents.to_string()
    );
}

#[test]
fn this_protocol_is_skua_controls() {
    let source = std::fs::read_to_string(concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../Skua.Control/ControlProtocol.cs"
    ))
    .unwrap();
    let version = source
        .split("public const int Version = ")
        .nth(1)
        .and_then(|rest| rest.split(';').next())
        .unwrap();
    assert_eq!(
        version.parse::<i64>().unwrap(),
        PROTOCOL,
        "bump PROTOCOL in src/engine.rs with ControlProtocol.Version"
    );
}
