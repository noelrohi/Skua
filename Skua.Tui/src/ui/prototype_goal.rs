//! PROTOTYPE, throwaway: three ways to show what a Script is farming and why, from its CoreBots log lines ("Doing Quest", "Farming to
//! buy", "Farming X (n/m)", "Killing M for item", "Death - Resetting") with live counts from the inventory. Run with
//! `SKUA_TUI_PROTOTYPE=1`, open Overview and flip with ←/→. Question: what tells you why an account loops?

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
use serde_json::json;

use super::{DIM, gauge, local_time, panel, rate_text, short_duration};
use crate::discovery;
use crate::dto::{Inventory, LogEntry, Status};
use crate::engine::Engine;

pub const VARIANTS: [&str; 3] = ["Tree", "Breadcrumb + bars", "Timeline"];

static VARIANT: AtomicUsize = AtomicUsize::new(0);

pub fn enabled() -> bool {
    std::env::var("SKUA_TUI_PROTOTYPE").is_ok_and(|v| v == "1")
}

pub fn cycle(by: isize) {
    let n = VARIANTS.len() as isize;
    VARIANT.store((VARIANT.load(Ordering::Relaxed) as isize + by).rem_euclid(n) as usize, Ordering::Relaxed);
}

#[derive(Debug, Clone, Default)]
struct Watch {
    run: i64,
    lines: Vec<LogEntry>,
    /// Item name → count, inventory and temp inventory, as the thread last read them.
    counts: HashMap<String, i64>,
    /// (ts, item, count) whenever a farmed item's count changed while the thread watched.
    changes: Vec<(i64, String, i64)>,
}

fn watches() -> &'static Mutex<BTreeMap<String, Watch>> {
    static W: OnceLock<Mutex<BTreeMap<String, Watch>>> = OnceLock::new();
    W.get_or_init(Mutex::default)
}

fn now_ms() -> i64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |d| d.as_millis() as i64)
}

/// Reads every Engine's run's script lines and item counts every 2 s.
pub fn start(skua_dir: PathBuf) {
    std::thread::spawn(move || {
        let mut connections: HashMap<String, Engine> = HashMap::new();
        loop {
            for name in discovery::engine_names(&skua_dir) {
                if !connections.contains_key(&name) {
                    match Engine::connect(&discovery::socket_path(&skua_dir, &name), Duration::from_secs(5)) {
                        Ok(e) => _ = connections.insert(name.clone(), e),
                        Err(_) => continue,
                    }
                }
                let engine = connections.get_mut(&name).unwrap();
                let Ok(status) = engine.status() else {
                    connections.remove(&name);
                    continue;
                };
                let Some(run) = status.script.run else { continue };
                let lines: Vec<LogEntry> = engine
                    .logs_of("script", None, Some(2000))
                    .map(|p| p.entries.into_iter().filter(|e| e.run == Some(run.number)).collect())
                    .unwrap_or_default();
                let mut counts = HashMap::new();
                for kind in ["inventory", "temp"] {
                    if let Ok(inv) = engine.call::<Inventory>("inventory", json!([kind])) {
                        for item in inv.items {
                            *counts.entry(item.name).or_insert(0) += item.qty;
                        }
                    }
                }
                let mut all = watches().lock().unwrap();
                let w = all.entry(name).or_default();
                if w.run != run.number {
                    *w = Watch { run: run.number, ..Watch::default() };
                }
                if let Some(farm) = goal(&lines).farm {
                    let now = counts.get(&farm.item).copied().unwrap_or(0);
                    if w.counts.get(&farm.item).copied() != Some(now) && w.counts.contains_key(&farm.item) || w.changes.is_empty() {
                        w.changes.push((now_ms(), farm.item.clone(), now));
                    }
                }
                w.lines = lines;
                w.counts = counts;
            }
            std::thread::sleep(Duration::from_secs(2));
        }
    });
}

#[derive(Debug, Clone)]
struct Step {
    item: String,
    want: i64,
    ts: i64,
    start: i64,
}

#[derive(Debug, Default)]
struct Goal {
    quest: Option<(String, i64)>,
    buy: Option<Step>,
    farm: Option<Step>,
    kill: Option<(String, String, i64)>,
    resets: Vec<i64>,
}

/// The CoreBots text after `[hh:mm:ss] (Tag) `, with its tag.
fn split(entry: &LogEntry) -> Option<(&str, &str)> {
    let text = entry.text.as_deref()?;
    let text = text.split_once("] ").map_or(text, |(_, r)| r);
    let rest = text.strip_prefix('(')?;
    rest.split_once(") ")
}

/// `name (n/m)` or `name (#n/m)` → (name, n, m).
fn counted(text: &str) -> Option<(String, i64, i64)> {
    let (name, tail) = text.rsplit_once(" (")?;
    let (n, m) = tail.trim_end_matches(')').trim_start_matches('#').split_once('/')?;
    Some((name.trim().to_owned(), n.trim().parse().ok()?, m.trim().parse().ok()?))
}

fn goal(lines: &[LogEntry]) -> Goal {
    let mut g = Goal::default();
    for e in lines {
        let Some((tag, text)) = split(e) else { continue };
        if let Some(q) = text.strip_prefix("Doing Quest: [") {
            if let Some((_, name)) = q.split_once("] - ") {
                g.quest = Some((name.trim_matches('"').to_owned(), e.ts));
            }
        } else if let Some(rest) = text.strip_prefix("Farming to buy ") {
            if let Some((item, n, m)) = counted(rest) {
                g.buy = Some(Step { item, want: m, ts: e.ts, start: n });
                g.farm = None;
                g.resets.clear();
            }
        } else if let Some(rest) = text.strip_prefix("Farming ") {
            if let Some((item, n, m)) = counted(rest)
                && g.farm.as_ref().is_none_or(|f| f.item != item)
            {
                g.farm = Some(Step { item, want: m, ts: e.ts, start: n });
                g.resets.clear();
            }
        } else if text.starts_with("Death - Resetting") {
            g.resets.push(e.ts);
        } else if text.contains("Killing ") {
            let k = &text[text.find("Killing ").unwrap() + 8..];
            if let Some((monster, item)) = k.split_once(" for ") {
                let item = item.trim_start_matches("item: ").trim_matches('"');
                g.kill = Some((monster.to_owned(), item.split(" (").next().unwrap_or(item).trim_end_matches('"').to_owned(), e.ts));
            }
        }
        let _ = tag;
    }
    g
}

fn switcher(frame: &mut Frame, area: Rect) {
    let i = VARIANT.load(Ordering::Relaxed);
    let text = format!(" ◀  {} · {}  ▶   ←/→ ", (b'A' + i as u8) as char, VARIANTS[i]);
    let w = text.chars().count() as u16;
    if area.width < w + 2 {
        return;
    }
    let pill = Rect { x: area.x + (area.width - w) / 2, y: area.y + area.height - 1, width: w, height: 1 };
    frame.render_widget(Clear, pill);
    frame.render_widget(Paragraph::new(text.black().on_yellow().bold()), pill);
}

/// Draws the Goal panel at the top of `area` and returns what is left below it.
pub fn render(frame: &mut Frame, name: &str, status: &Status, area: Rect) -> Rect {
    let w = watches().lock().unwrap().get(name).cloned().unwrap_or_default();
    let g = goal(&w.lines);
    let script = status.script.run.as_ref().map(|r| r.script.rsplit('/').next().unwrap_or("").trim_end_matches(".cs").to_owned()).unwrap_or_default();
    let have = |item: &str| w.counts.get(item).copied().unwrap_or(0);
    let width = area.width.saturating_sub(2);
    let lines = match VARIANT.load(Ordering::Relaxed) {
        1 => breadcrumb(&g, &script, &have, width),
        2 => timeline(&g, &w, &have, width),
        _ => tree(&g, &script, &have),
    };
    let height = (lines.len() as u16 + 2).min(area.height);
    let top = Rect { height, ..area };
    frame.render_widget(Paragraph::new(lines).block(panel("Goal")), top);
    switcher(frame, top);
    Rect { y: top.bottom(), height: area.height - height, ..area }
}

/// Items an hour since the farm step began, from its logged start count, and the time left at that rate.
fn pace(step: &Step, have: i64) -> Option<(f64, f64)> {
    let hours = (now_ms() - step.ts) as f64 / 3_600_000.0;
    let gained = (have - step.start) as f64;
    (hours > 0.02 && gained > 0.0).then(|| {
        let rate = gained / hours;
        (rate, (step.want - have).max(0) as f64 / rate * 3600.0)
    })
}

fn resets_line(g: &Goal) -> Option<Line<'static>> {
    let first = *g.resets.first()?;
    Some(Line::styled(
        format!(
            "⟲ wave reset {}× in {}: the leader died, which restarts the wave · last {} ago",
            g.resets.len(),
            short_duration((now_ms() - first) as f64 / 1000.0),
            short_duration((now_ms() - g.resets.last().unwrap()) as f64 / 1000.0)
        ),
        Style::new().yellow(),
    ))
}

/// A: the chain as a tree, each level what the one above needs.
fn tree(g: &Goal, script: &str, have: &dyn Fn(&str) -> i64) -> Vec<Line<'static>> {
    let mut lines = vec![Line::from(vec!["GOAL  ".fg(DIM).bold(), script.to_owned().magenta().bold()])];
    let mut indent = 1;
    let mut push = |kind: &str, text: String, right: Vec<Span<'static>>| {
        let mut spans = vec![Span::styled(format!("{}└ {kind:<6}", "   ".repeat(indent - 1)), Style::new().fg(DIM)), Span::raw(format!("{text:<38}"))];
        spans.extend(right);
        lines.push(Line::from(spans));
        indent += 1;
    };
    if let Some((quest, _)) = &g.quest {
        push("quest", quest.clone(), vec![]);
    }
    if let Some(b) = &g.buy {
        let n = have(&b.item);
        push("buy", b.item.clone(), vec![Span::raw(format!("{n}/{}", b.want))]);
    }
    if let Some(f) = &g.farm {
        let n = have(&f.item);
        let mut right = vec![Span::styled(format!("{n}/{}", f.want), Style::new().bold())];
        if let Some((rate, left)) = pace(f, n) {
            right.push(Span::styled(format!("   +{}/h  ~{}", rate_text(rate), short_duration(left)), Style::new().fg(DIM)));
        }
        push("farm", f.item.clone(), right);
    }
    if let Some((monster, item, ts)) = &g.kill {
        push("now", format!("killing {monster}"), vec![Span::styled(format!("for {item} · {} ago", short_duration((now_ms() - ts) as f64 / 1000.0)), Style::new().fg(DIM))]);
    }
    lines.extend(resets_line(g));
    lines
}

/// B: the chain as one breadcrumb, then a bar for each level with a count.
fn breadcrumb(g: &Goal, script: &str, have: &dyn Fn(&str) -> i64, width: u16) -> Vec<Line<'static>> {
    let mut crumbs = vec![script.to_owned()];
    crumbs.extend(g.quest.as_ref().map(|(q, _)| q.clone()));
    crumbs.extend(g.buy.as_ref().map(|b| b.item.clone()));
    crumbs.extend(g.farm.as_ref().map(|f| f.item.clone()));
    let mut lines = vec![Line::from(
        crumbs
            .iter()
            .enumerate()
            .flat_map(|(i, c)| {
                let last = i + 1 == crumbs.len();
                let mut s = vec![if last { c.clone().bold() } else { c.clone().fg(DIM) }];
                if !last {
                    s.push(" › ".fg(DIM));
                }
                s
            })
            .collect::<Vec<_>>(),
    )];
    lines.push(Line::raw(""));
    let bar = width.saturating_sub(40).clamp(16, 40);
    for step in [&g.buy, &g.farm].into_iter().flatten() {
        let n = have(&step.item);
        let mut spans = vec![Span::raw(format!("{:<30}", step.item.chars().take(29).collect::<String>()))];
        spans.extend(gauge("", bar, n as f64 / step.want.max(1) as f64, Color::Green, format!("{n}/{}", step.want)).spans.into_iter().skip(1));
        if let Some((rate, left)) = pace(step, n) {
            spans.push(Span::styled(format!("  ~{} left at {}/h", short_duration(left), rate_text(rate)), Style::new().fg(DIM)));
        }
        lines.push(Line::from(spans));
    }
    if let Some((monster, item, _)) = &g.kill {
        lines.push(Line::from(vec!["now  ".fg(DIM), format!("killing {monster} for {item}").into()]));
    }
    lines.extend(resets_line(g));
    lines
}

/// C: what happened, in order: the steps the Script logged, each reset, and each count change the TUI saw.
fn timeline(g: &Goal, w: &Watch, have: &dyn Fn(&str) -> i64, _width: u16) -> Vec<Line<'static>> {
    let mut events: Vec<(i64, Line<'static>)> = Vec::new();
    if let Some((q, ts)) = &g.quest {
        events.push((*ts, Line::raw(format!("doing quest {q}"))));
    }
    if let Some(b) = &g.buy {
        events.push((b.ts, Line::raw(format!("needs {} ×{} → farming its materials", b.item, b.want))));
    }
    if let Some(f) = &g.farm {
        events.push((f.ts, Line::raw(format!("farming {} ({}/{})", f.item, f.start, f.want))));
    }
    for ts in &g.resets {
        events.push((*ts, Line::styled("died → wave reset, back to the first crew", Style::new().yellow())));
    }
    let mut last: Option<i64> = None;
    for (ts, item, n) in &w.changes {
        if let Some(prev) = last
            && *n > prev
        {
            events.push((*ts, Line::styled(format!("+{} {item} ({n})", n - prev), Style::new().green())));
        }
        last = Some(*n);
    }
    events.sort_by_key(|(ts, _)| *ts);
    let keep = events.len().saturating_sub(8);
    let mut lines: Vec<Line<'static>> = events
        .into_iter()
        .skip(keep)
        .map(|(ts, line)| {
            let mut spans = vec![Span::styled(format!("{}  ", local_time(ts)), Style::new().fg(DIM))];
            spans.extend(line.spans);
            Line::from(spans)
        })
        .collect();
    if let Some(f) = &g.farm {
        let n = have(&f.item);
        lines.push(Line::from(vec!["now   ".fg(DIM), format!("{} {n}/{}", f.item, f.want).bold()]));
    }
    lines
}
