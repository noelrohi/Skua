//! PROTOTYPE, throwaway: three Overview layouts that show the party: what each account attacks, its kills and deaths, and the monsters in
//! its cell. Run with `SKUA_TUI_PROTOTYPE=1`, open Overview and flip with ←/→. Question: what should a non-technical, party-aware
//! Overview look like? A thread polls every Engine every 2 s on its own connections; the target comes from `eval`, since no op says it.

use std::collections::{BTreeMap, HashMap};
use std::path::PathBuf;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::Duration;

use ratatui::Frame;
use ratatui::layout::Rect;
use ratatui::style::{Color, Style, Stylize};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Clear, Paragraph};
use serde_json::{Value, json};

use super::{
    DIM, Stall, chat_lines, doing, gauge, idle_color, panel, player_lines, rate_text, ratio, short_duration, stall, thousands,
    truncate,
};
use crate::discovery;
use crate::dto::{LogEntry, Map, Monster, Quests, Status};
use crate::engine::Engine;
use crate::poller::Detail;

pub const VARIANTS: [&str; 3] = ["Panels", "Party board", "Monsters first"];

static VARIANT: AtomicUsize = AtomicUsize::new(0);

#[derive(Debug, Clone, Default)]
pub struct Member {
    pub status: Option<Status>,
    pub map: Option<Map>,
    /// The monster the player targets, by its map ID; None without one.
    pub target: Option<i64>,
    pub quests: Option<Quests>,
    pub chat: Vec<LogEntry>,
}

fn party() -> &'static Mutex<BTreeMap<String, Member>> {
    static PARTY: OnceLock<Mutex<BTreeMap<String, Member>>> = OnceLock::new();
    PARTY.get_or_init(Mutex::default)
}

pub fn enabled() -> bool {
    std::env::var("SKUA_TUI_PROTOTYPE").is_ok_and(|v| v == "1")
}

pub fn cycle(by: isize) {
    let n = VARIANTS.len() as isize;
    let next = (VARIANT.load(Ordering::Relaxed) as isize + by).rem_euclid(n);
    VARIANT.store(next as usize, Ordering::Relaxed);
}

/// Polls every Engine of the data folder every 2 s into the party, on a thread of its own.
pub fn start(skua_dir: PathBuf) {
    std::thread::spawn(move || {
        let mut connections: HashMap<String, Engine> = HashMap::new();
        loop {
            for name in discovery::engine_names(&skua_dir) {
                if !connections.contains_key(&name) {
                    match Engine::connect(&discovery::socket_path(&skua_dir, &name), Duration::from_secs(5)) {
                        Ok(engine) => _ = connections.insert(name.clone(), engine),
                        Err(_) => continue,
                    }
                }
                let engine = connections.get_mut(&name).expect("connected above");
                let Ok(status) = engine.status() else {
                    connections.remove(&name);
                    continue;
                };
                let playing = status.game.player.is_some();
                let mut member = Member { status: Some(status), ..Member::default() };
                if playing {
                    member.map = engine.map().ok();
                    member.quests = engine.quests().ok();
                    member.chat = engine.logs_of("game", None, Some(40)).map(|p| p.entries).unwrap_or_default();
                    member.target = engine
                        .call::<Value>("eval", json!(["Bot.Player.Target?.MapID ?? 0", 5]))
                        .ok()
                        .and_then(|r| r.get("value")?.as_i64())
                        .filter(|id| *id > 0);
                }
                party().lock().unwrap().insert(name, member);
            }
            std::thread::sleep(Duration::from_secs(2));
        }
    });
}

/// The selected account and the others on its map, it first.
fn members(name: &str) -> Vec<(String, Member)> {
    let party = party().lock().unwrap();
    let Some(me) = party.get(name) else { return Vec::new() };
    let map = me.status.as_ref().and_then(|s| s.game.player.as_ref()).map(|p| p.map.clone());
    let mut members = vec![(name.to_owned(), me.clone())];
    members.extend(
        party
            .iter()
            .filter(|(n, m)| {
                n.as_str() != name && map.is_some() && m.status.as_ref().and_then(|s| s.game.player.as_ref()).map(|p| &p.map) == map.as_ref()
            })
            .map(|(n, m)| (n.clone(), m.clone())),
    );
    members
}

pub fn render(frame: &mut Frame, name: &str, status: &Status, detail: Option<&Detail>, area: Rect) {
    let members = members(name);
    match VARIANT.load(Ordering::Relaxed) {
        1 => party_board(frame, status, &members, area),
        2 => monsters_first(frame, status, detail, &members, area),
        _ => panels(frame, status, detail, &members, area),
    }
    switcher(frame, area);
}

fn switcher(frame: &mut Frame, area: Rect) {
    let i = VARIANT.load(Ordering::Relaxed);
    let text = format!(" ◀  {} · {}  ▶   ←/→ ", (b'A' + i as u8) as char, VARIANTS[i]);
    let w = text.chars().count() as u16;
    if area.width < w + 2 || area.height < 3 {
        return;
    }
    let pill = Rect { x: area.x + (area.width - w) / 2, y: area.y + area.height - 1, width: w, height: 1 };
    frame.render_widget(Clear, pill);
    frame.render_widget(Paragraph::new(text.black().on_yellow().bold()), pill);
}

fn rows(area: Rect, heights: &[u16]) -> Vec<Rect> {
    let mut y = area.y;
    heights
        .iter()
        .map(|h| {
            let h = (*h).min(area.bottom().saturating_sub(y));
            let r = Rect { y, height: h, ..area };
            y += h;
            r
        })
        .collect()
}

fn halves(area: Rect) -> (Rect, Rect) {
    let half = area.width / 2;
    (Rect { width: half, ..area }, Rect { x: area.x + half, width: area.width - half, ..area })
}

fn short_hp(n: i64) -> String {
    match n {
        n if n >= 1_000_000 => format!("{:.1}m", n as f64 / 1e6),
        n if n >= 10_000 => format!("{}k", n / 1000),
        n => n.to_string(),
    }
}

/// The run line, plain: how long, kills, rate, deaths.
fn run_summary(status: &Status) -> Line<'static> {
    match &status.script.run {
        Some(run) => Line::raw(format!(
            "running {} · {} kills · {}/min · {} deaths",
            short_duration(run.elapsed_sec),
            thousands(run.kills),
            run.kills_per_min.map(rate_text).unwrap_or_else(|| "—".into()),
            run.deaths
        )),
        None => Line::styled("no Script running", Style::new().fg(DIM)),
    }
}

fn quest_state(status: &Status) -> Line<'static> {
    let Some(run) = &status.script.run else { return Line::raw("") };
    match (stall(run), run.quest_idle_sec) {
        (Some(Stall::Stuck(s)), _) => Line::styled(format!("stuck: no quest progress or kills for {}", short_duration(s)), Style::new().red().bold()),
        (Some(Stall::Grinding(s)), _) => Line::styled(format!("grinding: no quest progress for {}, still killing", short_duration(s)), Style::new().yellow()),
        (None, Some(s)) => Line::styled(format!("progressing: last quest progress {} ago", short_duration(s)), Style::new().green()),
        (None, None) => Line::styled("no quest in progress", Style::new().fg(DIM)),
    }
}

fn quest_rows(quests: Option<&Quests>, running: bool, width: usize) -> Vec<Line<'static>> {
    let Some(q) = quests else { return vec![Line::styled("…", Style::new().fg(DIM))] };
    let mut lines: Vec<Line<'static>> = q
        .quests
        .iter()
        .filter(|q| q.status == "inProgress")
        .map(|q| {
            let done = q.requirements.iter().filter(|r| r.owned() >= r.qty).count();
            let idle = q.requirements.iter().filter_map(|r| r.idle_sec).reduce(f64::min);
            let name_w = width.saturating_sub(18);
            Line::from(vec![
                Span::raw(format!("{:<name_w$}", truncate(&q.name, name_w))),
                Span::styled(format!("{:>7}", format!("{done}/{}", q.requirements.len())), Style::new().fg(DIM)),
                Span::styled(
                    format!("{:>8}", idle.map(short_duration).unwrap_or_default()),
                    Style::new().fg(idle.map_or(DIM, |s| idle_color(s, running))),
                ),
            ])
        })
        .collect();
    if lines.is_empty() {
        lines.push(Line::styled("no quest in progress", Style::new().fg(DIM)));
    }
    lines
}

fn target_of<'a>(member: &Member, map: Option<&'a Map>) -> Option<&'a Monster> {
    let id = member.target?;
    map?.monsters.iter().find(|m| m.map_id == id)
}

fn class_of(member: &Member) -> String {
    member.status.as_ref().and_then(|s| s.game.player.as_ref()).and_then(|p| p.class.clone()).unwrap_or_default()
}

fn party_lines(members: &[(String, Member)], width: usize) -> Vec<Line<'static>> {
    let me_map = members.first().and_then(|(_, m)| m.map.as_ref());
    members
        .iter()
        .map(|(name, m)| {
            let run = m.status.as_ref().and_then(|s| s.script.run.as_ref());
            let target = target_of(m, me_map).map(|t| t.name.clone()).unwrap_or_else(|| "—".into());
            let tw = width.saturating_sub(12 + 10);
            Line::from(vec![
                Span::styled(format!("{:<12}", truncate(name, 11)), Style::new().bold()),
                Span::raw(format!("{:<tw$}", truncate(&format!("→ {target}"), tw))),
                Span::styled(
                    format!("{:>10}", run.map(|r| format!("{} kills", thousands(r.kills))).unwrap_or_default()),
                    Style::new().fg(DIM),
                ),
            ])
        })
        .collect()
}

fn chat_panel(frame: &mut Frame, members: &[(String, Member)], area: Rect) {
    let width = area.width.saturating_sub(2) as usize;
    let chat = members.first().map(|(_, m)| m.chat.clone()).unwrap_or_default();
    let mut lines: Vec<Line> = chat.iter().flat_map(|e| chat_lines(e, width)).collect();
    if lines.is_empty() {
        lines.push(Line::styled("no chat yet", Style::new().fg(DIM)));
    }
    let skip = lines.len().saturating_sub(area.height.saturating_sub(2) as usize);
    frame.render_widget(Paragraph::new(lines.split_off(skip)).block(panel("Chat")), area);
}

/// A: panels, as planned: Player | Script, Quests | Party, then Chat.
fn panels(frame: &mut Frame, status: &Status, detail: Option<&Detail>, members: &[(String, Member)], area: Rect) {
    let parts = rows(area, &[8, 2 + members.len().max(3) as u16, area.height]);
    let (player, script) = halves(parts[0]);
    if let Some(p) = status.game.player.as_ref() {
        frame.render_widget(Paragraph::new(player_lines(p, status, player.width.saturating_sub(4))).block(panel("Player")), player);
    }
    let mut lines = vec![
        Line::styled(
            status.script.run.as_ref().map(|r| r.script.rsplit('/').next().unwrap_or("").trim_end_matches(".cs").to_owned()).unwrap_or_default(),
            Style::new().magenta().bold(),
        ),
        run_summary(status),
        quest_state(status),
    ];
    if let Some(run) = &status.script.run {
        lines.push(doing(run, detail));
    }
    frame.render_widget(Paragraph::new(lines).block(panel("Script")), script);
    let (quests, party) = halves(parts[1]);
    let running = status.script.run.is_some();
    let me = members.first().map(|(_, m)| m);
    frame.render_widget(
        Paragraph::new(quest_rows(me.and_then(|m| m.quests.as_ref()), running, quests.width.saturating_sub(2) as usize)).block(panel("Quests")),
        quests,
    );
    frame.render_widget(Paragraph::new(party_lines(members, party.width.saturating_sub(2) as usize)).block(panel("Party")), party);
    chat_panel(frame, members, parts[2]);
}

/// B: the party as a table (HP, target, kills, rate, deaths), the selected account's cell's monsters with who attacks each, then Chat.
fn party_board(frame: &mut Frame, status: &Status, members: &[(String, Member)], area: Rect) {
    let width = area.width.saturating_sub(2) as usize;
    let me_map = members.first().and_then(|(_, m)| m.map.as_ref());
    let mut lines = vec![
        Line::from(vec![quest_state(status).spans, vec![Span::raw("   ")], run_summary(status).spans].concat()),
        Line::raw(""),
        Line::styled(
            format!("{:<12}{:<20}{:<22}{:<26}{:>8}{:>8}{:>8}", "ACCOUNT", "CLASS", "HP", "TARGET", "KILLS", "/MIN", "DEATHS"),
            Style::new().fg(DIM).bold(),
        ),
    ];
    for (name, m) in members {
        let player = m.status.as_ref().and_then(|s| s.game.player.as_ref());
        let run = m.status.as_ref().and_then(|s| s.script.run.as_ref());
        let hp = player.map(|p| ratio(p.hp, p.max_hp)).unwrap_or(0.0);
        let target = target_of(m, me_map).map(|t| t.name.clone()).unwrap_or_else(|| "—".into());
        let mut spans = vec![
            Span::styled(format!("{:<12}", truncate(name, 11)), Style::new().bold()),
            Span::styled(format!("{:<20}", truncate(&class_of(m), 19)), Style::new().fg(DIM)),
        ];
        spans.extend(gauge("", 20, hp, if hp > 0.5 { Color::Green } else if hp > 0.25 { Color::Yellow } else { Color::Red }, player.map(|p| short_hp(p.hp)).unwrap_or_default()).spans);
        spans.push(Span::raw(format!(" {:<26}", truncate(&target, 25))));
        spans.push(Span::raw(format!("{:>8}", run.map(|r| thousands(r.kills)).unwrap_or_default())));
        spans.push(Span::styled(format!("{:>8}", run.and_then(|r| r.kills_per_min).map(rate_text).unwrap_or_default()), Style::new().fg(DIM)));
        let deaths = run.map(|r| r.deaths).unwrap_or(0);
        spans.push(Span::styled(format!("{:>8}", deaths), if deaths > 0 { Style::new().red() } else { Style::new().fg(DIM) }));
        lines.push(Line::from(spans));
    }
    lines.push(Line::raw(""));
    let cell = status.game.player.as_ref().map(|p| p.cell.clone()).unwrap_or_default();
    lines.push(Line::styled(format!("MONSTERS IN {cell}"), Style::new().fg(DIM).bold()));
    lines.extend(monster_lines(members, me_map, &cell, width));
    let used = lines.len() as u16 + 2;
    let parts = rows(area, &[used, area.height]);
    frame.render_widget(Paragraph::new(lines).block(panel("Party")), parts[0]);
    chat_panel(frame, members, parts[1]);
}

/// The monsters in `cell`, each with an HP bar and who attacks it; dead ones dim.
fn monster_lines(members: &[(String, Member)], map: Option<&Map>, cell: &str, width: usize) -> Vec<Line<'static>> {
    let Some(map) = map else { return vec![Line::styled("…", Style::new().fg(DIM))] };
    let mut lines = Vec::new();
    for m in map.monsters.iter().filter(|m| m.cell == cell) {
        let attackers: Vec<&str> = members.iter().filter(|(_, mm)| mm.target == Some(m.map_id)).map(|(n, _)| n.as_str()).collect();
        if !m.alive {
            lines.push(Line::styled(format!("  {:<24} dead, respawning", truncate(&m.name, 23)), Style::new().fg(DIM)));
            continue;
        }
        let bar_w = width.saturating_sub(24 + 3 + 30).clamp(10, 30) as u16;
        let mut spans = vec![Span::raw(format!("  {:<24} ", truncate(&m.name, 23)))];
        spans.extend(gauge("", bar_w, ratio(m.hp, m.max_hp), Color::Red, format!("{}/{}", short_hp(m.hp), short_hp(m.max_hp))).spans);
        spans.push(if attackers.is_empty() {
            Span::styled("  no one on it", Style::new().fg(DIM))
        } else {
            Span::styled(format!("  ◀ {}", attackers.join(", ")), Style::new().yellow())
        });
        lines.push(Line::from(spans));
    }
    if lines.is_empty() {
        lines.push(Line::styled("  no monsters here", Style::new().fg(DIM)));
    }
    lines
}

/// C: one plain status line, then every cell the party is in with its monsters and who is on each, then Chat.
fn monsters_first(frame: &mut Frame, status: &Status, detail: Option<&Detail>, members: &[(String, Member)], area: Rect) {
    let width = area.width.saturating_sub(2) as usize;
    let me_map = members.first().and_then(|(_, m)| m.map.as_ref());
    let player = status.game.player.as_ref();
    let mut lines = vec![
        Line::from(vec![
            Span::styled(player.map(|p| p.name.clone()).unwrap_or_default(), Style::new().bold()),
            Span::styled(
                player.map(|p| format!("  {} · HP {}/{} · {} {}", p.class.clone().unwrap_or_default(), p.hp, p.max_hp, p.map, p.cell)).unwrap_or_default(),
                Style::new().fg(DIM),
            ),
        ]),
        Line::from(vec![run_summary(status).spans, vec![Span::raw("   ")], quest_state(status).spans].concat()),
    ];
    if let Some(run) = &status.script.run {
        lines.push(doing(run, detail));
    }
    let mut cells: Vec<String> = Vec::new();
    for (_, m) in members {
        if let Some(c) = m.status.as_ref().and_then(|s| s.game.player.as_ref()).map(|p| p.cell.clone())
            && !cells.contains(&c)
        {
            cells.push(c);
        }
    }
    for cell in cells {
        let here: Vec<String> = members
            .iter()
            .filter(|(_, m)| m.status.as_ref().and_then(|s| s.game.player.as_ref()).is_some_and(|p| p.cell == cell))
            .map(|(n, m)| {
                let run = m.status.as_ref().and_then(|s| s.script.run.as_ref());
                format!("{n} ({}, {} kills)", class_of(m), run.map(|r| r.kills).unwrap_or(0))
            })
            .collect();
        lines.push(Line::raw(""));
        lines.push(Line::from(vec![format!("{cell}  ").bold(), Span::styled(here.join(" · "), Style::new().fg(DIM))]));
        lines.extend(monster_lines(members, me_map, &cell, width));
    }
    let used = lines.len() as u16 + 2;
    let parts = rows(area, &[used, area.height]);
    frame.render_widget(Paragraph::new(lines).block(panel("Overview")), parts[0]);
    chat_panel(frame, members, parts[1]);
}
