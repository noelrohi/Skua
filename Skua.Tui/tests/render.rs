mod common;

use std::time::Duration;

use common::*;
use ratatui::Terminal;
use ratatui::backend::TestBackend;
use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyModifiers, MouseButton, MouseEvent, MouseEventKind};
use ratatui::style::Color;
use serde_json::{Value, json};
use skua_tui::app::{App, Tab};
use skua_tui::engine::PROTOCOL;
use skua_tui::poller::Poller;

fn manager_file() -> Value {
    json!({
        "accounts": [
            { "name": "alice", "username": "alice", "displayName": "Alice", "tags": [] },
            { "name": "bob", "username": "bob", "displayName": "Bob", "tags": [] },
            { "name": "carol", "username": "carol", "displayName": "Carol", "tags": [] },
            { "name": "dave", "username": "dave", "displayName": "Dave", "tags": [] }
        ],
        "groups": [{ "name": "Farm", "usernames": ["alice", "bob"] }, { "name": "Butler", "usernames": ["carol"] }]
    })
}

/// alice's run 3 starting: 800 Atlas Gold, the Chaos Avenger, 2 Vouchers she has since spent, and the Relics as they are now.
fn run_started() -> Value {
    let mut items = vec![
        json!({ "id": 1, "name": "Atlas Gold", "qty": 800 }),
        json!({ "id": 2, "name": "Chaos Avenger", "qty": 1 }),
        json!({ "id": 50, "name": "Voucher of Nulgath", "qty": 2 }),
    ];
    items.extend((1..=40).map(|n| json!({ "id": 100 + n, "name": format!("Relic {n:02}"), "qty": n })));
    let mut started = event(
        1,
        "script.started",
        json!({ "run": 3, "script": "Farm/AtlasGold.cs", "restart": false,
        "inventory": items, "temp": [], "bank": null }),
    );
    started["run"] = json!(3);
    started
}

/// A fleet in a temp data folder: alice farms with a Script whose quests have stalled, bob's Engine is of another protocol, carol has no
/// Engine, and `default` is no account's.
struct Fleet {
    dir: tempfile::TempDir,
    _engines: Vec<FakeEngine>,
}

fn fleet() -> Fleet {
    let dir = skua_dir();
    write_manager_file(dir.path(), manager_file());
    let alice = FakeEngine::start(dir.path(), "alice", |method, params| match method {
        "hello" => Ok(hello(PROTOCOL, "alice")),
        "status" => {
            let mut status = status("alice", true, Some("Farm/AtlasGold.cs"));
            status["script"]["run"]["questIdleSec"] = json!(700.0);
            status["script"]["run"]["goal"] = json!({
                "quest": "Tainted Gem Exchange",
                "buy": { "item": "Atlas Crown", "want": 3, "have": 1, "perHour": null },
                "farm": { "item": "Atlas Gold", "want": 1000, "have": 870, "perHour": 120.0 },
                "now": "killing Frogzard for Atlas Gold",
                "resets": 2, "lastResetAt": "2026-10-02T14:10:00Z"
            });
            status["game"]["player"]["targetId"] = json!(5);
            Ok(status)
        }
        "logs" if params[0] == "events" => Ok(json!({
            "entries": [
                run_started(),
                event(2, "inventory.full", json!({ "used": 120, "slots": 120, "drop": null })),
                event(3, "hook.ran", json!({ "hook": "player.death", "eventSeq": 1, "startedAt": 1_791_036_000_000i64,
                    "durationMs": 1200, "exitCode": 0, "output": "respawned\n" })),
                event(4, "hook.ran", json!({ "hook": "inventory.full", "eventSeq": 2, "startedAt": 1_791_036_002_000i64,
                    "durationMs": 300, "exitCode": 2, "output": "banking…\nbank full\n" })),
            ],
            "next": "c4", "gap": false
        })),
        "logs" if params[0] == "game" => Ok(json!({
            "entries": [game_message(5, "zone", Some("Bob"), None, "anyone for ultra speaker?")],
            "next": "g5", "gap": false
        })),
        "logs" if params[1].is_null() => Ok(json!({
            "entries": [
                log_entry(1, "Farming Atlas Gold"),
                event(2, "inventory.full", json!({ "used": 120, "slots": 120, "drop": null })),
            ],
            "next": "c2", "gap": false
        })),
        "logs" => Ok(json!({ "entries": [], "next": "c2", "gap": false })),
        "inventory" => {
            let mut items = vec![
                json!({ "id": 1, "name": "Atlas Gold", "qty": 870, "maxStack": 1000, "category": "Item", "equipped": false, "enhancementLevel": 0 }),
                json!({ "id": 2, "name": "Chaos Avenger", "qty": 1, "maxStack": 1, "category": "Class", "equipped": true, "enhancementLevel": 0 }),
                json!({ "id": 3, "name": "Necrotic Sword of Doom", "qty": 1, "maxStack": 1, "category": "Sword", "equipped": false, "enhancementLevel": 0 }),
            ];
            items.extend((1..=40).map(|n| {
                json!({ "id": 100 + n, "name": format!("Relic {n:02}"), "qty": n, "maxStack": 99, "category": "Quest Item", "equipped": false, "enhancementLevel": null })
            }));
            Ok(json!({ "kind": "inventory", "usedSlots": 43, "totalSlots": 120, "items": items }))
        }
        "quests" => Ok(
            json!({ "filter": "loaded", "quests": [{ "id": 7551, "name": "Tainted Gem Exchange", "status": "inProgress",
            "memberOnly": false, "gold": 0, "xp": 0, "requirements": [
                { "itemId": 9, "name": "Cubes", "qty": 25, "have": 21, "temp": false, "idleSec": 840.0, "gainPerHour": 12.0 },
                { "itemId": 10, "name": "Gems", "qty": 10, "have": 2, "temp": false, "idleSec": 2820.0, "gainPerHour": 0.0, "inBank": 3 },
                { "itemId": 13, "name": "Crown", "qty": 1, "have": 0, "temp": false, "idleSec": 420.0, "gainPerHour": 8.0, "inBank": 1 },
                { "itemId": 11, "name": "Shards", "qty": 5, "have": 5, "temp": false, "idleSec": 30.0, "gainPerHour": 40.0 }
            ], "rewards": [] }, { "id": 7552, "name": "Gem Hoarder", "status": "notAccepted", "memberOnly": false, "gold": 0, "xp": 0,
            "requirements": [{ "itemId": 12, "name": "Rubies", "qty": 99, "have": 0, "temp": false, "idleSec": 840.0, "gainPerHour": null }],
            "rewards": [] }]}),
        ),
        "map" => Ok(json!({ "name": "battleon", "roomId": 9999, "cells": ["Enter", "r2"],
            "players": [{ "name": "alice", "level": 100, "cell": "Enter", "pad": "Spawn", "hp": 2400, "maxHp": 3000, "afk": false }],
            "monsters": [{ "id": 1, "mapId": 4, "name": "Frogzard", "cell": "r2", "hp": 0, "maxHp": 1000, "alive": false },
                { "id": 2, "mapId": 5, "name": "Hydra Crew", "cell": "Enter", "hp": 20000, "maxHp": 100000, "alive": true }] })),
        _ => Err((-32601, "no".into())),
    });
    let bob = FakeEngine::start(dir.path(), "bob", engine_of(11, "bob", status("bob", true, None)));
    let default = FakeEngine::start(
        dir.path(),
        "default",
        engine_of(PROTOCOL, "default", status("default", false, None)),
    );
    Fleet {
        dir,
        _engines: vec![alice, bob, default],
    }
}

/// Polls the fleet for the app's focus, twice so the selected Engine's tab is read, and draws the screen.
fn screen(fleet: &Fleet, app: &mut App, width: u16, height: u16) -> String {
    let mut poller = Poller::new(fleet.dir.path().to_owned(), Duration::from_secs(5));
    app.set_snapshot(poller.poll(&app.focus()));
    app.set_snapshot(poller.poll(&app.focus()));
    let mut terminal = Terminal::new(TestBackend::new(width, height)).unwrap();
    terminal.draw(|frame| skua_tui::ui::draw(frame, app)).unwrap();
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

/// The foreground colour of the cell at `x`, `y` as the screen draws now.
fn app_cell_fg(fleet: &Fleet, app: &mut App, width: u16, height: u16, x: u16, y: u16) -> Color {
    screen(fleet, app, width, height);
    let mut terminal = Terminal::new(TestBackend::new(width, height)).unwrap();
    terminal.draw(|frame| skua_tui::ui::draw(frame, app)).unwrap();
    terminal.backend().buffer()[(x, y)].fg
}

fn press(app: &mut App, code: KeyCode) {
    app.on_key(KeyEvent::new(code, KeyModifiers::NONE));
}

fn assert_shows(screen: &str, texts: &[&str]) {
    for text in texts {
        assert!(screen.contains(text), "expected {text:?} on screen:\n{screen}");
    }
}

#[test]
fn the_screen_shows_accounts_by_group_with_their_engines_and_the_selected_ones_overview() {
    let fleet = fleet();
    let mut app = App::new(fleet.dir.path().to_owned());

    let screen = screen(&fleet, &mut app, 160, 44);

    assert_shows(
        &screen,
        &[
            " skua  windowless Engines",
            "3 Engines · 2 alerts · hooks off · protocol 18",
            "Accounts",
            "▾ Farm · 2",
            "▾ Butler · 1",
            "▾ Ungrouped · 1",
            "▾ Other Engines · 1",
            "● alice      AtlasGo… 11m▲",
            "● bob        protocol 11",
            "● carol      offline",
            "● default    login screen",
            " Overview │ Inventory │ Quests │ Logs │ Game │",
            "Overview · battleon-9999 · Enter",
            "alice ─",
            "Lv 100",
            "battleon Enter · Artix",
            "2400/3000",
            "1,234,567 gold",
            "Current Quests",
            "Tainted Gem Exchange",
            "Cubes 21/25",
            "Gems 5/10",
            "Script",
            "AtlasGold  running 12m 34s · 0 kills · —/min · 0 deaths",
            "stuck · no quest progress or kills for 11m",
            "└ quest Tainted Gem Exchange",
            "└ buy   Atlas Crown",
            "1/3",
            "└ farm  Atlas Gold",
            "870/1000   +120/h  ~1h05m",
            "└ now   killing Frogzard for Atlas Gold",
            "⟲ wave reset 2× since this farm began",
            "alice  Chaos Avenger",
            "▶ Hydra Crew",
            "20k/100k",
            "◀ alice",
            "Chat",
            "[zone]",
            "anyone for ultra",
            "Room · 1 player(s) in battleon-9999",
            "Bags · run 3",
            "Atlas Gold",
            "870/1000",
            "+70",
            "★ new: Necrotic Sword of Doom",
            "spent or banked: Voucher of Nulgath -2",
            "acting on: alice",
            "? keys",
        ],
    );
    let rows: Vec<&str> = screen.lines().collect();
    let farm = rows.iter().position(|l| l.contains("▾ Farm")).unwrap();
    let butler = rows.iter().position(|l| l.contains("▾ Butler")).unwrap();
    let other = rows.iter().position(|l| l.contains("▾ Other Engines")).unwrap();
    assert!(farm < butler && butler < other);
}

#[test]
fn an_engine_of_another_protocol_fails_loudly_on_every_tab_and_shows_none_of_its_data() {
    let fleet = fleet();
    let mut app = App::new(fleet.dir.path().to_owned());
    screen(&fleet, &mut app, 120, 32);
    press(&mut app, KeyCode::Down);

    for tab in Tab::ALL {
        app.tab = tab;
        let screen = screen(&fleet, &mut app, 120, 32);
        assert_shows(
            &screen,
            &[
                "Protocol mismatch",
                "Engine 'bob'",
                "speaks protocol 11, but",
                "skua-tui speaks 18. Nothing it reports is shown.",
            ],
        );
        assert!(!screen.contains("Player") && !screen.contains("Artix"), "{screen}");
    }
}

#[test]
fn an_account_without_an_engine_says_so() {
    let fleet = fleet();
    let mut app = App::new(fleet.dir.path().to_owned());
    screen(&fleet, &mut app, 120, 32);
    press(&mut app, KeyCode::Char('/'));
    for c in "carol".chars() {
        press(&mut app, KeyCode::Char(c));
    }
    press(&mut app, KeyCode::Enter);

    let screen = screen(&fleet, &mut app, 120, 32);

    assert_shows(
        &screen,
        &[
            "No Engine",
            "carol is offline: no Engine runs for it.",
            "start a windowless Engine for it (skua-engine)",
            "/carol",
        ],
    );
}

#[test]
fn the_tabs_show_inventory_quests_logs_and_the_map_with_the_picture() {
    let fleet = fleet();
    let mut app = App::new(fleet.dir.path().to_owned());

    press(&mut app, KeyCode::Tab);
    assert_shows(
        &screen(&fleet, &mut app, 120, 32),
        &["43/120", "Atlas Gold", "870/1000", "Chaos Avenger ✓", "Class"],
    );
    press(&mut app, KeyCode::Tab);
    // Only the quest in progress, with its unmet requirements in columns; the quest that isn't accepted is only counted.
    let quests = screen(&fleet, &mut app, 120, 32);
    let row = |text: &str| -> String {
        let line = quests
            .lines()
            .find(|l| l.contains(text))
            .unwrap_or_else(|| panic!("no {text:?} in:\n{quests}"));
        line.split_whitespace().collect::<Vec<_>>().join(" ")
    };
    assert!(
        row("Tainted Gem Exchange").contains("Tainted Gem Exchange 2/4 done 30s"),
        "{quests}"
    );
    assert!(row("Cubes").contains("Cubes 21/25 +12/h ~20m 14m"), "{quests}");
    assert!(row("Gems ").contains("Gems 5/10 3 in bank 47m"), "{quests}");
    assert_shows(&quests, &[" Quests ", "1 not accepted"]);
    for hidden in ["Shards", "Crown", "Gem Hoarder", "Rubies", "7551"] {
        assert!(!quests.contains(hidden), "{hidden:?} shows:\n{quests}");
    }
    press(&mut app, KeyCode::Tab);
    assert_shows(
        &screen(&fleet, &mut app, 120, 32),
        &["Logs + events", "logs --tail 200, following", "Farming Atlas Gold"],
    );
    press(&mut app, KeyCode::Tab);
    assert_shows(
        &screen(&fleet, &mut app, 120, 32),
        &[
            "Game · battleon · room 9999",
            "cells: Enter, r2",
            "Frogzard",
            "p opens it in Preview",
        ],
    );
}

#[test]
fn the_hooks_tab_shows_the_hook_runner_and_the_engines_hook_runs_newest_first() {
    let fleet = fleet();
    let mut app = App::new(fleet.dir.path().to_owned());
    press(&mut app, KeyCode::Char('7'));

    let off = screen(&fleet, &mut app, 120, 32);
    assert_shows(
        &off,
        &[
            "hooks off · protocol",
            " Hooks │",
            "skua hooks: not running · H starts it",
            "STARTED  HOOK               EXIT     TOOK  OUTPUT",
            "inventory.full     2        0.3s  bank full",
            "player.death       0        1.2s  respawned",
            "Output · inventory.full",
            "banking…",
        ],
    );
    assert!(off.find("inventory.full     2").unwrap() < off.find("player.death       0").unwrap());

    // A Hook Runner holds its lock as .NET does.
    let lock = std::fs::File::create(fleet.dir.path().join("hooks.lock")).unwrap();
    assert_eq!(
        unsafe { libc::flock(std::os::fd::AsRawFd::as_raw_fd(&lock), libc::LOCK_EX) },
        0
    );
    assert_shows(
        &screen(&fleet, &mut app, 120, 32),
        &["hooks running · protocol", "skua hooks: running"],
    );

    while app.selected_row().unwrap().name != "default" {
        press(&mut app, KeyCode::Down);
    }
    assert_shows(
        &screen(&fleet, &mut app, 120, 32),
        &[
            "No hook has run for default yet.",
            "named after an event type,",
            "inventory.full. The Hook Runner runs it",
        ],
    );
}

#[test]
fn marks_and_the_command_palette_list_commands_and_run_them_on_the_marked_accounts() {
    let fleet = fleet();
    let mut app = App::new(fleet.dir.path().to_owned());
    screen(&fleet, &mut app, 120, 32);
    press(&mut app, KeyCode::Char('a'));
    press(&mut app, KeyCode::Char(':'));

    let palette = screen(&fleet, &mut app, 120, 32);
    assert_shows(
        &palette,
        &[
            "2 marked",
            "Commands · on 2 accounts",
            "start Engine",
            "log in…",
            "start Script…",
            "answer Question",
        ],
    );

    for name in ["alice", "bob"] {
        let (x, y) = find(&palette, &format!("● {name}"));
        assert_eq!(
            app_cell_fg(&fleet, &mut app, 120, 32, x + 2, y),
            Color::Blue,
            "{name} is marked"
        );
    }
    for c in "stscr".chars() {
        press(&mut app, KeyCode::Char(c));
    }
    press(&mut app, KeyCode::Enter);
    let screen = screen(&fleet, &mut app, 120, 32);
    assert_shows(&screen, &["Start a Script", "on 2 accounts", "reading from alice…"]);
    assert_eq!(app.jobs.len(), 1, "one Engine searches the Scripts");
}

#[test]
fn the_keys_help_lists_every_key() {
    let fleet = fleet();
    let mut app = App::new(fleet.dir.path().to_owned());
    press(&mut app, KeyCode::Char('?'));

    let screen = screen(&fleet, &mut app, 120, 32);

    assert_shows(
        &screen,
        &[
            "Keys",
            "mark its whole group",
            "command palette",
            "start / stop Engine (stop asks first)",
            "log in (server picker) / log out",
            "any key closes",
        ],
    );
}

#[test]
fn the_selection_stays_on_its_account_when_an_engine_appears_above_it() {
    let fleet = fleet();
    let mut app = App::new(fleet.dir.path().to_owned());
    screen(&fleet, &mut app, 120, 32);
    press(&mut app, KeyCode::Char('/'));
    press(&mut app, KeyCode::Char('d'));
    press(&mut app, KeyCode::Enter);
    press(&mut app, KeyCode::Down);
    assert_eq!(app.selected_row().unwrap().name, "default");

    let _aaron = FakeEngine::start(
        fleet.dir.path(),
        "aaron-d",
        engine_of(PROTOCOL, "aaron-d", status("aaron-d", false, None)),
    );
    screen(&fleet, &mut app, 120, 32);

    assert_eq!(app.selected_row().unwrap().name, "default");
}

/// Where `text` first shows on the screen, as a column and row.
fn find(screen: &str, text: &str) -> (u16, u16) {
    for (y, line) in screen.lines().enumerate() {
        if let Some(i) = line.find(text) {
            return (line[..i].chars().count() as u16, y as u16);
        }
    }
    panic!("no {text:?} on screen:\n{screen}");
}

fn mouse(app: &mut App, kind: MouseEventKind, (column, row): (u16, u16)) {
    app.on_mouse(MouseEvent {
        kind,
        column,
        row,
        modifiers: KeyModifiers::NONE,
    });
}

#[test]
fn the_inventory_splits_into_categories_scrolls_and_takes_clicks() {
    let fleet = fleet();
    let mut app = App::new(fleet.dir.path().to_owned());
    app.tab = Tab::Inventory;

    let all = screen(&fleet, &mut app, 120, 32);
    assert_shows(
        &all,
        &[
            " All 43 ",
            " Weapons 1 ",
            " Classes 1 ",
            " Items 1 ",
            " Quest items 40 ",
            "1–23 of 43",
            "Atlas Gold",
        ],
    );
    assert!(!all.contains("Gear") && !all.contains("Relic 21"), "{all}");

    // ←/→ pick a category, and a click on one does too.
    press(&mut app, KeyCode::Right);
    let weapons = screen(&fleet, &mut app, 120, 32);
    assert_shows(&weapons, &["Necrotic Sword of Doom"]);
    assert!(
        !weapons.contains("Atlas Gold") && !weapons.contains("of 43"),
        "{weapons}"
    );
    mouse(
        &mut app,
        MouseEventKind::Down(MouseButton::Left),
        find(&weapons, "Quest items 40"),
    );
    let relics = screen(&fleet, &mut app, 120, 32);
    assert_shows(&relics, &["Relic 01", "1–23 of 40"]);

    // The wheel scrolls the list; page down and End too; a click on it puts j/k on it, and esc gives them back to the accounts.
    mouse(&mut app, MouseEventKind::ScrollDown, find(&relics, "Relic 05"));
    assert_shows(&screen(&fleet, &mut app, 120, 32), &["4–26 of 40", "Relic 04"]);
    press(&mut app, KeyCode::End);
    assert_shows(&screen(&fleet, &mut app, 120, 32), &["18–40 of 40", "Relic 40"]);
    press(&mut app, KeyCode::Home);
    let top = screen(&fleet, &mut app, 120, 32);
    mouse(
        &mut app,
        MouseEventKind::Down(MouseButton::Left),
        find(&top, "Relic 03"),
    );
    press(&mut app, KeyCode::Char('j'));
    assert_shows(&screen(&fleet, &mut app, 120, 32), &["2–24 of 40", "acting on: alice"]);
    press(&mut app, KeyCode::Esc);
    press(&mut app, KeyCode::Char('j'));
    assert_shows(&screen(&fleet, &mut app, 120, 32), &["acting on: bob"]);

    // A click on an account picks it, and one on a tab opens it.
    let screen_now = screen(&fleet, &mut app, 120, 32);
    mouse(
        &mut app,
        MouseEventKind::Down(MouseButton::Left),
        find(&screen_now, "alice   "),
    );
    mouse(
        &mut app,
        MouseEventKind::Down(MouseButton::Left),
        find(&screen_now, " Quests "),
    );
    assert_shows(
        &screen(&fleet, &mut app, 120, 32),
        &["acting on: alice", "Tainted Gem Exchange"],
    );
}

#[test]
fn an_account_in_another_cell_of_the_map_is_listed_with_its_cell_and_its_run() {
    let fleet = fleet();
    let mut elsewhere = status("dave", true, Some("Farm/Other.cs"));
    elsewhere["game"]["player"]["cell"] = json!("r2");
    elsewhere["script"]["run"]["kills"] = json!(40);
    elsewhere["script"]["run"]["deaths"] = json!(2);
    let _dave = FakeEngine::start(fleet.dir.path(), "dave", engine_of(PROTOCOL, "dave", elsewhere));
    let mut app = App::new(fleet.dir.path().to_owned());

    let screen = screen(&fleet, &mut app, 120, 32);

    let rows: Vec<&str> = screen.lines().collect();
    let dave = rows
        .iter()
        .position(|l| l.contains("dave  Chaos Avenger · in r2"))
        .expect("dave's row");
    assert!(
        rows[dave + 1].contains("40 kills") && rows[dave + 1].contains("2 deaths"),
        "{screen}"
    );
    // dave targets nothing in alice's cell.
    assert!(!screen.contains("◀ alice, dave"), "{screen}");
}

#[test]
fn a_stall_while_still_killing_is_grinding_not_an_alert() {
    let fleet = fleet();
    let mut grinding = status("carol", true, Some("Farm/RareDrop.cs"));
    grinding["script"]["run"]["questIdleSec"] = json!(700.0);
    grinding["script"]["run"]["kills"] = json!(312);
    grinding["script"]["run"]["killsPerMin"] = json!(5.4);
    let _carol = FakeEngine::start(fleet.dir.path(), "carol", engine_of(PROTOCOL, "carol", grinding));
    let mut app = App::new(fleet.dir.path().to_owned());
    app.filter = "carol".into();

    let screen = screen(&fleet, &mut app, 120, 32);

    // alice is stuck and bob speaks another protocol; carol only grinds.
    assert_shows(
        &screen,
        &[
            "4 Engines · 2 alerts",
            "RareDrop  running 12m 34s · 312 kills · 5.4/min · 0 deaths",
            "grinding · no quest progress for 11m, still killing",
        ],
    );
    // alice plays in carol's cell, so she is in carol's party, after carol, each with their own kills under their name.
    let rows: Vec<&str> = screen.lines().collect();
    let carol = rows
        .iter()
        .position(|l| l.contains("carol  Chaos Avenger"))
        .expect("carol's row");
    let alice = rows
        .iter()
        .position(|l| l.contains("alice  Chaos Avenger"))
        .expect("alice's row");
    assert!(carol < alice, "{screen}");
    assert!(rows[carol + 1].contains("312 kills · 5.4/min"), "{screen}");
    let row = screen.lines().find(|l| l.contains("● carol")).unwrap();
    assert!(
        row.contains("RareDr") && row.contains("11m") && !row.contains('▲'),
        "{row}"
    );
}
