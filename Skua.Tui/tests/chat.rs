mod common;

use std::sync::Mutex;
use std::sync::mpsc::{self, Receiver, Sender};
use std::time::{Duration, Instant};

use common::*;
use ratatui::Terminal;
use ratatui::backend::TestBackend;
use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
use serde_json::{Value, json};
use skua_tui::actions::Runner;
use skua_tui::app::{App, Tab};
use skua_tui::chat::{CHAT_TAIL, Follower, MAX_CHAT_ENTRIES};
use skua_tui::engine::{NOT_LOGGED_IN, PROTOCOL};
use skua_tui::poller::Poller;

/// `ErrorCode.InvalidArgument` on the wire.
const INVALID_ARGUMENT: i64 = 1003;

/// What the fake's subscription pushes next: a `LogPage`, or None to drop the connection.
type Push = Option<Value>;

/// A playing Engine whose `subscribe` pushes what the test sends, after a tail of `tail`.
fn engine(
    tail: Vec<Value>,
    pushes: Receiver<Push>,
    refuse_chat: Option<(i64, &'static str)>,
) -> impl Fn(&str, &Value) -> Result<Value, (i64, String)> + Send + Sync + 'static {
    let pushes = Mutex::new(pushes);
    move |method, params| match method {
        "hello" => Ok(hello(PROTOCOL, "alice")),
        "status" => Ok(status("alice", true, None)),
        "logs" if params[0] == "game" => Ok(json!({ "entries": tail, "next": "g2", "gap": true })),
        "logs" => Ok(json!({ "entries": [], "next": "0", "gap": false })),
        "subscribe" => Ok(json!({ "token": 1 })),
        "$/enumerator/next" => match pushes.lock().unwrap().recv() {
            Ok(Some(page)) => Ok(json!({ "values": [page], "finished": false })),
            _ => Err((DROP_CONNECTION, String::new())),
        },
        "chat_send" => match refuse_chat {
            Some((code, message)) => Err((code, message.into())),
            None => Ok(
                json!({ "channel": if params[1].is_null() { "zone" } else { "whisper" }, "to": params[1], "text": params[0] }),
            ),
        },
        _ => Err((-32601, format!("no method {method}"))),
    }
}

/// skua-tui on alice's Chat tab, with what main does in place.
struct Tui {
    _dir: tempfile::TempDir,
    app: App,
    poller: Poller,
    runner: Runner,
    follower: Follower,
    alice: FakeEngine,
    pushes: Sender<Push>,
}

impl Tui {
    fn new(tail: Vec<Value>) -> Tui {
        Tui::with(tail, None)
    }

    fn with(tail: Vec<Value>, refuse_chat: Option<(i64, &'static str)>) -> Tui {
        let dir = skua_dir();
        write_manager_file(
            dir.path(),
            json!({ "accounts": [{ "name": "alice", "username": "alice", "tags": [] }], "groups": [] }),
        );
        let (pushes, received) = mpsc::channel();
        let alice = FakeEngine::start(dir.path(), "alice", engine(tail, received, refuse_chat));
        let mut tui = Tui {
            app: App::new(dir.path().to_owned()),
            poller: Poller::new(dir.path().to_owned(), Duration::from_secs(5)),
            runner: Runner::new(dir.path().to_owned()),
            follower: Follower::new(dir.path().to_owned()),
            _dir: dir,
            alice,
            pushes,
        };
        tui.app.set_snapshot(tui.poller.poll(&tui.app.focus()));
        tui.key(KeyCode::Char('6'));
        assert_eq!(tui.app.tab, Tab::Chat);
        tui.follower.sync(&mut tui.app);
        tui
    }

    /// Syncs the follow, as main's loop does, until `done` or 5 s.
    fn wait_until(&mut self, done: impl Fn(&App) -> bool) {
        let started = Instant::now();
        while !done(&self.app) {
            assert!(
                started.elapsed() < Duration::from_secs(5),
                "timed out; chat: {:?}",
                self.app.chat
            );
            std::thread::sleep(Duration::from_millis(10));
            self.follower.sync(&mut self.app);
        }
    }

    fn key(&mut self, code: KeyCode) {
        self.app.on_key(KeyEvent::new(code, KeyModifiers::NONE));
    }

    fn keys(&mut self, text: &str) {
        text.chars().for_each(|c| self.key(KeyCode::Char(c)));
    }

    /// Opens the input, types `text` and sends it, running the job as main's threads do.
    fn say(&mut self, text: &str) {
        if !self.app.chat.typing {
            self.key(KeyCode::Enter);
        }
        self.keys(text);
        self.key(KeyCode::Enter);
        for job in std::mem::take(&mut self.app.jobs) {
            let outcome = self.runner.run(job);
            self.app.on_outcome(outcome);
        }
    }

    fn screen(&self, width: u16, height: u16) -> String {
        let mut terminal = Terminal::new(TestBackend::new(width, height)).unwrap();
        terminal.draw(|frame| skua_tui::ui::draw(frame, &self.app)).unwrap();
        let buffer = terminal.backend().buffer();
        let mut out = String::new();
        for y in 0..height {
            for x in 0..width {
                out.push_str(buffer[(x, y)].symbol());
            }
            out.push('\n');
        }
        println!("{out}");
        out
    }
}

fn page(entries: Vec<Value>, next: &str, gap: bool) -> Value {
    json!({ "entries": entries, "next": next, "gap": gap })
}

fn assert_shows(screen: &str, texts: &[&str]) {
    for text in texts {
        assert!(screen.contains(text), "expected {text:?} on screen:\n{screen}");
    }
}

fn line_of(screen: &str, text: &str) -> usize {
    screen
        .lines()
        .position(|l| l.contains(text))
        .unwrap_or_else(|| panic!("{text:?} isn't on screen:\n{screen}"))
}

#[test]
fn the_chat_tab_shows_the_tail_then_follows_what_the_engine_pushes_newest_at_the_bottom() {
    let mut tui = Tui::new(vec![
        game_message(1, "server", None, None, "You joined \"battleon-9999\""),
        game_message(2, "zone", Some("bob"), None, "anyone for ultra?"),
    ]);
    tui.wait_until(|app| app.chat.entries.len() == 2);

    assert_eq!(
        tui.alice.params_of("logs").last(),
        Some(&json!(["game", null, null, CHAT_TAIL]))
    );
    assert_eq!(tui.alice.params_of("subscribe"), vec![json!([["game"], "g2"])]);
    assert_eq!(tui.alice.params_of("$/enumerator/next"), vec![json!([1])]);

    tui.pushes
        .send(Some(page(
            vec![game_message(
                3,
                "whisper",
                Some("carol"),
                Some("alice"),
                "psst, got a sec?",
            )],
            "g3",
            false,
        )))
        .unwrap();
    tui.wait_until(|app| app.chat.entries.len() == 3);
    tui.pushes
        .send(Some(page(
            vec![game_message(9, "zone", Some("dave"), None, "brb")],
            "g9",
            true,
        )))
        .unwrap();
    tui.wait_until(|app| app.chat.entries.len() == 5);

    let screen = tui.screen(120, 24);
    assert_shows(
        &screen,
        &[
            " Game │ Chat │",
            "[server]  You joined \"battleon-9999\"",
            "[zone]    bob: anyone for ultra?",
            "[whisper] carol → alice: psst, got a sec?",
            "… messages missed here",
            "[zone]    dave: brb",
            "enter chat",
        ],
    );
    assert!(line_of(&screen, "anyone for ultra?") < line_of(&screen, "psst, got a sec?"));
    assert!(line_of(&screen, "psst, got a sec?") < line_of(&screen, "dave: brb"));
}

#[test]
fn the_chat_keeps_only_the_newest_entries() {
    let mut tui = Tui::new(vec![]);
    let many: Vec<Value> = (1..=MAX_CHAT_ENTRIES as i64 + 50)
        .map(|seq| game_message(seq, "zone", Some("bob"), None, &format!("line {seq}")))
        .collect();
    tui.pushes.send(Some(page(many, "gx", false))).unwrap();
    tui.wait_until(|app| {
        app.chat
            .entries
            .back()
            .is_some_and(|e| e.seq == MAX_CHAT_ENTRIES as i64 + 50)
    });

    assert_eq!(tui.app.chat.entries.len(), MAX_CHAT_ENTRIES);
    assert_eq!(tui.app.chat.entries.front().unwrap().seq, 51);
}

#[test]
fn a_dropped_connection_ends_the_follow_and_says_so() {
    let mut tui = Tui::new(vec![game_message(1, "zone", Some("bob"), None, "hi")]);
    tui.wait_until(|app| app.chat.entries.len() == 1);

    tui.pushes.send(None).unwrap();
    tui.wait_until(|app| app.chat.ended.is_some());

    assert_shows(
        &tui.screen(120, 24),
        &["follow ended: the Engine stopped answering", "bob: hi"],
    );
}

#[test]
fn leaving_the_chat_tab_stops_following_and_coming_back_follows_again() {
    let mut tui = Tui::new(vec![game_message(1, "zone", Some("bob"), None, "hi")]);
    tui.wait_until(|app| app.chat.entries.len() == 1);

    tui.key(KeyCode::Char('1'));
    tui.follower.sync(&mut tui.app);
    assert!(tui.app.chat.engine().is_none());
    tui.key(KeyCode::Char('6'));
    tui.follower.sync(&mut tui.app);
    tui.wait_until(|app| app.chat.entries.len() == 1);

    assert_eq!(tui.alice.params_of("subscribe").len(), 2);
}

#[test]
fn typed_text_is_sent_as_zone_chat_and_slash_w_whispers() {
    let mut tui = Tui::new(vec![]);
    tui.key(KeyCode::Enter);
    tui.keys("hello all");
    assert_shows(&tui.screen(120, 24), &["> hello all"]);
    tui.say("");
    tui.say("/w bob  psst there");

    assert_eq!(
        tui.alice.params_of("chat_send"),
        vec![json!(["hello all", null]), json!(["psst there", "bob"])]
    );
    assert!(tui.app.chat.typing, "the input stays open for the next message");
    assert_shows(&tui.screen(120, 24), &["whispered bob"]);
}

#[test]
fn keys_typed_in_the_input_are_text_not_actions() {
    let mut tui = Tui::new(vec![]);
    tui.key(KeyCode::Enter);
    tui.keys("qxs");

    assert!(!tui.app.quit);
    assert!(tui.app.jobs.is_empty());
    assert_eq!(tui.app.chat.input, "qxs");

    tui.key(KeyCode::Esc);
    assert!(!tui.app.chat.typing);
    assert_eq!(tui.app.chat.input, "qxs", "esc keeps what was typed");
}

#[test]
fn a_slash_command_other_than_w_is_not_sent() {
    let mut tui = Tui::new(vec![]);
    tui.say("/party hi");
    tui.say("/w bob");

    assert!(tui.alice.params_of("chat_send").is_empty());
    assert_shows(&tui.screen(120, 24), &["not sent: /w <name> <text> whispers"]);
}

#[test]
fn the_engines_refusal_is_shown_in_the_chat() {
    let mut tui = Tui::with(vec![], Some((NOT_LOGGED_IN, "Not logged in.")));
    tui.say("hello");
    assert_eq!(tui.alice.params_of("chat_send"), vec![json!(["hello", null])]);
    assert_shows(&tui.screen(120, 24), &["not sent: Not logged in."]);

    let mut tui = Tui::with(vec![], Some((INVALID_ARGUMENT, "Chat text can't contain '%'.")));
    tui.say("100%");
    assert_shows(&tui.screen(120, 24), &["not sent: Chat text can't contain '%'."]);
}
