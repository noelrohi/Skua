//! PROTOTYPE, throwaway: three cleaner layouts for the Quests tab, next to the current one. Run with `SKUA_TUI_PROTOTYPE=1`, open the
//! Quests tab and flip with ←/→. Question: what should a clean, uncluttered Quests tab look like?

use std::sync::atomic::{AtomicUsize, Ordering};

use ratatui::Frame;
use ratatui::layout::Rect;
use ratatui::style::{Color, Style, Stylize};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Clear, Paragraph};

use super::{DIM, STALL_SEC, idle_color, panel, quest_lines, short_duration, truncate};
use crate::dto::{Quest, QuestRequirement, Quests, Status};

pub const VARIANTS: [&str; 4] = ["Current", "Focus", "Table", "Pulse"];

static VARIANT: AtomicUsize = AtomicUsize::new(0);

pub fn enabled() -> bool {
    std::env::var("SKUA_TUI_PROTOTYPE").is_ok_and(|v| v == "1")
}

pub fn cycle(by: isize) {
    let n = VARIANTS.len() as isize;
    let next = (VARIANT.load(Ordering::Relaxed) as isize + by).rem_euclid(n);
    VARIANT.store(next as usize, Ordering::Relaxed);
}

pub fn render(frame: &mut Frame, status: &Status, quests: &Quests, area: Rect) {
    let running = status.script.run.is_some();
    let width = area.width.saturating_sub(2) as usize;
    let (title, lines) = match VARIANT.load(Ordering::Relaxed) {
        1 => ("Quests", focus(quests, running, width)),
        2 => ("Quests", table(quests, running, width)),
        3 => ("Quests", pulse(status, quests, width)),
        _ => (
            "Quests · loaded",
            quests
                .quests
                .iter()
                .flat_map(|q| quest_lines(q, running, width))
                .collect(),
        ),
    };
    frame.render_widget(Paragraph::new(lines).block(panel(title)), area);
    switcher(frame, area);
}

/// The floating pill: obviously not part of the design.
fn switcher(frame: &mut Frame, area: Rect) {
    let i = VARIANT.load(Ordering::Relaxed);
    let text = format!(" ◀  {} · {}  ▶   ←/→ ", (b'A' + i as u8) as char, VARIANTS[i]);
    let w = text.chars().count() as u16;
    if area.width < w + 2 || area.height < 3 {
        return;
    }
    let pill = Rect {
        x: area.x + (area.width - w) / 2,
        y: area.y + area.height - 2,
        width: w,
        height: 1,
    };
    frame.render_widget(Clear, pill);
    frame.render_widget(Paragraph::new(text.black().on_yellow().bold()), pill);
}

fn accepted(q: &Quest) -> bool {
    q.status == "inProgress"
}

fn unmet(q: &Quest) -> impl Iterator<Item = &QuestRequirement> {
    q.requirements.iter().filter(|r| r.have < r.qty)
}

/// How long since any unmet requirement rose.
fn quest_idle(q: &Quest) -> Option<f64> {
    unmet(q).filter_map(|r| r.idle_sec).reduce(f64::min)
}

/// The average of each requirement's share done, so one 0/400 doesn't drown five 1/1s.
fn done_ratio(q: &Quest) -> f64 {
    if q.requirements.is_empty() {
        return 1.0;
    }
    q.requirements
        .iter()
        .map(|r| (r.have.min(r.qty) as f64) / r.qty.max(1) as f64)
        .sum::<f64>()
        / q.requirements.len() as f64
}

fn rate_eta(r: &QuestRequirement) -> Option<String> {
    let rate = r.gain_per_hour.filter(|rate| *rate > 0.0)?;
    let left = (r.qty - r.have) as f64 / rate * 3600.0;
    let rate = if rate >= 10.0 {
        format!("{rate:.0}")
    } else {
        format!("{rate:.1}")
    };
    Some(format!("+{rate}/h  ~{}", short_duration(left)))
}

fn bar(ratio: f64, width: usize, color: Color) -> Vec<Span<'static>> {
    let filled = (ratio.clamp(0.0, 1.0) * width as f64).round() as usize;
    vec![
        Span::styled("━".repeat(filled), Style::new().fg(color)),
        Span::styled("─".repeat(width - filled), Style::new().fg(Color::Indexed(238))),
    ]
}

fn others(quests: &Quests) -> Line<'static> {
    let completable = quests.quests.iter().filter(|q| q.status == "completable").count();
    let waiting = quests.quests.iter().filter(|q| q.status == "notAccepted").count();
    let mut parts = Vec::new();
    if completable > 0 {
        parts.push(format!("{completable} completable"));
    }
    if waiting > 0 {
        parts.push(format!("{waiting} not accepted"));
    }
    Line::styled(format!(" {}", parts.join(" · ")), Style::new().fg(DIM))
}

/// A: only what's being worked on. Accepted quests, their unmet requirements one per line, idle in a right-hand column; the rest is a count.
fn focus(quests: &Quests, running: bool, width: usize) -> Vec<Line<'static>> {
    let mut lines = vec![Line::raw("")];
    let active: Vec<&Quest> = quests.quests.iter().filter(|q| accepted(q)).collect();
    if active.is_empty() {
        lines.push(Line::styled(" No quest accepted", Style::new().fg(DIM)));
    }
    for q in active {
        let met = q.requirements.len() - unmet(q).count();
        let right = format!("{met}/{} done", q.requirements.len());
        let idle = quest_idle(q);
        let idle_text = idle
            .map(|s| format!("  ·  idle {}", short_duration(s)))
            .unwrap_or_default();
        let name_w = width
            .saturating_sub(right.len() + idle_text.chars().count() + 2)
            .min(40);
        lines.push(Line::from(vec![
            format!(" {:<name_w$}", truncate(&q.name, name_w)).bold(),
            Span::styled(right, Style::new().fg(DIM)),
            Span::styled(idle_text, Style::new().fg(idle.map_or(DIM, |s| idle_color(s, running)))),
        ]));
        for r in unmet(q) {
            let count = format!("{}/{}", r.have, r.qty);
            let tail = rate_eta(r).unwrap_or_default();
            let idle = r.idle_sec.map(short_duration).unwrap_or_default();
            let name_w = width.saturating_sub(4 + 12 + 18 + 7).min(29);
            lines.push(Line::from(vec![
                Span::raw(format!("   {:<name_w$} ", truncate(&r.name, name_w))),
                Span::styled(format!("{count:>12}"), Style::new()),
                Span::styled(format!("{tail:>18}"), Style::new().fg(DIM)),
                Span::styled(
                    format!("{idle:>7}"),
                    Style::new().fg(r.idle_sec.map_or(DIM, |s| idle_color(s, running))),
                ),
            ]));
        }
        lines.push(Line::raw(""));
    }
    lines.push(others(quests));
    lines
}

/// B: one aligned table of every unmet requirement of the accepted quests, with a bar per row; the not-accepted quests by name at the foot.
fn table(quests: &Quests, running: bool, width: usize) -> Vec<Line<'static>> {
    let quest_w = 18;
    let fixed = 1 + quest_w + 2 + 10 + 2 + 12 + 2 + 6 + 2 + 16;
    let item_w = width.saturating_sub(fixed).clamp(10, 32);
    let mut lines = vec![Line::styled(
        format!(
            " {:<quest_w$}  {:<item_w$}  {:>10}  {:<12}  {:>6}  {:>16}",
            "QUEST", "ITEM", "HAVE", "", "IDLE", "RATE · ETA"
        ),
        Style::new().fg(DIM).bold(),
    )];
    for q in quests.quests.iter().filter(|q| accepted(q)) {
        for (i, r) in unmet(q).enumerate() {
            let ratio = r.have as f64 / r.qty.max(1) as f64;
            let idle = r.idle_sec.unwrap_or(0.0);
            let mut spans = vec![
                Span::styled(
                    format!(
                        " {:<quest_w$}  ",
                        if i == 0 {
                            truncate(&q.name, quest_w)
                        } else {
                            String::new()
                        }
                    ),
                    Style::new().bold(),
                ),
                Span::raw(format!("{:<item_w$}  ", truncate(&r.name, item_w))),
                Span::raw(format!("{:>10}  ", format!("{}/{}", r.have, r.qty))),
            ];
            spans.extend(bar(
                ratio,
                12,
                if idle >= STALL_SEC && running {
                    Color::Red
                } else {
                    Color::Cyan
                },
            ));
            spans.push(Span::styled(
                format!("  {:>6}", r.idle_sec.map(short_duration).unwrap_or_default()),
                Style::new().fg(idle_color(idle, running)),
            ));
            spans.push(Span::styled(
                format!("  {:>16}", rate_eta(r).unwrap_or_default()),
                Style::new().fg(DIM),
            ));
            lines.push(Line::from(spans));
        }
    }
    lines.push(Line::raw(""));
    let waiting: Vec<String> = quests
        .quests
        .iter()
        .filter(|q| q.status == "notAccepted")
        .map(|q| q.name.clone())
        .collect();
    if !waiting.is_empty() {
        lines.push(Line::styled(" NOT ACCEPTED", Style::new().fg(DIM).bold()));
        let mut line = String::from(" ");
        for name in waiting {
            if line.chars().count() + name.chars().count() + 3 > width && line.len() > 1 {
                lines.push(Line::styled(
                    std::mem::replace(&mut line, String::from(" ")),
                    Style::new().fg(DIM),
                ));
            }
            if line.len() > 1 {
                line.push_str(" · ");
            }
            line.push_str(&name);
        }
        lines.push(Line::styled(line, Style::new().fg(DIM)));
    }
    lines
}

/// C: the answer first (is it moving?), then one line per accepted quest with a bar and what it still needs; nothing else.
fn pulse(status: &Status, quests: &Quests, width: usize) -> Vec<Line<'static>> {
    let run = status.script.run.as_ref();
    let headline = match run.and_then(|r| r.quest_idle_sec) {
        Some(s) if s >= STALL_SEC => Line::from(vec![
            " ▲ Stalled ".black().on_red().bold(),
            format!("  no quest progress for {}", short_duration(s)).red(),
        ]),
        Some(s) if s >= STALL_SEC / 2.0 => Line::from(vec![
            " ● Slowing ".black().on_yellow().bold(),
            format!("  last progress {} ago", short_duration(s)).yellow(),
        ]),
        Some(s) => Line::from(vec![
            " ● Progressing ".black().on_green().bold(),
            format!("  last progress {} ago", short_duration(s)).fg(DIM),
        ]),
        None if run.is_some() => Line::from(vec![" ○ No quest in progress ".black().on_dark_gray().bold()]),
        None => Line::from(vec![" ○ No Script running ".black().on_dark_gray().bold()]),
    };
    let mut lines = vec![Line::raw(""), headline, Line::raw(""), Line::raw("")];
    let name_w = 22;
    let bar_w = 16;
    for q in quests.quests.iter().filter(|q| accepted(q)) {
        let ratio = done_ratio(q);
        let mut spans = vec![Span::styled(
            format!(" {:<name_w$} ", truncate(&q.name, name_w)),
            Style::new().bold(),
        )];
        spans.extend(bar(ratio, bar_w, Color::Green));
        spans.push(Span::styled(format!(" {:>3.0}%", ratio * 100.0), Style::new().fg(DIM)));
        lines.push(Line::from(spans));
        let needs: Vec<String> = unmet(q)
            .map(|r| {
                if r.qty > 1 {
                    format!("{} {}/{}", r.name, r.have, r.qty)
                } else {
                    r.name.clone()
                }
            })
            .collect();
        let text = format!("needs {}", needs.join(", "));
        lines.push(Line::styled(
            format!(" {:name_w$} {}", "", truncate(&text, width.saturating_sub(name_w + 3))),
            Style::new().fg(DIM),
        ));
        lines.push(Line::raw(""));
    }
    lines.push(others(quests));
    lines
}
