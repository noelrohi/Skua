mod common;

use std::os::unix::fs::PermissionsExt;
use std::os::unix::net::UnixListener;
use std::time::Duration;

use common::*;
use ratatui::Terminal;
use ratatui::backend::TestBackend;
use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
use serde_json::{Value, json};
use skua_tui::actions::Runner;
use skua_tui::app::{App, Modal};
use skua_tui::engine::{NOT_LOGGED_IN, PROTOCOL, SCRIPT_RUNNING};
use skua_tui::poller::Poller;

type Reply = Result<Value, (i64, String)>;

// `ErrorCode`s on the wire.
const COMPILE_FAILED: i64 = 1004;
const BUSY: i64 = 1007;
const DIALOG_NOT_PENDING: i64 = 1008;
const LOGIN_FAILED: i64 = 1010;

fn servers() -> Value {
    json!({ "servers": [
        { "name": "Artix", "online": true, "playerCount": 1203, "maxPlayers": 1500, "memberOnly": false, "language": "en" },
        { "name": "Twilly", "online": true, "playerCount": 845, "maxPlayers": 1500, "memberOnly": false, "language": "en" },
        { "name": "Gravelyn", "online": false, "playerCount": 0, "maxPlayers": 500, "memberOnly": true, "language": "en" }
    ]})
}

fn scripts(query: &str) -> Value {
    let all = [
        ("Farm/AtlasGold.cs", true),
        ("Farm/Gold/GoldFarm.cs", true),
        ("Other/Classes/ArchPaladin.cs", false),
    ];
    let found: Vec<Value> = all
        .iter()
        .filter(|(path, _)| path.to_lowercase().contains(&query.to_lowercase()))
        .map(|(path, downloaded)| json!({ "path": path, "name": null, "description": null, "tags": [], "downloaded": downloaded, "outdated": false }))
        .collect();
    json!({ "source": { "owner": "auqw", "repo": "Scripts", "branch": "Skua" }, "matched": found.len(), "scripts": found })
}

fn options() -> Value {
    json!({ "script": "Farm/AtlasGold.cs", "storage": "AtlasGold", "options": [
        { "key": "Class", "category": "Options", "name": "Class", "displayName": "Class to farm with", "description": "Equipped before farming",
          "type": "enum", "value": "ChaosAvenger", "default": "ChaosAvenger", "choices": ["ChaosAvenger", "LegionRevenant", "ArchPaladin"], "transient": false },
        { "key": "Bank", "category": "Options", "name": "Bank", "displayName": "Bank when full", "description": null,
          "type": "bool", "value": "True", "default": "True", "choices": null, "transient": false },
        { "key": "Gold:Stop", "category": "Gold", "name": "Stop", "displayName": "Stop at gold", "description": null,
          "type": "int", "value": "100", "default": "100", "choices": null, "transient": false },
        { "key": "Once", "category": "Options", "name": "Once", "displayName": "Run once", "description": null,
          "type": "bool", "value": "False", "default": "False", "choices": null, "transient": true }
    ]})
}

fn question() -> Value {
    json!({ "id": 7, "caption": "Inventory full", "text": "Your inventory is full. Bank the farm items?", "choices": ["Yes", "Bank", "No"],
            "raisedAt": "2026-10-02T14:00:00Z", "expiresAt": "2026-10-02T14:02:00Z", "thread": "Script Thread", "script": "Farm/AtlasGold.cs" })
}

/// A playing Engine that answers every op the actions use, unless `refuse` names it with an error.
fn engine(
    name: &'static str,
    refuse: Option<(&'static str, i64, &'static str)>,
) -> impl Fn(&str, &Value) -> Reply + Send + Sync + 'static {
    move |method, params| {
        if let Some((refused, code, message)) = refuse
            && refused == method
        {
            return Err((code, message.into()));
        }
        match method {
            "hello" => Ok(hello(PROTOCOL, name)),
            "status" => {
                let mut status = status(name, true, Some("Farm/AtlasGold.cs"));
                status["pendingDialogs"] = json!([question()]);
                Ok(status)
            }
            "logs" => Ok(json!({ "entries": [], "next": "0", "gap": false })),
            "servers" => Ok(servers()),
            "login" => {
                Ok(json!({ "server": params[0], "alreadyLoggedIn": false, "username": name, "isTestAccount": false }))
            }
            "logout" => Ok(json!({ "wasLoggedIn": true })),
            "scripts_search" => Ok(scripts(params[0].as_str().unwrap())),
            "script_options" => Ok(options()),
            "script_start" => Ok(json!({ "run": 4, "status": { "state": "running", "run": null, "lastRun": null } })),
            "script_stop" => Ok(
                json!({ "wasRunning": true, "ended": true, "status": { "state": "idle", "run": null, "lastRun": null } }),
            ),
            "dialogs" => Ok(json!({ "questions": [question()] })),
            "dialog_answer" => Ok(json!({ "id": params[0], "choice": params[1] })),
            "join" => Ok(json!({ "map": "battleon", "cell": params[1], "pad": params[2], "alreadyThere": false })),
            "scripts_update" => Ok(
                json!({ "source": { "owner": "auqw", "repo": "Scripts", "branch": "Skua" }, "mode": "incremental",
                "commit": "abc1234", "downloaded": 15, "failed": [], "added": ["a.cs", "b.cs", "c.cs"], "changed": ["d.cs"] }),
            ),
            "shutdown_if_idle" => Ok(Value::Null),
            _ => Err((-32601, format!("no method {method}"))),
        }
    }
}

/// skua-tui over a fleet in a temp data folder: alice and bob farm (group Farm), carol has no Engine, and dave's Engine is of protocol 11.
struct Tui {
    dir: tempfile::TempDir,
    app: App,
    poller: Poller,
    runner: Runner,
    alice: FakeEngine,
    bob: FakeEngine,
    dave: FakeEngine,
}

impl Tui {
    fn new() -> Tui {
        Tui::with(None, None)
    }

    fn with(
        alice_refuses: Option<(&'static str, i64, &'static str)>,
        bob_refuses: Option<(&'static str, i64, &'static str)>,
    ) -> Tui {
        let dir = skua_dir();
        write_manager_file(
            dir.path(),
            json!({
                "accounts": [
                    { "name": "alice", "username": "alice", "tags": [] },
                    { "name": "bob", "username": "bob", "tags": [] },
                    { "name": "carol", "username": "carol", "tags": [] },
                    { "name": "dave", "username": "dave", "tags": [] }
                ],
                "groups": [{ "name": "Farm", "usernames": ["alice", "bob"] }]
            }),
        );
        write_settings(
            dir.path(),
            json!({ "client": { "TestAccountService": "skua-account-alice" } }),
        );
        let alice = FakeEngine::start(dir.path(), "alice", engine("alice", alice_refuses));
        let bob = FakeEngine::start(dir.path(), "bob", engine("bob", bob_refuses));
        let dave = FakeEngine::start(dir.path(), "dave", engine_of(11, "dave", status("dave", true, None)));
        let mut runner = Runner::new(dir.path().to_owned());
        runner.engine_executable = Some(dir.path().join("no-skua-engine"));
        runner.start_timeout = Duration::from_secs(10);
        let mut tui = Tui {
            app: App::new(dir.path().to_owned()),
            poller: Poller::new(dir.path().to_owned(), Duration::from_secs(5)),
            runner,
            dir,
            alice,
            bob,
            dave,
        };
        tui.refresh();
        tui
    }

    fn refresh(&mut self) {
        let snapshot = self.poller.poll(&self.app.focus());
        self.app.set_snapshot(snapshot);
    }

    fn select(&mut self, name: &str) {
        while self.app.selected_row().unwrap().name != name {
            self.key(KeyCode::Down);
        }
    }

    fn key(&mut self, code: KeyCode) {
        self.app.on_key(KeyEvent::new(code, KeyModifiers::NONE));
    }

    fn keys(&mut self, text: &str) {
        text.chars().for_each(|c| self.key(KeyCode::Char(c)));
    }

    /// Runs the queued jobs, as main's threads do, and hands their outcomes back.
    fn run_jobs(&mut self) {
        for job in std::mem::take(&mut self.app.jobs) {
            let outcome = self.runner.run(job);
            self.app.on_outcome(outcome);
        }
    }

    fn screen(&self) -> String {
        let mut terminal = Terminal::new(TestBackend::new(120, 32)).unwrap();
        terminal.draw(|frame| skua_tui::ui::draw(frame, &self.app)).unwrap();
        let buffer = terminal.backend().buffer();
        let mut out = String::new();
        for y in 0..32 {
            for x in 0..120 {
                out.push_str(buffer[(x, y)].symbol());
            }
            out.push('\n');
        }
        println!("{out}");
        out
    }

    fn calls_but_reads(engine: &FakeEngine) -> Vec<String> {
        engine
            .methods()
            .into_iter()
            .filter(|m| !["hello", "status", "logs", "inventory", "quests", "map"].contains(&m.as_str()))
            .collect()
    }
}

fn write_settings(skua_dir: &std::path::Path, contents: Value) {
    std::fs::write(skua_dir.join("Skua.settings.json"), contents.to_string()).unwrap();
}

fn assert_shows(screen: &str, texts: &[&str]) {
    for text in texts {
        assert!(screen.contains(text), "expected {text:?} on screen:\n{screen}");
    }
}

#[test]
fn log_in_picks_a_server_from_the_picker_and_logs_in_the_marked_engine_whose_account_is_active() {
    let mut tui = Tui::new();
    tui.key(KeyCode::Char('a'));
    tui.key(KeyCode::Char('L'));
    tui.run_jobs();

    let picker = tui.screen();
    assert_shows(
        &picker,
        &[
            "Log in to",
            "on 2 accounts",
            "Artix",
            "1203/1500",
            "Twilly",
            "845/1500",
            "Gravelyn",
            "member  offline",
            "enter log in",
        ],
    );
    assert_eq!(tui.alice.params_of("servers"), vec![json!([])]);
    assert!(tui.bob.params_of("servers").is_empty(), "one Engine lists the servers");

    tui.key(KeyCode::Down);
    tui.key(KeyCode::Enter);
    assert!(tui.app.modal.is_none());
    assert_shows(&tui.screen(), &["logging in to Twilly…"]);
    tui.run_jobs();

    assert_eq!(tui.alice.params_of("login"), vec![json!(["Twilly", null, false])]);
    assert!(tui.bob.params_of("login").is_empty(), "bob's Engine would log in alice");
    assert_shows(
        &tui.screen(),
        &["bob: not logged in: its Engine would log in the Active Account 'alice', not bob."],
    );
    assert_eq!(tui.app.activity["alice"].text, "logged in as alice on Twilly");
}

#[test]
fn a_refused_login_shows_the_engines_reason_on_its_account() {
    let mut tui = Tui::with(Some(("login", LOGIN_FAILED, "Artix is full.")), None);
    tui.key(KeyCode::Char('L'));
    tui.run_jobs();
    tui.key(KeyCode::Enter);
    tui.run_jobs();

    assert_eq!(tui.alice.params_of("login"), vec![json!(["Artix", null, false])]);
    assert_shows(&tui.screen(), &["alice: Artix is full."]);
    assert_eq!(tui.app.activity["alice"].tone, skua_tui::app::Tone::Failed);
}

/// Opens the server picker on the selected account and picks the first server.
fn log_in(tui: &mut Tui) {
    tui.key(KeyCode::Char('L'));
    tui.run_jobs();
    tui.key(KeyCode::Enter);
    tui.run_jobs();
}

#[test]
fn a_login_is_refused_before_it_is_sent_while_the_test_account_is_active() {
    let mut tui = Tui::new();
    std::fs::remove_file(tui.dir.path().join("Skua.settings.json")).unwrap();
    log_in(&mut tui);

    assert!(tui.alice.params_of("login").is_empty());
    assert_shows(
        &tui.screen(),
        &["alice: not logged in: its Engine would log in the Active Account 'test', not alice."],
    );
    assert!(
        tui.app.activity["alice"]
            .text
            .ends_with("'skua account use alice' makes alice the Active Account"),
        "{:?}",
        tui.app.activity["alice"]
    );
}

#[test]
fn a_login_is_refused_while_the_active_account_is_a_keychain_service_set_by_hand() {
    let mut tui = Tui::new();
    write_settings(
        tui.dir.path(),
        json!({ "client": { "TestAccountService": "my-aqw-login" } }),
    );
    log_in(&mut tui);

    assert!(tui.alice.params_of("login").is_empty());
    assert_shows(
        &tui.screen(),
        &["alice: not logged in: the Active Account is the Keychain service 'my-aqw-login', which is no account's."],
    );
}

#[test]
fn the_active_account_is_read_afresh_and_its_keys_ignoring_case_as_core_reads_them() {
    let mut tui = Tui::new();
    tui.select("bob");
    write_settings(
        tui.dir.path(),
        json!({ "Client": { "testaccountservice": "skua-account-bob" } }),
    );
    log_in(&mut tui);

    assert_eq!(tui.bob.params_of("login"), vec![json!(["Artix", null, false])]);
    assert_shows(&tui.screen(), &["bob: logged in as bob on Artix"]);
}

#[test]
fn a_login_on_an_engine_the_skua_app_hosts_is_refused() {
    let dir = skua_dir();
    write_manager_file(
        dir.path(),
        json!({ "accounts": [{ "name": "erin", "username": "erin", "tags": [] }], "groups": [] }),
    );
    write_settings(
        dir.path(),
        json!({ "client": { "TestAccountService": "skua-account-erin" } }),
    );
    let windowless = engine("erin", None);
    let erin = FakeEngine::start(dir.path(), "erin", move |method, params| match method {
        "hello" => Ok(
            json!({ "protocol": PROTOCOL, "build": "1.4.4.4+abc1234", "engineName": "erin", "pid": 4242, "host": "app" }),
        ),
        _ => windowless(method, params),
    });
    let mut app = App::new(dir.path().to_owned());
    app.set_snapshot(Poller::new(dir.path().to_owned(), Duration::from_secs(5)).poll(&app.focus()));
    let runner = Runner::new(dir.path().to_owned());
    for code in [KeyCode::Char('L'), KeyCode::Enter] {
        app.on_key(KeyEvent::new(code, KeyModifiers::NONE));
        for job in std::mem::take(&mut app.jobs) {
            app.on_outcome(runner.run(job));
        }
    }

    assert!(erin.params_of("login").is_empty());
    assert_eq!(
        app.activity["erin"].text,
        "not logged in: the Skua app hosts this Engine, with an account skua-tui can't see; log in from the app"
    );
}

#[test]
fn log_out_logs_out_the_selected_account() {
    let mut tui = Tui::new();
    tui.key(KeyCode::Char('O'));
    tui.run_jobs();

    assert_eq!(tui.alice.params_of("logout"), vec![json!([])]);
    assert!(Tui::calls_but_reads(&tui.bob).is_empty());
    assert_shows(&tui.screen(), &["alice: logged out"]);
}

#[test]
fn start_script_searches_reads_its_options_and_starts_it_with_the_changed_ones_on_every_marked_account() {
    let mut tui = Tui::new();
    tui.key(KeyCode::Char('a'));
    tui.key(KeyCode::Char('s'));
    tui.run_jobs();
    assert_shows(
        &tui.screen(),
        &[
            "Start a Script",
            "Farm/AtlasGold.cs",
            "Other/Classes/ArchPaladin.cs  not downloaded: U",
        ],
    );

    tui.keys("atlas");
    tui.run_jobs();
    assert_eq!(
        tui.alice.params_of("scripts_search").last(),
        Some(&json!(["atlas", null]))
    );
    assert!(!tui.screen().contains("GoldFarm"));
    tui.key(KeyCode::Enter);
    tui.run_jobs();
    assert_eq!(
        tui.alice.params_of("script_options"),
        vec![json!(["Farm/AtlasGold.cs"])]
    );

    let form = tui.screen();
    assert_shows(
        &form,
        &[
            "Options · AtlasGold.cs",
            "Class to farm with          ‹ ChaosAvenger ›",
            "Bank when full              ‹ True ›",
            "Stop at gold                100",
            "Run once                    ‹ False ›  (resets each start)",
            "Equipped before farming",
            "starts on 2 accounts; changed values are stored as its options",
        ],
    );

    tui.key(KeyCode::Right);
    tui.key(KeyCode::Down);
    tui.key(KeyCode::Char(' '));
    tui.key(KeyCode::Down);
    tui.key(KeyCode::Backspace);
    tui.keys("50");
    assert_shows(&tui.screen(), &["‹ LegionRevenant › *", "‹ False › *", "1050▏ *"]);
    // A transient option can't be set.
    tui.key(KeyCode::Down);
    tui.key(KeyCode::Char(' '));
    assert_shows(
        &tui.screen(),
        &["Run once                    ‹ False ›  (resets each start)"],
    );
    tui.key(KeyCode::Enter);
    tui.run_jobs();

    let started =
        json!(["Farm/AtlasGold.cs", { "Bank": "False", "Class": "LegionRevenant", "Gold:Stop": "1050" }, null, null]);
    assert_eq!(tui.alice.params_of("script_start"), vec![started.clone()]);
    assert_eq!(tui.bob.params_of("script_start"), vec![started]);
    assert_shows(&tui.screen(), &["Script started, run 4"]);
}

#[test]
fn a_script_that_doesnt_compile_shows_its_diagnostics_in_the_options_form() {
    let mut tui = Tui::with(
        Some((
            "script_options",
            COMPILE_FAILED,
            "Farm/AtlasGold.cs doesn't compile:\nAtlasGold.cs(12,5): error CS0103: The name 'Foo' does not exist",
        )),
        None,
    );
    tui.key(KeyCode::Char('s'));
    tui.run_jobs();
    tui.key(KeyCode::Enter);
    tui.run_jobs();

    assert_shows(
        &tui.screen(),
        &[
            "Farm/AtlasGold.cs doesn't compile:",
            "error CS0103: The name 'Foo' does not exist",
        ],
    );
    tui.key(KeyCode::Enter);
    assert!(tui.app.jobs.is_empty(), "nothing starts without options");
}

#[test]
fn starting_a_script_while_one_runs_shows_the_engines_refusal() {
    let mut tui = Tui::with(Some(("script_start", SCRIPT_RUNNING, "A Script is running.")), None);
    tui.key(KeyCode::Char('s'));
    tui.run_jobs();
    tui.key(KeyCode::Enter);
    tui.run_jobs();
    tui.key(KeyCode::Enter);
    tui.run_jobs();

    assert_eq!(
        tui.alice.params_of("script_start"),
        vec![json!(["Farm/AtlasGold.cs", {}, null, null])]
    );
    assert_shows(&tui.screen(), &["alice: A Script is running."]);
}

#[test]
fn stop_script_stops_each_marked_accounts_script() {
    let mut tui = Tui::new();
    tui.key(KeyCode::Char('a'));
    tui.key(KeyCode::Char('x'));
    tui.run_jobs();

    assert_eq!(tui.alice.params_of("script_stop"), vec![json!([])]);
    assert_eq!(tui.bob.params_of("script_stop"), vec![json!([])]);
    assert_shows(&tui.screen(), &["Script stopped"]);
}

#[test]
fn a_question_is_shown_with_its_choices_and_answered_with_a_number() {
    let mut tui = Tui::new();
    assert_shows(
        &tui.screen(),
        &[
            "Question · Inventory full · d to answer",
            "[ ] ● alice        AtlasGold    ?",
        ],
    );
    tui.key(KeyCode::Char('d'));
    tui.run_jobs();

    assert_eq!(tui.alice.params_of("dialogs"), vec![json!([])]);
    assert_shows(
        &tui.screen(),
        &[
            "Question · alice · Inventory full",
            "Your inventory is full. Bank the farm items?",
            "1  Yes",
            "2  Bank",
            "3  No",
            "from Farm/AtlasGold.cs",
            "1–9 or ↑↓ enter · esc later",
        ],
    );

    tui.key(KeyCode::Char('2'));
    tui.run_jobs();
    assert_eq!(tui.alice.params_of("dialog_answer"), vec![json!([7, "Bank"])]);
    assert_shows(&tui.screen(), &["alice: answered Question 7: Bank"]);
}

#[test]
fn an_answer_to_a_question_no_longer_pending_shows_the_engines_error() {
    let mut tui = Tui::with(
        Some(("dialog_answer", DIALOG_NOT_PENDING, "No Question 7 is pending.")),
        None,
    );
    tui.key(KeyCode::Char('d'));
    tui.run_jobs();
    tui.key(KeyCode::Enter);
    tui.run_jobs();

    assert_eq!(tui.alice.params_of("dialog_answer"), vec![json!([7, "Yes"])]);
    assert_shows(&tui.screen(), &["alice: No Question 7 is pending."]);
}

#[test]
fn join_moves_every_marked_account_to_the_map_cell_and_pad_typed() {
    let mut tui = Tui::new();
    tui.key(KeyCode::Char('a'));
    tui.key(KeyCode::Char('J'));
    assert_shows(&tui.screen(), &["Join a map", "map[-room] [cell] [pad]"]);
    tui.keys("battleon-9999 Enter Spawn");
    tui.key(KeyCode::Enter);
    tui.run_jobs();

    for engine in [&tui.alice, &tui.bob] {
        assert_eq!(
            engine.params_of("join"),
            vec![json!(["battleon-9999", "Enter", "Spawn", null])]
        );
    }
    assert_shows(&tui.screen(), &["joined battleon · Enter · Spawn"]);
}

#[test]
fn join_while_not_logged_in_shows_not_logged_in() {
    let mut tui = Tui::with(Some(("join", NOT_LOGGED_IN, "Not logged in.")), None);
    tui.key(KeyCode::Char('J'));
    tui.keys("battleon");
    tui.key(KeyCode::Enter);
    tui.run_jobs();

    assert_eq!(tui.alice.params_of("join"), vec![json!(["battleon", null, null, null])]);
    assert_shows(&tui.screen(), &["alice: Not logged in."]);
}

#[test]
fn update_scripts_runs_once_for_the_data_folder_and_shows_what_changed() {
    let mut tui = Tui::new();
    tui.key(KeyCode::Char('a'));
    tui.key(KeyCode::Char('U'));
    tui.run_jobs();

    assert_eq!(tui.alice.params_of("scripts_update"), vec![json!([])]);
    assert!(tui.bob.params_of("scripts_update").is_empty());
    assert_shows(
        &tui.screen(),
        &["Scripts updated (incremental): 15 downloaded, 3 new, 1 changed"],
    );
}

#[test]
fn a_busy_update_shows_busy() {
    let mut tui = Tui::with(Some(("scripts_update", BUSY, "Scripts are already updating.")), None);
    tui.key(KeyCode::Char('U'));
    tui.run_jobs();

    assert_shows(&tui.screen(), &["alice: Scripts are already updating."]);
}

#[test]
fn stop_engine_asks_first_and_sends_nothing_unless_confirmed() {
    let mut tui = Tui::new();
    tui.key(KeyCode::Char('a'));
    tui.key(KeyCode::Char('X'));

    assert_shows(
        &tui.screen(),
        &[
            "Confirm",
            "Stop the Engine of alice, bob? Its game closes.",
            "y yes · n no",
        ],
    );
    tui.key(KeyCode::Char('n'));
    tui.run_jobs();
    assert!(tui.app.modal.is_none());
    assert!(Tui::calls_but_reads(&tui.alice).is_empty() && Tui::calls_but_reads(&tui.bob).is_empty());

    tui.key(KeyCode::Char('X'));
    tui.key(KeyCode::Char('y'));
    tui.run_jobs();
    assert_eq!(tui.alice.params_of("shutdown_if_idle"), vec![json!([])]);
    assert_eq!(tui.bob.params_of("shutdown_if_idle"), vec![json!([])]);
    assert_eq!(tui.alice.params_of("shutdown"), Vec::<Value>::new());
    assert_shows(&tui.screen(), &["Engine stopping"]);
}

#[test]
fn an_engine_running_a_script_refuses_to_stop_and_says_how() {
    let mut tui = Tui::with(Some(("shutdown_if_idle", SCRIPT_RUNNING, "A Script is running.")), None);
    tui.key(KeyCode::Char('X'));
    tui.key(KeyCode::Char('y'));
    tui.run_jobs();

    assert_shows(
        &tui.screen(),
        &["alice: A Script is running. The Engine keeps running; x stops the Script first."],
    );
}

#[test]
fn an_engine_of_another_protocol_is_never_acted_on() {
    let mut tui = Tui::new();
    tui.select("dave");
    for key in ['X', 'E', 'O', 'x', 'L', 's', 'U'] {
        tui.key(KeyCode::Char(key));
        tui.key(KeyCode::Char('y'));
        tui.run_jobs();
        assert!(tui.app.modal.is_none(), "{key} opened a dialog");
    }

    assert!(tui.app.jobs.is_empty());
    assert_eq!(Tui::calls_but_reads(&tui.dave), Vec::<String>::new());
    assert_shows(
        &tui.screen(),
        &["dave: left alone: Protocol mismatch: Engine 'dave'", "! "],
    );
}

#[test]
fn start_engine_launches_skua_engine_for_the_account_as_skua_does_and_waits_for_its_hello() {
    let mut tui = Tui::new();
    let fake = tui.dir.path().join("skua-engine");
    // Records its arguments and environment, then lives until the test's Engine answers on the socket.
    std::fs::write(
        &fake,
        "#!/bin/sh\nprintf '%s\\n' \"$@\" \"$SKUA_DIR\" \"$SKUA_ENGINE_SOCKET\" > \"$SKUA_DIR/started\"\n\
         while [ ! -S \"$SKUA_ENGINE_SOCKET\" ]; do sleep 0.05; done\nsleep 1\n",
    )
    .unwrap();
    std::fs::set_permissions(&fake, std::fs::Permissions::from_mode(0o755)).unwrap();
    tui.runner.engine_executable = Some(fake);
    tui.select("carol");
    assert_shows(
        &tui.screen(),
        &[
            "carol is offline",
            "E      start a windowless Engine for it (skua-engine)",
        ],
    );

    tui.key(KeyCode::Char('E'));
    let dir = tui.dir.path().to_owned();
    let serve = std::thread::spawn(move || {
        while !dir.join("started").exists() {
            std::thread::sleep(Duration::from_millis(20));
        }
        FakeEngine::start(
            &dir,
            "carol",
            engine_of(PROTOCOL, "carol", status("carol", false, None)),
        )
    });
    tui.run_jobs();
    let _carol = serve.join().unwrap();

    let started = std::fs::read_to_string(tui.dir.path().join("started")).unwrap();
    let skua_dir = tui.dir.path().to_str().unwrap();
    assert_eq!(
        started.lines().collect::<Vec<_>>(),
        [
            "--name",
            "carol",
            "--detach",
            skua_dir,
            &format!("{skua_dir}/engines/carol.sock")
        ]
    );
    assert_shows(
        &tui.screen(),
        &["carol: Engine started (build 1.4.4.4+abc1234, pid 4242)"],
    );
}

#[test]
fn start_engine_reports_an_engine_that_exits_during_start() {
    let mut tui = Tui::new();
    let fake = tui.dir.path().join("skua-engine");
    std::fs::write(&fake, "#!/bin/sh\nexit 4\n").unwrap();
    std::fs::set_permissions(&fake, std::fs::Permissions::from_mode(0o755)).unwrap();
    tui.runner.engine_executable = Some(fake);
    tui.select("carol");
    tui.key(KeyCode::Char('E'));
    tui.run_jobs();

    assert_shows(
        &tui.screen(),
        &["carol: the Engine exited during start with exit status: 4; see"],
    );
}

#[test]
fn start_engine_leaves_running_engines_alone() {
    let mut tui = Tui::new();
    tui.key(KeyCode::Char('a'));
    tui.key(KeyCode::Char('E'));

    assert!(tui.app.jobs.is_empty());
    assert_shows(&tui.screen(), &["its Engine already runs"]);
}

#[test]
fn start_engine_never_launches_one_where_an_engine_is_starting() {
    let mut tui = Tui::new();
    // carol's Engine holds its lock but doesn't answer yet; the launcher would fail if it ran.
    let lock_path = tui.dir.path().join("engines/carol.lock");
    let lock = std::fs::File::create(&lock_path).unwrap();
    assert_eq!(
        unsafe { libc::flock(std::os::fd::AsRawFd::as_raw_fd(&lock), libc::LOCK_EX) },
        0
    );
    drop(UnixListener::bind(tui.dir.path().join("engines/carol.sock")).unwrap());
    tui.runner.start_timeout = Duration::from_millis(300);
    tui.refresh();
    tui.select("carol");
    tui.key(KeyCode::Char('E'));
    tui.run_jobs();

    assert_shows(&tui.screen(), &["carol: Engine 'carol' is starting or hung"]);
    drop(lock);
}

#[test]
fn the_command_palette_runs_an_action() {
    let mut tui = Tui::new();
    tui.key(KeyCode::Char(':'));
    tui.keys("log in");
    tui.key(KeyCode::Enter);

    assert!(matches!(tui.app.modal, Some(Modal::Servers { .. })));
    tui.run_jobs();
    assert_shows(&tui.screen(), &["Log in to", "Artix"]);
}

/// A fake `skua` that records how it was started, then holds the Hook Runner's lock as `skua hooks` does, until the data folder goes.
fn fake_skua(dir: &std::path::Path) -> std::path::PathBuf {
    let fake = dir.join("skua");
    std::fs::write(
        &fake,
        "#!/bin/sh\nprintf '%s\\n' \"$@\" \"$SKUA_DIR\" > \"$SKUA_DIR/started\"\n\
         exec perl -e 'use Fcntl qw(:flock); my $p = \"$ENV{SKUA_DIR}/hooks.lock\"; open(my $f, \">>\", $p) or die; \
         flock($f, LOCK_EX) or die; select(undef, undef, undef, 0.1) while -e $p;'\n",
    )
    .unwrap();
    std::fs::set_permissions(&fake, std::fs::Permissions::from_mode(0o755)).unwrap();
    fake
}

#[test]
fn start_hook_runner_launches_skua_hooks_for_the_data_folder_and_waits_for_its_lock() {
    let mut tui = Tui::new();
    tui.runner.skua_executable = Some(fake_skua(tui.dir.path()));
    assert_shows(&tui.screen(), &["hooks off"]);

    tui.key(KeyCode::Char(':'));
    tui.keys("hook runner");
    tui.key(KeyCode::Enter);
    tui.run_jobs();
    tui.refresh();

    let started = std::fs::read_to_string(tui.dir.path().join("started")).unwrap();
    assert_eq!(
        started.lines().collect::<Vec<_>>(),
        ["hooks", tui.dir.path().to_str().unwrap()]
    );
    assert_shows(&tui.screen(), &["the Hook Runner started", "hooks running"]);
    assert!(
        Tui::calls_but_reads(&tui.alice).is_empty(),
        "skua-tui runs no hook and calls no Engine for it"
    );

    tui.key(KeyCode::Char('H'));
    assert!(tui.app.jobs.is_empty());
    assert_shows(&tui.screen(), &["the Hook Runner already runs"]);
}

#[test]
fn start_hook_runner_reports_a_runner_that_exits_during_start() {
    let mut tui = Tui::new();
    let fake = tui.dir.path().join("skua");
    std::fs::write(
        &fake,
        "#!/bin/sh\necho 'skua: a Hook Runner already runs' >&2\nexit 1\n",
    )
    .unwrap();
    std::fs::set_permissions(&fake, std::fs::Permissions::from_mode(0o755)).unwrap();
    tui.runner.skua_executable = Some(fake);

    tui.key(KeyCode::Char('H'));
    tui.run_jobs();

    assert_shows(
        &tui.screen(),
        &["Hook Runner: skua hooks exited during start with exit status: 1; see"],
    );
    let log = std::fs::read_to_string(tui.dir.path().join("hooks.log")).unwrap();
    assert!(log.contains("already runs"), "{log}");
}
