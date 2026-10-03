mod common;

use std::time::Duration;

use common::*;
use ratatui::Terminal;
use ratatui::backend::TestBackend;
use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyModifiers, MouseButton, MouseEvent, MouseEventKind};
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
            Ok(status)
        }
        "logs" if params[0] == "events" => Ok(json!({
            "entries": [
                event(2, "inventory.full", json!({ "used": 120, "slots": 120, "drop": null })),
                event(3, "hook.ran", json!({ "hook": "player.death", "eventSeq": 1, "startedAt": 1_791_036_000_000i64,
                    "durationMs": 1200, "exitCode": 0, "output": "respawned\n" })),
                event(4, "hook.ran", json!({ "hook": "inventory.full", "eventSeq": 2, "startedAt": 1_791_036_002_000i64,
                    "durationMs": 300, "exitCode": 2, "output": "banking…\nbank full\n" })),
            ],
            "next": "c4", "gap": false
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
            "3 Engines · 2 alerts · hooks off · protocol 17",
            "Accounts",
            "▾ Farm · 2",
            "▾ Butler · 1",
            "▾ Ungrouped · 1",
            "▾ Other Engines · 1",
            "[ ] ● alice        AtlasGold",
            " 11m▲",
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
            "quests stalled 11m",
            " ago · Farming Atlas Gold",
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
                "this skua-tui speaks 17. Nothing it reports is shown.",
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
fn the_tabs_show_inventory_quests_logs_and_the_map_with_the_picture_not_yet() {
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
            "The game's picture: not yet",
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
            "such as inventory.full. The Hook Runner runs it",
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
            "run 3 · 12m 34s · 312 kills · 5.4/min",
            "no quest progress 11m · still killing",
        ],
    );
    let row = screen.lines().find(|l| l.contains("● carol")).unwrap();
    assert!(
        row.contains("RareDrop") && row.contains("11m") && !row.contains('▲'),
        "{row}"
    );
}
