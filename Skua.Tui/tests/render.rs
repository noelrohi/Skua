mod common;

use std::time::Duration;

use common::*;
use ratatui::Terminal;
use ratatui::backend::TestBackend;
use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
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

/// A fleet in a temp data folder: alice farms with a Script, bob's Engine is of another protocol, carol has no Engine, and `default` is no
/// account's.
struct Fleet {
    dir: tempfile::TempDir,
    _engines: Vec<FakeEngine>,
}

fn fleet() -> Fleet {
    let dir = skua_dir();
    write_manager_file(dir.path(), manager_file());
    let alice = FakeEngine::start(dir.path(), "alice", |method, params| match method {
        "hello" => Ok(hello(PROTOCOL, "alice")),
        "status" => Ok(status("alice", true, Some("Farm/AtlasGold.cs"))),
        "logs" if params[1].is_null() => Ok(json!({
            "entries": [
                log_entry(1, "Farming Atlas Gold"),
                event(2, "inventory.full", json!({ "used": 120, "slots": 120, "drop": null })),
            ],
            "next": "c2", "gap": false
        })),
        "logs" => Ok(json!({ "entries": [], "next": "c2", "gap": false })),
        "inventory" => Ok(
            json!({ "kind": "inventory", "usedSlots": 2, "totalSlots": 120, "items": [
                { "id": 1, "name": "Atlas Gold", "qty": 870, "maxStack": 1000, "category": "Item", "equipped": false, "enhancementLevel": 0 },
                { "id": 2, "name": "Chaos Avenger", "qty": 1, "maxStack": 1, "category": "Class", "equipped": true, "enhancementLevel": 0 }
            ]}),
        ),
        "quests" => Ok(
            json!({ "filter": "loaded", "quests": [{ "id": 7551, "name": "Tainted Gem Exchange", "status": "inProgress",
            "memberOnly": false, "gold": 0, "xp": 0, "requirements": [{ "itemId": 9, "name": "Cubes", "qty": 25, "have": 21, "temp": false }], "rewards": [] }]}),
        ),
        "map" => Ok(json!({ "name": "battleon", "roomId": 9999, "cells": ["Enter", "r2"],
            "players": [{ "name": "alice", "level": 100, "cell": "Enter", "pad": "Spawn", "hp": 2400, "maxHp": 3000, "afk": false }],
            "monsters": [{ "id": 1, "mapId": 4, "name": "Frogzard", "cell": "r2", "hp": 0, "maxHp": 1000, "alive": false }] })),
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

    let screen = screen(&fleet, &mut app, 120, 32);

    assert_shows(
        &screen,
        &[
            " skua  windowless Engines",
            "3 Engines · 1 alert · protocol 12",
            "Accounts",
            "▾ Farm · 2",
            "▾ Butler · 1",
            "▾ Ungrouped · 1",
            "▾ Other Engines · 1",
            "[ ] ● alice        AtlasGold",
            "bob          protocol 11",
            "carol        offline",
            "default      login screen",
            " Overview │ Inventory │ Quests │ Logs │ Game │",
            "Player",
            "alice  Lv 100  Chaos Avenger  in combat",
            "2400/3000",
            "max level",
            "gold 1,234,567",
            "battleon · Enter · Spawn · Artix",
            "Script",
            "Farm/AtlasGold.cs",
            "run 3 · 12m 34s",
            "Logs",
            "Farming Atlas Gold",
            "inventory.full",
            "drop=null slots=120 used=120",
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
                "this skua-tui speaks 12. Nothing it reports is shown.",
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
            "start a windowless Engine for it (not yet)",
            "/carol",
        ],
    );
}

#[test]
fn the_tabs_show_inventory_quests_logs_and_the_map_with_the_picture_not_yet() {
    let fleet = fleet();
    let mut app = App::new(fleet.dir.path().to_owned());

    press(&mut app, KeyCode::Tab);
    assert_shows(
        &screen(&fleet, &mut app, 120, 32),
        &["2/120", "Atlas Gold", "870/1000", "Chaos Avenger ✓", "Class"],
    );
    press(&mut app, KeyCode::Tab);
    assert_shows(
        &screen(&fleet, &mut app, 120, 32),
        &[
            "Quests · loaded",
            "7551",
            "Tainted Gem Exchange",
            "in progress",
            "Cubes 21/25",
        ],
    );
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
            "The game's picture: not yet",
        ],
    );
}

#[test]
fn marks_and_the_command_palette_list_commands_whose_actions_come_next() {
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
            "[x] ● alice",
            "[x] ● bob",
            "Commands · on 2 accounts",
            "start Engine",
            "log in…",
            "start Script…",
            "answer Question",
        ],
    );

    for c in "stscr".chars() {
        press(&mut app, KeyCode::Char(c));
    }
    press(&mut app, KeyCode::Enter);
    let screen = screen(&fleet, &mut app, 120, 32);
    assert_shows(&screen, &["start Script on 2 accounts: not in skua-tui yet"]);
    assert!(app.modal.is_none());
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
            "start / stop Engine (not yet)",
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
