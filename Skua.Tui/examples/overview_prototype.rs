//! PROTOTYPE, throw me away. What should the Overview show, as the game's screen in text without its art? The Game tab, with the picture,
//! stays as it is.
//!
//! Three variants of the Overview inside the real TUI chrome (header, accounts, tabs, status line), driven by a fake fight modeled on a
//! Legion Revenant farming Dragon's Will in moonlab, with what the Overview shows today (the Script's run, whether it is stuck, its goal,
//! a Question) folded in. Nothing talks to an Engine.
//!
//!   cargo run --example overview_prototype -- [A|B|C]
//!
//! ←/→ cycle the variants, n marks what the Control Surface can't tell the TUI today (SP, class rank, skills and cooldowns, auras, combat
//! text), d shows a Question, q quits.

use std::collections::BTreeMap;
use std::io;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use ratatui::Frame;
use ratatui::crossterm::event::{self, Event, KeyCode, KeyEventKind};
use ratatui::layout::{Alignment, Constraint, Layout, Rect};
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, BorderType, Borders, Clear, Paragraph};
use serde_json::json;
use skua_tui::app::{App, Tab};
use skua_tui::discovery::ManagerAccounts;
use skua_tui::poller::{Detail, EngineView, Snapshot};

const DIM: Color = Color::DarkGray;
const TRACK: Color = Color::Indexed(237);
const DUMP: (u16, u16) = (150, 30);
const VARIANTS: [(&str, &str); 3] = [("A", "Screen"), ("B", "Columns"), ("C", "Feed")];

/// Whether `n` marks what the Control Surface lacks.
static LACK: AtomicBool = AtomicBool::new(false);
/// Whether `d` raised a Question.
static QUESTION: AtomicBool = AtomicBool::new(false);

fn lack(style: Style) -> Style {
    if LACK.load(Ordering::Relaxed) {
        style.bg(Color::Indexed(53)).add_modifier(Modifier::UNDERLINED)
    } else {
        style
    }
}

// ───────────────────────────────────────────── the fake fight ─────────────────────────────────────────────

const ME: &str = "ALT1";
const MY_CELL: &str = "Enter";
const MAP: &str = "moonlab-9399";
const QUEST: &str = "Dragon's Will";

struct Skill {
    key: u8,
    name: &'static str,
    cd: f64,
    left: f64,
    mana: i64,
}

struct Aura {
    name: &'static str,
    left: f64,
    max: f64,
    stacks: u32,
}

struct Mon {
    id: i64,
    name: &'static str,
    cell: &'static str,
    hp: i64,
    max: i64,
    respawn: f64,
}

struct Mate {
    name: &'static str,
    level: i64,
    class: &'static str,
    hp: i64,
    max: i64,
    cell: &'static str,
    target: Option<i64>,
    afk: bool,
    next: f64,
}

struct Req {
    name: &'static str,
    have: i64,
    qty: i64,
    per_hour: Option<f64>,
}

#[derive(Clone, Copy, PartialEq)]
enum Anchor {
    Me,
    Mon(i64),
}

struct Float {
    text: String,
    style: Style,
    age: f64,
    anchor: Anchor,
}

#[derive(Clone, Copy, PartialEq)]
enum Kind {
    Hit,
    Crit,
    Heal,
    Proc,
    Hurt,
    Kill,
    Quest,
    Chat(&'static str),
}

struct Entry {
    t: f64,
    kind: Kind,
    who: Option<&'static str>,
    text: String,
}

struct Sim {
    t: f64,
    rng: u64,
    hp: i64,
    max_hp: i64,
    mp: i64,
    max_mp: i64,
    sp: i64,
    gold: i64,
    xp: i64,
    xp_max: i64,
    kills: i64,
    skills: Vec<Skill>,
    auras: Vec<Aura>,
    target_auras: Vec<Aura>,
    mons: Vec<Mon>,
    mates: Vec<Mate>,
    target: Option<i64>,
    reqs: Vec<Req>,
    floats: Vec<Float>,
    feed: Vec<Entry>,
    toast: Option<(String, f64)>,
    next_action: f64,
    next_hurt: f64,
    next_chat: f64,
    chat_i: usize,
    /// When a quest requirement last rose.
    last_progress: f64,
}

const CHATTER: &[(&str, Option<&str>, &str)] = &[
    ("moderator", None, "isCompletedBefore: Dragons Will (7722) completion check"),
    ("moderator", None, "QuestProgression: Doing Quest: 7722 - Dragons Will"),
    ("warning", None, "Please slow down. Last action was too soon!"),
    ("server", None, "You joined \"moonlab-9399\""),
    ("moderator", None, "HuntMonster: Killing Slime Mold for item: Unyielding Slime"),
    ("zone", Some("alt4"), "anyone doing the ultra after this?"),
    ("party", Some("alt3"), "mana low, give me a sec"),
    ("warning", None, "Please slow down. Last action was too soon!"),
    ("whisper", Some("alt5"), "nice gear!"),
    ("moderator", None, "HuntMonster: Killing Omnipotent Cell for item: Omnipotent Cells"),
];

impl Sim {
    fn new() -> Sim {
        let aura = |name, left, max, stacks| Aura { name, left, max, stacks };
        let mon = |id, name, cell, hp, max| Mon { id, name, cell, hp, max, respawn: 0.0 };
        let mut sim = Sim {
            t: 0.0,
            rng: 0x2545_f491_4f6c_dd1d,
            hp: 3070,
            max_hp: 3070,
            mp: 85,
            max_mp: 100,
            sp: 100,
            gold: 100_525_185,
            xp: 0,
            xp_max: 0,
            kills: 0,
            skills: vec![
                Skill { key: 1, name: "Auto Attack", cd: 2.0, left: 0.0, mana: 0 },
                Skill { key: 2, name: "Soul Rend", cd: 3.0, left: 0.0, mana: 15 },
                Skill { key: 3, name: "Arcane Shield", cd: 9.0, left: 0.7, mana: 20 },
                Skill { key: 4, name: "Clarity", cd: 7.0, left: 1.9, mana: 10 },
                Skill { key: 5, name: "Revenant's Wrath", cd: 12.0, left: 0.0, mana: 30 },
                Skill { key: 6, name: "", cd: 0.0, left: 0.0, mana: 0 },
            ],
            auras: vec![aura("Arcane Shield", 6.0, 8.0, 1), aura("Depravity", 9.0, 10.0, 2)],
            target_auras: vec![aura("Soul Rot", 4.0, 6.0, 1)],
            mons: vec![
                mon(1, "Slime Mold", MY_CELL, 2100, 3000),
                mon(2, "Slime Mold", MY_CELL, 3000, 3000),
                mon(3, "Slime Mold", MY_CELL, 800, 3000),
                mon(4, "Omnipotent Cell", MY_CELL, 4500, 4500),
                mon(5, "Dragon Plasma", "r2", 3800, 3800),
                mon(6, "Dragon Plasma", "r2", 3800, 3800),
                mon(7, "Chaotic Invertebrae", "r3", 5200, 5200),
                mon(8, "Ultra Slime Mother", "r4", 250_000, 250_000),
            ],
            mates: vec![
                Mate { name: "alt2", level: 100, class: "ArchMage", hp: 2600, max: 2600, cell: MY_CELL, target: Some(2), afk: false, next: 0.5 },
                Mate { name: "alt3", level: 98, class: "Lord Of Order", hp: 2950, max: 2950, cell: MY_CELL, target: Some(1), afk: false, next: 1.1 },
                Mate { name: "alt4", level: 82, class: "Void Highlord", hp: 2200, max: 2200, cell: "r2", target: None, afk: true, next: 99.0 },
            ],
            target: Some(1),
            reqs: vec![
                Req { name: "Unyielding Slime", have: 293, qty: 300, per_hour: Some(410.0) },
                Req { name: "Omnipotent Cells", have: 0, qty: 20, per_hour: None },
                Req { name: "Dragon's Plasma", have: 0, qty: 20, per_hour: None },
                Req { name: "Chaotic Invertebrae", have: 0, qty: 20, per_hour: None },
            ],
            floats: vec![],
            feed: vec![],
            toast: Some(("Dragon's Will: Unyielding Slime 293/300".into(), 0.0)),
            next_action: 0.3,
            next_hurt: 1.0,
            next_chat: 0.5,
            chat_i: 0,
            last_progress: -12.0,
        };
        sim.xp = 1_840_200;
        sim.xp_max = 2_970_000;
        for _ in 0..6 {
            sim.chat();
        }
        sim
    }

    fn rand(&mut self, lo: i64, hi: i64) -> i64 {
        self.rng ^= self.rng << 13;
        self.rng ^= self.rng >> 7;
        self.rng ^= self.rng << 17;
        lo + (self.rng % (hi - lo + 1) as u64) as i64
    }

    fn chance(&mut self, percent: i64) -> bool {
        self.rand(1, 100) <= percent
    }

    fn log(&mut self, kind: Kind, who: Option<&'static str>, text: String) {
        self.feed.push(Entry { t: self.t, kind, who, text });
        if self.feed.len() > 400 {
            self.feed.remove(0);
        }
    }

    fn float(&mut self, anchor: Anchor, text: String, style: Style) {
        self.floats.push(Float { text, style, age: 0.0, anchor });
    }

    fn chat(&mut self) {
        let (channel, from, text) = CHATTER[self.chat_i % CHATTER.len()];
        self.chat_i += 1;
        self.log(Kind::Chat(channel), from, text.to_owned());
    }

    fn mon(&mut self, id: i64) -> Option<&mut Mon> {
        self.mons.iter_mut().find(|m| m.id == id)
    }

    fn proc_aura(&mut self, name: &'static str) {
        match self.auras.iter_mut().find(|a| a.name == name) {
            Some(a) => {
                a.left = a.max;
                a.stacks = (a.stacks + 1).min(3);
            }
            None => self.auras.push(Aura { name, left: 10.0, max: 10.0, stacks: 1 }),
        }
        self.float(Anchor::Me, format!("{name}!"), Style::new().light_green().bold());
        self.log(Kind::Proc, None, format!("{name}!"));
    }

    fn damage(&mut self, id: i64, amount: i64, by: Option<&'static str>) {
        let Some(mon) = self.mon(id) else { return };
        if mon.hp <= 0 {
            return;
        }
        mon.hp -= amount;
        if mon.hp > 0 {
            return;
        }
        mon.hp = 0;
        mon.respawn = 4.0;
        let name = mon.name;
        self.log(Kind::Kill, by, format!("{name} dies"));
        if by.is_some() {
            return;
        }
        self.kills += 1;
        let gold = self.rand(900, 1500);
        self.gold += gold;
        self.xp += 6_200;
        let drop = match name {
            "Slime Mold" => Some(0),
            "Omnipotent Cell" if self.chance(60) => Some(1),
            _ => None,
        };
        if let Some(i) = drop
            && self.reqs[i].have < self.reqs[i].qty
        {
            self.reqs[i].have += 1;
            self.last_progress = self.t;
            let req = &self.reqs[i];
            let text = format!("{QUEST}: {} {}/{}", req.name, req.have, req.qty);
            self.toast = Some((text.clone(), 0.0));
            self.log(Kind::Quest, None, text);
        }
    }

    fn step(&mut self, dt: f64) {
        self.t += dt;
        for s in &mut self.skills {
            s.left = (s.left - dt).max(0.0);
        }
        for a in self.auras.iter_mut().chain(self.target_auras.iter_mut()) {
            a.left -= dt;
        }
        self.auras.retain(|a| a.left > 0.0);
        self.target_auras.retain(|a| a.left > 0.0);
        self.floats.iter_mut().for_each(|f| f.age += dt);
        self.floats.retain(|f| f.age < 2.4);
        if let Some((_, age)) = &mut self.toast {
            *age += dt;
            if *age > 4.0 {
                self.toast = None;
            }
        }
        for m in &mut self.mons {
            if m.respawn > 0.0 {
                m.respawn -= dt;
                if m.respawn <= 0.0 {
                    m.respawn = 0.0;
                    m.hp = m.max;
                }
            }
        }
        if self.chance(40) {
            self.mp = (self.mp + 1).min(self.max_mp);
        }

        // Keep a target in the cell.
        let alive = |m: &Mon| m.cell == MY_CELL && m.hp > 0;
        if !self.target.is_some_and(|id| self.mons.iter().any(|m| m.id == id && alive(m))) {
            self.target = self.mons.iter().find(|m| alive(m)).map(|m| m.id);
            self.target_auras.clear();
        }

        if self.t >= self.next_action
            && let Some(target) = self.target
        {
            self.next_action = self.t + 0.9;
            let pick = [5u8, 2, 3, 4, 1]
                .into_iter()
                .find(|k| self.skills.iter().any(|s| s.key == *k && s.left <= 0.0 && s.mana <= self.mp));
            if let Some(key) = pick {
                let s = self.skills.iter_mut().find(|s| s.key == key).unwrap();
                s.left = s.cd;
                self.mp -= s.mana;
                let name = s.name;
                match key {
                    3 => {
                        self.auras.retain(|a| a.name != "Arcane Shield");
                        self.auras.push(Aura { name: "Arcane Shield", left: 8.0, max: 8.0, stacks: 1 });
                        self.float(Anchor::Me, "Arcane Shield!".into(), Style::new().light_green().bold());
                        self.log(Kind::Proc, None, "Arcane Shield!".into());
                    }
                    4 => {
                        self.auras.retain(|a| a.name != "Clarity");
                        self.auras.push(Aura { name: "Clarity", left: 6.0, max: 6.0, stacks: 1 });
                        let heal = 388.min(self.max_hp - self.hp);
                        self.hp += heal;
                        self.float(Anchor::Me, "Clarity!".into(), Style::new().light_green().bold());
                        self.float(Anchor::Me, "388".into(), Style::new().green().bold());
                        self.log(Kind::Heal, None, format!("Clarity heals you for 388 ({heal} HP)"));
                    }
                    _ => {
                        let base = match key {
                            5 => self.rand(2400, 3600),
                            2 => self.rand(1100, 1700),
                            _ => self.rand(500, 900),
                        };
                        let crit = self.chance(22);
                        let dmg = if crit { base * 18 / 10 } else { base };
                        let mon_name = self.mons.iter().find(|m| m.id == target).map_or("?", |m| m.name);
                        if crit {
                            self.float(Anchor::Mon(target), format!("{}!", thousands(dmg)), Style::new().yellow().bold());
                            self.log(Kind::Crit, None, format!("{name} crits {mon_name} for {}", thousands(dmg)));
                        } else {
                            self.float(Anchor::Mon(target), thousands(dmg), Style::new().white().bold());
                            self.log(Kind::Hit, None, format!("{name} hits {mon_name} for {}", thousands(dmg)));
                        }
                        if key == 2 && !self.target_auras.iter().any(|a| a.name == "Soul Rot") {
                            self.target_auras.push(Aura { name: "Soul Rot", left: 6.0, max: 6.0, stacks: 1 });
                        }
                        self.damage(target, dmg, None);
                        if self.chance(18) {
                            self.proc_aura("Depravity");
                        } else if self.chance(10) {
                            self.proc_aura("Immorality");
                        }
                    }
                }
            }
        }

        // The party hits their own targets.
        for i in 0..self.mates.len() {
            if self.t < self.mates[i].next || self.mates[i].afk {
                continue;
            }
            self.mates[i].next = self.t + 1.3;
            let alive_target = self.mates[i].target.filter(|id| self.mons.iter().any(|m| m.id == *id && m.hp > 0));
            let target = alive_target.or_else(|| self.mons.iter().find(|m| alive(m)).map(|m| m.id));
            self.mates[i].target = target;
            if let Some(id) = target {
                let dmg = self.rand(500, 900);
                let who = self.mates[i].name;
                self.float(Anchor::Mon(id), thousands(dmg), Style::new().fg(DIM));
                self.damage(id, dmg, Some(who));
            }
            let drift = self.rand(-200, 180);
            self.mates[i].hp = (self.mates[i].hp + drift).clamp(self.mates[i].max / 3, self.mates[i].max);
        }

        // The cell hits back.
        if self.t >= self.next_hurt && self.mons.iter().any(|m| alive(m)) {
            self.next_hurt = self.t + 1.6;
            let mut dmg = self.rand(140, 360);
            if self.auras.iter().any(|a| a.name == "Arcane Shield") {
                dmg = dmg * 6 / 10;
            }
            self.hp = (self.hp - dmg).max(self.max_hp / 4);
            self.float(Anchor::Me, format!("-{dmg}"), Style::new().red().bold());
            self.log(Kind::Hurt, None, format!("Slime Mold hits you for {dmg}"));
        }
        if self.hp < self.max_hp / 2 && self.chance(5) {
            self.hp += 900;
            self.float(Anchor::Me, "900".into(), Style::new().green().bold());
            self.log(Kind::Heal, Some("alt3"), "alt3 heals you for 900".into());
        }

        if self.t >= self.next_chat {
            self.next_chat = self.t + 3.0 + self.rand(0, 40) as f64 / 10.0;
            self.chat();
        }
    }

    fn players(&self) -> usize {
        self.mates.len() + 1
    }

    fn here(&self) -> impl Iterator<Item = &Mon> {
        self.mons.iter().filter(|m| m.cell == MY_CELL)
    }

    fn attackers(&self, id: i64) -> Vec<&'static str> {
        let mut who: Vec<&'static str> = Vec::new();
        if self.target == Some(id) {
            who.push(ME);
        }
        who.extend(self.mates.iter().filter(|m| m.target == Some(id)).map(|m| m.name));
        who
    }

    fn clock(&self, t: f64) -> String {
        let s = (23 * 3600 + 2 * 60 + 51) + t as i64;
        format!("{:02}:{:02}:{:02}", s / 3600 % 24, s / 60 % 60, s % 60)
    }
}

// ───────────────────────────────────────────── small pieces ─────────────────────────────────────────────

fn thousands(n: i64) -> String {
    let s = n.abs().to_string();
    let mut out = String::new();
    for (i, c) in s.chars().enumerate() {
        if i > 0 && (s.len() - i) % 3 == 0 {
            out.push(',');
        }
        out.push(c);
    }
    if n < 0 { format!("-{out}") } else { out }
}

fn short(n: i64) -> String {
    match n {
        n if n >= 1_000_000 => format!("{:.1}m", n as f64 / 1e6),
        n if n >= 10_000 => format!("{}k", n / 1000),
        n if n >= 1_000 => format!("{:.1}k", n as f64 / 1e3),
        n => n.to_string(),
    }
}

fn truncate(text: &str, width: usize) -> String {
    if text.chars().count() <= width {
        text.to_owned()
    } else {
        let mut s: String = text.chars().take(width.saturating_sub(1)).collect();
        s.push('…');
        s
    }
}

/// A bar `width` cells wide with `text` centered on it.
fn bar(width: u16, ratio: f64, color: Color, text: &str, style: impl Fn(Style) -> Style) -> Vec<Span<'static>> {
    let width = width as usize;
    let filled = (ratio.clamp(0.0, 1.0) * width as f64).round() as usize;
    let text: String = text.chars().take(width).collect();
    let start = (width - text.chars().count()) / 2;
    let cells: Vec<char> = (0..width)
        .map(|i| if i >= start { text.chars().nth(i - start).unwrap_or(' ') } else { ' ' })
        .collect();
    let (done, todo) = cells.split_at(filled.min(width));
    vec![
        Span::styled(done.iter().collect::<String>(), style(Style::new().black().bg(color).bold())),
        Span::styled(todo.iter().collect::<String>(), style(Style::new().white().bg(TRACK))),
    ]
}

fn plain(s: Style) -> Style {
    s
}

fn ratio(a: i64, b: i64) -> f64 {
    if b <= 0 { 0.0 } else { a as f64 / b as f64 }
}

fn hp_color(r: f64) -> Color {
    if r > 0.5 {
        Color::Green
    } else if r > 0.25 {
        Color::Yellow
    } else {
        Color::Red
    }
}

fn panel(title: &str) -> Block<'static> {
    Block::new()
        .borders(Borders::ALL)
        .border_type(BorderType::Rounded)
        .border_style(Style::new().fg(DIM))
        .title(Span::styled(format!(" {title} "), Style::new().bold()))
}

fn channel_style(channel: &str) -> Style {
    match channel {
        "moderator" => Style::new().fg(Color::Rgb(230, 200, 120)),
        "warning" => Style::new().red(),
        "server" => Style::new().cyan(),
        "whisper" => Style::new().magenta(),
        "party" => Style::new().light_cyan(),
        _ => Style::new(),
    }
}

fn chat_line(e: &Entry, width: usize) -> Line<'static> {
    let Kind::Chat(channel) = e.kind else { unreachable!() };
    let tag = match channel {
        "moderator" => "[Moderator] ".to_owned(),
        "warning" | "server" => String::new(),
        c => format!("[{c}] "),
    };
    let who = e.who.map(|w| format!("{w}: ")).unwrap_or_default();
    Line::styled(truncate(&format!("{tag}{who}{}", e.text), width), channel_style(channel))
}

fn skill_glyph(s: &Skill) -> (&'static str, Style) {
    if s.name.is_empty() {
        ("·", Style::new().fg(DIM))
    } else if s.left <= 0.0 {
        ("●", Style::new().light_blue().bold())
    } else {
        match s.left / s.cd {
            r if r > 0.66 => ("○", Style::new().fg(DIM)),
            r if r > 0.33 => ("◔", Style::new().fg(DIM)),
            _ => ("◑", Style::new().fg(DIM)),
        }
    }
}

fn aura_text(a: &Aura) -> String {
    if a.stacks > 1 {
        format!("{} ×{} {:.0}s", a.name, a.stacks, a.left.ceil())
    } else {
        format!("{} {:.0}s", a.name, a.left.ceil())
    }
}


fn duration(sec: f64) -> String {
    let s = sec as i64;
    if s >= 3600 { format!("{}h {:02}m", s / 3600, s / 60 % 60) } else { format!("{}m {:02}s", s / 60, s % 60) }
}

/// What the Overview says today about the run: the Script, how it goes, and its goal as a tree.
fn script_lines(sim: &Sim) -> Vec<Line<'static>> {
    let idle = sim.t - sim.last_progress;
    let farm = &sim.reqs[0];
    let left = (farm.qty - farm.have).max(0) as f64 / farm.per_hour.unwrap_or(1.0) * 3600.0;
    vec![
        Line::from(vec![
            Span::styled("DragonsWill", Style::new().magenta().bold()),
            Span::raw(format!(
                "  running {} · {} kills · 18.4/min · 0 deaths",
                duration(2710.0 + sim.t),
                thousands(812 + sim.kills)
            )),
        ]),
        Line::styled(
            format!("progressing · last quest progress {:.0}s ago", idle),
            if idle < 60.0 { Style::new().green() } else { Style::new().yellow() },
        ),
        Line::from(vec![Span::styled("└ quest  ", Style::new().fg(DIM)), Span::raw(QUEST)]),
        Line::from(vec![
            Span::styled("   └ farm   ", Style::new().fg(DIM)),
            Span::raw(format!("{:<20}", farm.name)),
            Span::styled(format!("{}/{}", farm.have, farm.qty), Style::new().bold()),
            Span::styled(format!("  +{:.0}/h  ~{}", farm.per_hour.unwrap_or(0.0), duration(left)), Style::new().fg(DIM)),
        ]),
        Line::from(vec![
            Span::styled("      └ now    ", Style::new().fg(DIM)),
            Span::raw("killing Slime Mold for Unyielding Slime"),
        ]),
    ]
}

/// A Question the Script raised, when `d` raised one; returns the height it took.
fn question(f: &mut Frame, area: Rect) -> u16 {
    if !QUESTION.load(Ordering::Relaxed) || area.height < 5 {
        return 0;
    }
    let b = panel("Question · DragonsWill · d to answer")
        .border_style(Style::new().yellow())
        .title_style(Style::new().yellow().bold());
    let rect = Rect { height: 5, ..area };
    f.render_widget(Clear, rect);
    f.render_widget(
        Paragraph::new(vec![
            Line::styled("Dragon's Will is ready to turn in. Turn it in now?", Style::new().yellow().bold()),
            Line::raw("  1  Yes"),
            Line::raw("  2  No, keep farming"),
        ])
        .block(b),
        rect,
    );
    5
}

// ───────────────────────────────────────────── A · Screen ─────────────────────────────────────────────

/// The game's screen, where things sit in it, without buffs, damage numbers or XP: the player frame top left, the Script in the middle,
/// quests top right; any Question under them; the party and the cell's monsters with their HP; chat and the room; the skill bar.
fn variant_a(f: &mut Frame, sim: &Sim, area: Rect) {
    let block = panel(&format!("Overview · {MAP} · cell {MY_CELL}"));
    let inner = block.inner(area);
    f.render_widget(block, area);
    let mut party = vec![(ME, "Legion Revenant", sim.hp, sim.max_hp)];
    party.extend(sim.mates.iter().filter(|m| m.cell == MY_CELL).map(|m| (m.name, m.class, m.hp, m.max)));
    let question_h = if QUESTION.load(Ordering::Relaxed) { 5 } else { 0 };
    let [top, question_area, toast, stage, bottom, skills] = Layout::vertical([
        Constraint::Length(7),
        Constraint::Length(question_h),
        Constraint::Length(1),
        Constraint::Length((party.len() as u16 * 3).max(4)),
        Constraint::Fill(1),
        Constraint::Length(4),
    ])
    .areas(inner);

    // Player frame, Script, quests.
    let [frame_area, script_area, quests_area] =
        Layout::horizontal([Constraint::Length(36), Constraint::Min(0), Constraint::Length(38)]).areas(top);
    let frame_block = Block::new()
        .borders(Borders::ALL)
        .border_type(BorderType::Rounded)
        .border_style(Style::new().fg(DIM))
        .title(Span::styled(format!(" {ME} "), Style::new().yellow().bold()))
        .title(Line::styled(" Lv 100 ", Style::new().bold()).right_aligned())
        .title_bottom(Line::styled(format!(" ● {} gold ", thousands(sim.gold)), Style::new().yellow().bold()).right_aligned());
    let fi = frame_block.inner(frame_area);
    f.render_widget(frame_block, frame_area);
    let w = fi.width.saturating_sub(4);
    let mut lines = vec![
        Line::from(vec![
            Span::styled("Legion Revenant", Style::new().fg(DIM)),
            Span::styled(", Rank 10", lack(Style::new().fg(DIM))),
        ]),
        Line::raw(""),
    ];
    let mut gauge = |label: &str, value: i64, max: i64, color: Color, missing: bool| {
        let mut spans = vec![Span::styled(format!("{label:<3} "), Style::new().fg(DIM))];
        let style = if missing { lack } else { plain };
        spans.extend(bar(w, ratio(value, max), color, &format!("{value}/{max}"), style));
        lines.push(Line::from(spans));
    };
    gauge("HP", sim.hp, sim.max_hp, Color::Red, false);
    gauge("MP", sim.mp, sim.max_mp, Color::Blue, false);
    gauge("SP", sim.sp, 100, Color::Green, true);
    f.render_widget(Paragraph::new(lines), fi);
    f.render_widget(Paragraph::new(script_lines(sim)).block(panel("Script")), script_area);
    let qb = panel("Current Quests");
    let qi = qb.inner(quests_area);
    f.render_widget(qb, quests_area);
    let mut lines = vec![Line::styled(QUEST, Style::new().green().bold())];
    for r in &sim.reqs {
        let done = r.have >= r.qty;
        lines.push(Line::styled(
            format!("  {}{} {}/{}", if done { "✓ " } else { "" }, r.name, r.have, r.qty),
            if done { Style::new().green() } else { Style::new() },
        ));
    }
    f.render_widget(Paragraph::new(lines), qi);

    question(f, question_area);
    if let Some((text, age)) = &sim.toast {
        let style = if *age > 3.0 { Style::new().fg(DIM) } else { Style::new().yellow().bold() };
        f.render_widget(Paragraph::new(Line::styled(text.clone(), style)).alignment(Alignment::Center), toast);
    }

    // The party on the left, the player first; the cell's monsters on the right, the target outlined.
    let party_w = 30;
    for (i, (name, class, hp, max)) in party.iter().enumerate() {
        let rect = Rect { x: stage.x + 1, y: stage.y + i as u16 * 3, width: party_w, height: 2 };
        let name_style = if i == 0 { Style::new().yellow().bold() } else { Style::new().bold() };
        f.render_widget(
            Paragraph::new(vec![
                Line::from(vec![
                    Span::styled(truncate(name, 12), name_style),
                    Span::styled(format!(" {}", truncate(class, 15)), Style::new().fg(DIM)),
                ]),
                Line::from(bar(24, ratio(*hp, *max), hp_color(ratio(*hp, *max)), &format!("{hp}/{max}"), plain)),
            ]),
            rect,
        );
    }
    let mons: Vec<&Mon> = sim.here().collect();
    let mons_x = stage.x + 1 + party_w + 2;
    let plate_w = ((stage.right().saturating_sub(mons_x)) / mons.len().max(1) as u16).saturating_sub(1).clamp(16, 26);
    for (i, m) in mons.iter().enumerate() {
        let x = mons_x + i as u16 * (plate_w + 1);
        if x + plate_w > stage.right() {
            break;
        }
        let rect = Rect { x, y: stage.y, width: plate_w, height: 4 };
        let targeted = sim.target == Some(m.id);
        let b = Block::new()
            .borders(Borders::ALL)
            .border_type(BorderType::Rounded)
            .border_style(if targeted { Style::new().yellow() } else { Style::new().fg(DIM) })
            .title(Span::styled(
                format!("{}{}", if targeted { "▶ " } else { "" }, m.name),
                if m.hp > 0 { Style::new().bold() } else { Style::new().fg(DIM) },
            ));
        let bi = b.inner(rect);
        f.render_widget(b, rect);
        let lines = if m.hp > 0 {
            vec![
                Line::from(bar(bi.width, ratio(m.hp, m.max), Color::Red, &format!("{}/{}", short(m.hp), short(m.max)), plain)),
                Line::styled(truncate(&sim.attackers(m.id).join(", "), bi.width as usize), Style::new().fg(DIM)),
            ]
        } else {
            vec![Line::styled(format!("respawning {:.0}s", m.respawn.ceil()), Style::new().fg(DIM))]
        };
        f.render_widget(Paragraph::new(lines), bi);
    }

    // Chat, and who's in the room.
    let [chat_area, room_area] = Layout::horizontal([Constraint::Min(0), Constraint::Length(30)]).areas(bottom);
    let chat: Vec<&Entry> = sim.feed.iter().filter(|e| matches!(e.kind, Kind::Chat(_))).collect();
    let skip = chat.len().saturating_sub(chat_area.height as usize);
    let lines: Vec<Line> = chat[skip..].iter().map(|e| chat_line(e, chat_area.width as usize)).collect();
    f.render_widget(Paragraph::new(lines), chat_area);
    let mut lines: Vec<Line> = Vec::new();
    for m in &sim.mates {
        lines.push(Line::styled(
            format!("{:<12}Lv{:<4}{}{}", m.name, m.level, m.cell, if m.afk { " afk" } else { "" }),
            Style::new().fg(DIM),
        ));
    }
    lines.push(Line::from(vec![
        Span::raw(format!("{} player(s) in ", sim.players())),
        Span::styled(MAP, Style::new().yellow()),
    ]));
    let pad = room_area.height.saturating_sub(lines.len() as u16);
    f.render_widget(
        Paragraph::new(lines).alignment(Alignment::Right),
        Rect { y: room_area.y + pad, height: room_area.height - pad, ..room_area },
    );

    // Skill bar.
    let slot_w = (skills.width / 6).clamp(10, 20);
    let x0 = skills.x + (skills.width.saturating_sub(slot_w * 6)) / 2;
    for (i, s) in sim.skills.iter().enumerate() {
        let rect = Rect { x: x0 + i as u16 * slot_w, y: skills.y, width: slot_w, height: 4 };
        let ready = s.left <= 0.0 && !s.name.is_empty();
        let b = Block::new()
            .borders(Borders::ALL)
            .border_type(BorderType::Rounded)
            .border_style(lack(if ready { Style::new().light_blue() } else { Style::new().fg(DIM) }))
            .title(Span::styled(format!("{}", s.key), Style::new().bold()));
        let bi = b.inner(rect);
        f.render_widget(b, rect);
        let lines = if s.name.is_empty() {
            vec![Line::styled("empty", Style::new().fg(DIM))]
        } else {
            let second = if ready {
                Line::styled("ready", lack(Style::new().light_blue())).centered()
            } else {
                Line::from(bar(bi.width, 1.0 - s.left / s.cd, Color::Blue, &format!("{:.1}", s.left), lack))
            };
            vec![Line::styled(truncate(s.name, bi.width as usize), Style::new().fg(if ready { Color::White } else { DIM })), second]
        };
        f.render_widget(Paragraph::new(lines), bi);
    }
}

// ───────────────────────────────────────────── B · Columns ─────────────────────────────────────────────

/// A cockpit in three columns: me (vitals, auras, skills), the map by cell with who fights what, then quests and chat.
fn variant_b(f: &mut Frame, sim: &Sim, area: Rect) {
    let [me, field, right] =
        Layout::horizontal([Constraint::Length(36), Constraint::Min(30), Constraint::Length(42)]).areas(area);

    // Me.
    let [vitals, auras, skills] = Layout::vertical([Constraint::Length(9), Constraint::Min(5), Constraint::Length(8)]).areas(me);
    let b = panel(ME);
    let vi = b.inner(vitals);
    f.render_widget(b, vitals);
    let w = vi.width.saturating_sub(4);
    let mut lines = vec![
        Line::from(vec![
            Span::styled("Lv 100 · Legion Revenant", Style::new().fg(DIM)),
            Span::styled(" R10", lack(Style::new().fg(DIM))),
        ]),
        Line::raw(""),
    ];
    for (label, v, m, c, missing) in [
        ("HP", sim.hp, sim.max_hp, Color::Red, false),
        ("MP", sim.mp, sim.max_mp, Color::Blue, false),
        ("SP", sim.sp, 100, Color::Green, true),
    ] {
        let mut spans = vec![Span::styled(format!("{label:<3} "), Style::new().fg(DIM))];
        spans.extend(bar(w, ratio(v, m), c, &format!("{v}/{m}"), if missing { lack } else { plain }));
        lines.push(Line::from(spans));
    }
    lines.push(Line::raw(""));
    lines.push(Line::from(vec![
        Span::styled("gold ", Style::new().fg(DIM)),
        Span::styled(thousands(sim.gold), Style::new().yellow().bold()),
    ]));
    let mut xp = vec![Span::styled("XP  ", Style::new().fg(DIM))];
    xp.extend(bar(w, ratio(sim.xp, sim.xp_max), Color::Magenta, &format!("{:.0}%", ratio(sim.xp, sim.xp_max) * 100.0), plain));
    lines.push(Line::from(xp));
    f.render_widget(Paragraph::new(lines), vi);

    let b = panel("Auras");
    let ai = b.inner(auras);
    f.render_widget(b, auras);
    let mut lines = vec![Line::styled("on you", Style::new().fg(DIM).bold())];
    let aw = ai.width.saturating_sub(22);
    for a in &sim.auras {
        let mut spans = vec![Span::styled(format!("{:<20}", truncate(&aura_text(a), 19)), lack(Style::new().light_green()))];
        spans.extend(bar(aw, a.left / a.max, Color::Green, "", lack));
        lines.push(Line::from(spans));
    }
    lines.push(Line::styled("on target", Style::new().fg(DIM).bold()));
    for a in &sim.target_auras {
        let mut spans = vec![Span::styled(format!("{:<20}", truncate(&aura_text(a), 19)), lack(Style::new().light_red()))];
        spans.extend(bar(aw, a.left / a.max, Color::Red, "", lack));
        lines.push(Line::from(spans));
    }
    f.render_widget(Paragraph::new(lines), ai);

    let b = panel("Skills");
    let si = b.inner(skills);
    f.render_widget(b, skills);
    let lines: Vec<Line> = sim
        .skills
        .iter()
        .map(|s| {
            if s.name.is_empty() {
                return Line::styled(format!("{} ·", s.key), Style::new().fg(DIM));
            }
            let mut spans = vec![
                Span::styled(format!("{} ", s.key), Style::new().bold()),
                Span::styled(format!("{:<18}", truncate(s.name, 17)), lack(Style::new())),
            ];
            if s.left <= 0.0 {
                spans.push(Span::styled("ready", lack(Style::new().light_blue().bold())));
            } else {
                spans.extend(bar(si.width.saturating_sub(21), 1.0 - s.left / s.cd, Color::Blue, &format!("{:.1}s", s.left), lack));
            }
            Line::from(spans)
        })
        .collect();
    f.render_widget(Paragraph::new(lines), si);

    // The run, any Question, then the map by cell.
    let [script_area, rest] = Layout::vertical([Constraint::Length(7), Constraint::Min(0)]).areas(field);
    f.render_widget(Paragraph::new(script_lines(sim)).block(panel("Script")), script_area);
    let qh = question(f, rest);
    let field = Rect { y: rest.y + qh, height: rest.height - qh, ..rest };
    let b = panel(&format!("{MAP} · {} players · {} monsters", sim.players(), sim.mons.len()));
    let mi = b.inner(field);
    f.render_widget(b, field);
    let mut cells: Vec<&str> = vec![MY_CELL];
    for m in &sim.mons {
        if !cells.contains(&m.cell) {
            cells.push(m.cell);
        }
    }
    let mut lines = Vec::new();
    let bw = mi.width.saturating_sub(24 + 18).clamp(8, 26);
    for cell in cells {
        let players: Vec<&str> = std::iter::once(ME)
            .filter(|_| cell == MY_CELL)
            .chain(sim.mates.iter().filter(|m| m.cell == cell).map(|m| m.name))
            .collect();
        let mons: Vec<&Mon> = sim.mons.iter().filter(|m| m.cell == cell).collect();
        if cell != MY_CELL {
            lines.push(Line::styled(
                format!("▸ {cell:<8}{} monsters · {}", mons.len(), if players.is_empty() { "nobody".into() } else { players.join(", ") }),
                Style::new().fg(DIM),
            ));
            continue;
        }
        lines.push(Line::from(vec![
            Span::styled(format!("▾ {cell:<8}"), Style::new().bold()),
            Span::styled("you're here", Style::new().yellow()),
        ]));
        lines.push(Line::styled("  players", Style::new().fg(DIM).bold()));
        let mut rows: Vec<(&str, &str, i64, i64, Option<i64>)> = vec![(ME, "Legion Revenant", sim.hp, sim.max_hp, sim.target)];
        rows.extend(sim.mates.iter().filter(|m| m.cell == cell).map(|m| (m.name, m.class, m.hp, m.max, m.target)));
        for (name, class, hp, max, target) in rows {
            let mut spans = vec![
                Span::styled(format!("  {:<12}", truncate(name, 11)), if name == ME { Style::new().yellow().bold() } else { Style::new().bold() }),
                Span::styled(format!("{:<10}", truncate(class, 9)), Style::new().fg(DIM)),
            ];
            spans.extend(bar(bw, ratio(hp, max), hp_color(ratio(hp, max)), &short(hp), plain));
            let on = target.and_then(|id| sim.mons.iter().find(|m| m.id == id)).map(|m| format!("  → {} #{}", m.name, m.id));
            spans.push(Span::styled(on.unwrap_or_default(), Style::new().fg(DIM)));
            lines.push(Line::from(spans));
        }
        lines.push(Line::styled("  monsters", Style::new().fg(DIM).bold()));
        for m in mons {
            let targeted = sim.target == Some(m.id);
            let mut spans = vec![Span::styled(
                format!("{} {:<20}", if targeted { "▶" } else { " " }, truncate(&format!("{} #{}", m.name, m.id), 19)),
                if m.hp <= 0 { Style::new().fg(DIM) } else if targeted { Style::new().yellow().bold() } else { Style::new() },
            )];
            spans.push(Span::raw("  "));
            if m.hp > 0 {
                spans.extend(bar(bw, ratio(m.hp, m.max), Color::Red, &format!("{}/{}", short(m.hp), short(m.max)), plain));
                let who = sim.attackers(m.id);
                spans.push(if who.is_empty() {
                    Span::styled("  no one on it", Style::new().fg(DIM))
                } else {
                    Span::styled(format!("  ◀ {}", who.join(", ")), Style::new().yellow())
                });
            } else {
                spans.push(Span::styled(format!("dead · respawns in {:.0}s", m.respawn.ceil()), Style::new().fg(DIM)));
            }
            lines.push(Line::from(spans));
        }
        // What just hit what in this cell, newest first, in place of the game's floating numbers.
        lines.push(Line::raw(""));
        lines.push(Line::styled("  just now", Style::new().fg(DIM).bold()));
        let recent: Vec<&Float> = sim.floats.iter().rev().take(6).collect();
        for fl in recent {
            let on = match fl.anchor {
                Anchor::Me => "you".to_owned(),
                Anchor::Mon(id) => sim.mons.iter().find(|m| m.id == id).map_or("?".into(), |m| format!("{} #{id}", m.name)),
            };
            lines.push(Line::from(vec![
                Span::styled(format!("  {:<16}", fl.text), lack(fl.style)),
                Span::styled(format!("on {on}"), Style::new().fg(DIM)),
            ]));
        }
        lines.push(Line::raw(""));
    }
    f.render_widget(Paragraph::new(lines), mi);

    // Quests, then chat.
    let qh = 3 + sim.reqs.len() as u16 * 2;
    let [quests, chat] = Layout::vertical([Constraint::Length(qh), Constraint::Min(4)]).areas(right);
    let b = panel("Quests");
    let qi = b.inner(quests);
    f.render_widget(b, quests);
    let mut lines = vec![Line::from(vec![
        Span::styled(QUEST, Style::new().green().bold()),
        Span::styled(format!("  {} kills", sim.kills), Style::new().fg(DIM)),
    ])];
    for r in &sim.reqs {
        let done = r.have >= r.qty;
        lines.push(Line::from(vec![
            Span::styled(format!("{}{}", if done { "✓ " } else { "" }, r.name), if done { Style::new().green() } else { Style::new() }),
            Span::styled(r.per_hour.map(|p| format!("  +{p:.0}/h")).unwrap_or_default(), Style::new().fg(DIM)),
        ]));
        lines.push(Line::from(bar(qi.width, ratio(r.have, r.qty), if done { Color::Green } else { Color::Cyan }, &format!("{}/{}", r.have, r.qty), plain)));
    }
    f.render_widget(Paragraph::new(lines), qi);
    let b = panel("Chat");
    let ci = b.inner(chat);
    f.render_widget(b, chat);
    let entries: Vec<&Entry> = sim.feed.iter().filter(|e| matches!(e.kind, Kind::Chat(_))).collect();
    let skip = entries.len().saturating_sub(ci.height as usize);
    let lines: Vec<Line> = entries[skip..].iter().map(|e| chat_line(e, ci.width as usize)).collect();
    f.render_widget(Paragraph::new(lines), ci);
}

// ───────────────────────────────────────────── C · Feed ─────────────────────────────────────────────

/// A status strip on top, then everything the game shows as one stream, a MUD's way: hits, heals, procs, kills, quest progress and
/// chat, each a line; quests and the room in a narrow gutter.
fn variant_c(f: &mut Frame, sim: &Sim, area: Rect) {
    let [strip, body] = Layout::vertical([Constraint::Length(7), Constraint::Min(0)]).areas(area);
    let b = panel(&format!("{ME} · {MAP} › {MY_CELL}"));
    let si = b.inner(strip);
    f.render_widget(b, strip);

    let mut l1 = vec![
        Span::styled("Lv 100 Legion Revenant", Style::new().fg(DIM)),
        Span::styled(" R10", lack(Style::new().fg(DIM))),
        Span::raw("   HP "),
    ];
    l1.extend(bar(18, ratio(sim.hp, sim.max_hp), Color::Red, &format!("{}/{}", sim.hp, sim.max_hp), plain));
    l1.push(Span::raw("  MP "));
    l1.extend(bar(10, ratio(sim.mp, sim.max_mp), Color::Blue, &format!("{}", sim.mp), plain));
    l1.push(Span::styled("  SP ", lack(Style::new())));
    l1.extend(bar(8, ratio(sim.sp, 100), Color::Green, &format!("{}", sim.sp), lack));
    l1.push(Span::styled(format!("   ● {}", thousands(sim.gold)), Style::new().yellow().bold()));
    l1.push(Span::styled(format!("   XP {:.0}%", ratio(sim.xp, sim.xp_max) * 100.0), Style::new().magenta()));

    let mut l2 = vec![Span::styled("target ", Style::new().fg(DIM))];
    match sim.target.and_then(|id| sim.mons.iter().find(|m| m.id == id)) {
        Some(m) => {
            l2.push(Span::styled(format!("▶ {} #{} ", m.name, m.id), Style::new().yellow().bold()));
            l2.extend(bar(18, ratio(m.hp, m.max), Color::Red, &format!("{}/{}", short(m.hp), short(m.max)), plain));
            let ta: Vec<String> = sim.target_auras.iter().map(aura_text).collect();
            l2.push(Span::styled(format!("  {}", ta.join(" · ")), lack(Style::new().light_red())));
        }
        None => l2.push(Span::styled("none", Style::new().fg(DIM))),
    }
    l2.push(Span::styled("   on you ", Style::new().fg(DIM)));
    let auras: Vec<String> = sim.auras.iter().map(aura_text).collect();
    l2.push(Span::styled(auras.join(" · "), lack(Style::new().light_green())));

    let mut l3 = vec![Span::styled("skills ", Style::new().fg(DIM))];
    for s in &sim.skills {
        let (glyph, style) = skill_glyph(s);
        l3.push(Span::styled(format!("{} ", s.key), Style::new().bold()));
        l3.push(Span::styled(glyph, lack(style)));
        let label = if s.name.is_empty() {
            String::new()
        } else if s.left > 0.0 {
            format!(" {} {:.1}", s.name, s.left)
        } else {
            format!(" {}", s.name)
        };
        l3.push(Span::styled(format!("{label}   "), lack(if s.left > 0.0 { Style::new().fg(DIM) } else { Style::new() })));
    }
    let script = script_lines(sim);
    let farm = &sim.reqs[0];
    let l5 = Line::from(vec![
        Span::styled("goal   ", Style::new().fg(DIM)),
        Span::raw(format!("{QUEST} ← {} ", farm.name)),
        Span::styled(format!("{}/{}", farm.have, farm.qty), Style::new().bold()),
        Span::raw(" ← killing Slime Mold"),
    ]);
    let mut l4 = vec![Span::styled("script ", Style::new().fg(DIM))];
    l4.extend(script[0].spans.clone());
    l4.push(Span::styled("  ·  ", Style::new().fg(DIM)));
    l4.extend(script[1].spans.iter().cloned().map(|s| s.style(script[1].style)));
    f.render_widget(Paragraph::new(vec![Line::from(l1), Line::from(l2), Line::from(l3), Line::from(l4), l5]), si);

    let [feed, gutter] = Layout::horizontal([Constraint::Min(0), Constraint::Length(34)]).areas(body);
    let qh = question(f, feed);
    let feed = Rect { y: feed.y + qh, height: feed.height - qh, ..feed };
    let b = panel("Feed").title(Line::styled(" everything, newest last ", Style::new().fg(DIM)).right_aligned());
    let fi = b.inner(feed);
    f.render_widget(b, feed);
    let skip = sim.feed.len().saturating_sub(fi.height as usize);
    let lines: Vec<Line> = sim.feed[skip..]
        .iter()
        .map(|e| {
            let (icon, style, missing) = match e.kind {
                Kind::Hit => ("⚔", Style::new(), true),
                Kind::Crit => ("⚔", Style::new().yellow().bold(), true),
                Kind::Heal => ("✚", Style::new().green(), true),
                Kind::Proc => ("✦", Style::new().light_green(), true),
                Kind::Hurt => ("✖", Style::new().red(), true),
                Kind::Kill => ("☠", if e.who.is_some() { Style::new().fg(DIM) } else { Style::new().bold() }, false),
                Kind::Quest => ("❖", Style::new().yellow().bold(), false),
                Kind::Chat(c) => ("›", channel_style(c), false),
            };
            let text = match (e.kind, e.who) {
                (Kind::Chat(_), _) => chat_line(e, fi.width.saturating_sub(12) as usize).spans.into_iter().map(|s| s.content).collect(),
                (Kind::Kill, Some(who)) => format!("{} (by {who})", e.text),
                _ => e.text.clone(),
            };
            let style = if missing { lack(style) } else { style };
            Line::from(vec![
                Span::styled(format!("{} ", &sim.clock(e.t)[3..]), Style::new().fg(DIM)),
                Span::styled(format!("{icon} "), style),
                Span::styled(truncate(&text, fi.width.saturating_sub(8) as usize), style),
            ])
        })
        .collect();
    f.render_widget(Paragraph::new(lines), fi);

    let b = panel(QUEST);
    let gi = b.inner(gutter);
    f.render_widget(b, gutter);
    let mut lines = Vec::new();
    for r in &sim.reqs {
        let done = r.have >= r.qty;
        lines.push(Line::from(vec![
            Span::styled(format!("{:>7} ", format!("{}/{}", r.have, r.qty)), if done { Style::new().green().bold() } else { Style::new().bold() }),
            Span::styled(truncate(r.name, gi.width as usize - 8), if done { Style::new().green() } else { Style::new() }),
        ]));
    }
    lines.push(Line::raw(""));
    lines.push(Line::styled(format!("ROOM · {} players", sim.players()), Style::new().fg(DIM).bold()));
    lines.push(Line::styled(format!("{ME} ({MY_CELL})"), Style::new().yellow()));
    for m in &sim.mates {
        lines.push(Line::styled(format!("{} ({}){}", m.name, m.cell, if m.afk { " afk" } else { "" }), Style::new().fg(DIM)));
    }
    lines.push(Line::raw(""));
    lines.push(Line::styled(format!("IN {MY_CELL}"), Style::new().fg(DIM).bold()));
    for m in sim.here() {
        let mut spans = vec![Span::styled(
            format!("{:<16}", truncate(&m.name, 15)),
            if m.hp > 0 { Style::new() } else { Style::new().fg(DIM) },
        )];
        let text = if m.hp > 0 { short(m.hp) } else { "dead".into() };
        spans.extend(bar(gi.width.saturating_sub(16), ratio(m.hp, m.max), Color::Red, &text, plain));
        lines.push(Line::from(spans));
    }
    f.render_widget(Paragraph::new(lines), gi);
}

// ───────────────────────────────────────────── the real chrome ─────────────────────────────────────────────

/// The real TUI on the Overview, with a fake fleet: alt1 is selected and playing, two alts are on its map, and one account is offline.
fn chrome_app() -> App {
    let accounts: ManagerAccounts = serde_json::from_value(json!({
        "accounts": [
            { "name": "alt1", "username": "ALT1" },
            { "name": "alt2", "username": "alt2" },
            { "name": "alt3", "username": "alt3" },
            { "name": "storage", "username": "storage" }
        ],
        "groups": [{ "name": "Dragon's Will", "usernames": ["ALT1", "alt2", "alt3"] }]
    }))
    .unwrap();
    let engine = |name: &str, display: &str, class: &str, script: Option<&str>| {
        let run = script.map(|s| json!({ "number": 1, "script": s, "relogins": 0, "reloggingIn": false, "elapsedSec": 2710.0,
            "questIdleSec": 12.0, "kills": 812, "killsPerMin": 18.4, "deaths": 0 }));
        let status = json!({
            "engine": { "name": name, "build": "1.4.4.4+prototype", "protocol": 17, "uptimeSec": 3725.0, "pid": 4242, "host": "engine" },
            "game": { "gameHostUp": true, "state": "playing", "server": "Twilly", "playerAgeSec": 0.4, "player": {
                "name": display, "level": 100, "class": class, "hp": 3070, "maxHp": 3070, "mp": 85, "maxMp": 100, "gold": 100_525_185,
                "map": MAP, "cell": MY_CELL, "pad": "Spawn", "alive": true, "inCombat": true, "xp": 0, "requiredXp": 0, "xpPercent": null,
                "targetId": 1 } },
            "script": { "state": if run.is_some() { "running" } else { "idle" }, "run": run, "lastRun": null },
            "pendingDialogs": []
        });
        EngineView::Up {
            hello: serde_json::from_value(json!({ "protocol": 17, "build": "1.4.4.4+prototype", "engineName": name, "pid": 4242, "host": "engine" }))
                .unwrap(),
            status: Box::new(serde_json::from_value(status).unwrap()),
        }
    };
    let mut engines = BTreeMap::new();
    engines.insert("alt1".into(), engine("alt1", ME, "Legion Revenant", Some("Story/DragonsWill.cs")));
    engines.insert("alt2".into(), engine("alt2", "alt2", "ArchMage", Some("Army/ArmyDragonsWill.cs")));
    engines.insert("alt3".into(), engine("alt3", "alt3", "Lord Of Order", Some("Army/ArmyDragonsWill.cs")));
    let mut app = App::new(PathBuf::from("/nonexistent/skua-prototype"));
    app.set_snapshot(Snapshot {
        accounts: Ok(accounts),
        hook_runner: true,
        engines,
        detail: Detail { engine: Some("alt1".into()), ..Default::default() },
    });
    app.tab = Tab::Overview;
    app
}

fn draw(f: &mut Frame, app: &App, sim: &Sim, variant: usize) {
    skua_tui::ui::draw(f, app);
    let area = f.area();
    if area.width < 100 || area.height < 30 {
        f.render_widget(Clear, area);
        f.render_widget(Paragraph::new("the prototype wants at least 100×30"), area);
        return;
    }
    // Where ui::draw puts the selected tab's content.
    let left = (area.width * 3 / 10).clamp(30, 38).min(area.width / 2);
    let content = Rect { x: left, y: 2, width: area.width - left, height: area.height - 4 };
    f.render_widget(Clear, content);
    match variant {
        0 => variant_a(f, sim, content),
        1 => variant_b(f, sim, content),
        _ => variant_c(f, sim, content),
    }

    // The switcher, over the footer.
    let bar_area = Rect { y: area.height - 1, height: 1, ..area };
    f.render_widget(Clear, bar_area);
    let (key, name) = VARIANTS[variant];
    let line = Line::from(vec![
        Span::styled(format!("  ◀  {key} · {name}  ▶  "), Style::new().black().on_yellow().bold()),
        Span::styled(
            format!(
                "  ←/→ variant · n {} what the Control Surface lacks · d Question · q quit",
                if LACK.load(Ordering::Relaxed) { "unmark" } else { "mark" }
            ),
            Style::new().fg(DIM),
        ),
    ]);
    f.render_widget(Paragraph::new(line).alignment(Alignment::Center), bar_area);
}

fn main() -> io::Result<()> {
    let mut variant = std::env::args()
        .nth(1)
        .and_then(|a| VARIANTS.iter().position(|(k, _)| k.eq_ignore_ascii_case(&a)))
        .unwrap_or(0);
    let app = chrome_app();
    let mut sim = Sim::new();
    // `--dump`: each variant after 8 s of fighting, as text, for a look without a terminal.
    if std::env::args().any(|a| a == "--dump") {
        QUESTION.store(std::env::args().any(|a| a == "--question"), Ordering::Relaxed);
        (0..80).for_each(|_| sim.step(0.1));
        for v in 0..VARIANTS.len() {
            let mut t = ratatui::Terminal::new(ratatui::backend::TestBackend::new(DUMP.0, DUMP.1)).unwrap();
            t.draw(|f| draw(f, &app, &sim, v)).unwrap();
            let buf = t.backend().buffer();
            for y in 0..DUMP.1 {
                println!("{}", (0..DUMP.0).map(|x| buf[(x, y)].symbol()).collect::<String>());
            }
        }
        return Ok(());
    }
    let mut terminal = ratatui::init();
    let mut last = Instant::now();
    let result = (|| -> io::Result<()> {
        loop {
            while last.elapsed() >= Duration::from_millis(100) {
                last += Duration::from_millis(100);
                sim.step(0.1);
            }
            terminal.draw(|f| draw(f, &app, &sim, variant))?;
            if event::poll(Duration::from_millis(50))?
                && let Event::Key(key) = event::read()?
                && key.kind == KeyEventKind::Press
            {
                match key.code {
                    KeyCode::Char('q') | KeyCode::Esc => return Ok(()),
                    KeyCode::Right | KeyCode::Char('l') => variant = (variant + 1) % VARIANTS.len(),
                    KeyCode::Left | KeyCode::Char('h') => variant = (variant + VARIANTS.len() - 1) % VARIANTS.len(),
                    KeyCode::Char(c @ ('a' | 'b' | 'c' | 'A' | 'B' | 'C')) => {
                        variant = (c.to_ascii_uppercase() as u8 - b'A') as usize;
                    }
                    KeyCode::Char('d') => {
                        QUESTION.fetch_xor(true, Ordering::Relaxed);
                    }
                    KeyCode::Char('n') => {
                        LACK.fetch_xor(true, Ordering::Relaxed);
                    }
                    _ => {}
                }
            }
        }
    })();
    ratatui::restore();
    result
}
