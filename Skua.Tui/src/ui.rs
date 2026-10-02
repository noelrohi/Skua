//! Accounts by group on the left; the selected account's Engine in tabs on the right; a status line and the keys at the bottom.

use ratatui::Frame;
use ratatui::layout::{Alignment, Rect};
use ratatui::style::{Color, Modifier, Style, Stylize};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, BorderType, Borders, Clear, Paragraph, Wrap};
use serde_json::Value;

use crate::app::{App, KEYS, Modal, Row, Tab};
use crate::discovery::MANAGER_FILE;
use crate::dto::{GameState, Hello, LogEntry, Player, Status};
use crate::engine::{Error, NOT_LOGGED_IN, PROTOCOL};
use crate::poller::{Detail, EngineView, LOG_TAIL};

const DIM: Color = Color::DarkGray;
const SELECTED: Color = Color::Indexed(237);

pub fn draw(frame: &mut Frame, app: &App) {
    let area = frame.area();
    if area.width < 40 || area.height < 10 {
        frame.render_widget(Paragraph::new("skua-tui needs at least 40×10"), area);
        return;
    }
    header(frame, app, Rect { height: 1, ..area });
    let body = Rect {
        y: 1,
        height: area.height - 3,
        ..area
    };
    let left = (area.width * 3 / 10).clamp(30, 38).min(area.width / 2);
    accounts(frame, app, Rect { width: left, ..body });
    right(
        frame,
        app,
        Rect {
            x: left,
            width: area.width - left,
            ..body
        },
    );
    status_line(
        frame,
        app,
        Rect {
            y: area.height - 2,
            height: 1,
            ..area
        },
    );
    footer(
        frame,
        Rect {
            y: area.height - 1,
            height: 1,
            ..area
        },
    );
    if let Some(modal) = &app.modal {
        draw_modal(frame, app, modal);
    }
}

fn header(frame: &mut Frame, app: &App, area: Rect) {
    let engines = app.snapshot.as_ref().map_or(0, |s| s.engines.len());
    let alerts = app
        .snapshot
        .as_ref()
        .map_or(0, |s| s.engines.values().filter(|v| is_alert(v)).count());
    let left = Line::from(vec![" skua ".black().on_blue().bold(), " windowless Engines".bold()]);
    let right = Line::from(vec![
        Span::raw(format!("{engines} Engines · ")),
        Span::styled(
            format!("{alerts}"),
            if alerts > 0 {
                Style::new().red().bold()
            } else {
                Style::new()
            },
        ),
        Span::raw(format!(
            " alert{} · protocol {PROTOCOL} ",
            if alerts == 1 { "" } else { "s" }
        )),
    ])
    .fg(DIM)
    .alignment(Alignment::Right);
    frame.render_widget(Paragraph::new(left), area);
    frame.render_widget(Paragraph::new(right), area);
}

fn accounts(frame: &mut Frame, app: &App, area: Rect) {
    let rows = app.rows();
    let count = if !app.marks.is_empty() {
        Span::styled(format!(" {} marked ", app.marks.len()), Style::new().blue())
    } else if !app.filter.is_empty() {
        Span::styled(format!(" /{} ", app.filter), Style::new().fg(DIM))
    } else {
        Span::styled(format!(" {} ", rows.len()), Style::new().fg(DIM))
    };
    let block = panel("Accounts")
        .border_style(Style::new().blue())
        .title_top(Line::from(count).right_aligned());
    let inner = block.inner(area);
    frame.render_widget(block, area);

    let mut lines = Vec::new();
    if let Some(Err(error)) = app.snapshot.as_ref().map(|s| &s.accounts) {
        lines.push(Line::styled(error.clone(), Style::new().red().bold()));
    }
    if app.snapshot.is_none() {
        lines.push(Line::styled("reading…", Style::new().fg(DIM)));
    }
    let mut selected_line = 0;
    let mut group: Option<&str> = None;
    for (i, row) in rows.iter().enumerate() {
        if group != Some(row.group.as_str()) {
            group = Some(&row.group);
            let members = rows.iter().filter(|r| r.group == row.group).count();
            lines.push(Line::styled(
                format!("▾ {} · {members}", row.group),
                Style::new().fg(DIM).bold(),
            ));
        }
        if i == app.selected {
            selected_line = lines.len();
        }
        lines.push(account_line(app, row, i == app.selected, inner.width));
    }
    let scroll = (selected_line + 1).saturating_sub(inner.height as usize) as u16;
    frame.render_widget(Paragraph::new(lines).scroll((scroll, 0)), inner);
}

fn account_line(app: &App, row: &Row, selected: bool, width: u16) -> Line<'static> {
    let marked = app.marks.contains(&row.name);
    let view = app.engine(&row.name);
    let (state, color) = row_state(view);
    let flag = match view {
        Some(EngineView::Up { status, .. }) if !status.pending_dialogs.is_empty() => "?".yellow().bold(),
        Some(v) if is_alert(v) => "▲".red().bold(),
        _ => " ".into(),
    };
    let up = matches!(view, Some(EngineView::Up { .. }));
    let fixed = 3 + 2 + 2 + 13 + 1;
    let state_width = (width as usize).saturating_sub(fixed);
    let mut line = Line::from(vec![
        if marked { "[x]".blue().bold() } else { "[ ]".fg(DIM) },
        " ".into(),
        Span::styled("●", Style::new().fg(color)),
        " ".into(),
        Span::styled(
            format!("{:<12}", truncate(&row.name, 12)),
            if up { Style::new().bold() } else { Style::new().fg(DIM) },
        ),
        " ".into(),
        Span::styled(
            format!("{:<state_width$}", truncate(&state, state_width)),
            Style::new().fg(color),
        ),
        flag,
    ]);
    if selected {
        line = line.bg(SELECTED);
    }
    line
}

fn row_state(view: Option<&EngineView>) -> (String, Color) {
    match view {
        None | Some(EngineView::Offline) => ("offline".into(), DIM),
        Some(EngineView::Failed(Error::ProtocolMismatch { protocol, .. })) => {
            (format!("protocol {protocol}"), Color::Red)
        }
        Some(EngineView::Failed(_)) => ("not answering".into(), Color::Red),
        Some(EngineView::Up { status, .. }) => match &status.script.run {
            Some(run) => (
                base_name(&run.script).trim_end_matches(".cs").to_owned(),
                Color::Magenta,
            ),
            None => {
                let (text, color) = state(status);
                (text.into(), color)
            }
        },
    }
}

fn is_alert(view: &EngineView) -> bool {
    match view {
        EngineView::Failed(_) => true,
        EngineView::Up { status, .. } => state(status).1 == Color::Red,
        EngineView::Offline => false,
    }
}

fn playing(status: &Status) -> Option<&Player> {
    status
        .game
        .player
        .as_ref()
        .filter(|_| status.game.state == GameState::Playing)
}

/// The game state in a word or two, and its color.
fn state(status: &Status) -> (&'static str, Color) {
    match status.game.state {
        GameState::Playing if playing(status).is_some_and(|p| !p.alive) => ("dead", Color::Red),
        GameState::Playing => ("playing", Color::Green),
        GameState::NotStarted if !status.game.game_host_up => ("no Game Host", DIM),
        GameState::NotStarted => ("game loading", DIM),
        GameState::LoginScreen => ("login screen", Color::Blue),
        GameState::LoggingIn => ("logging in", Color::Yellow),
        GameState::Disconnected => ("disconnected", Color::Red),
    }
}

fn right(frame: &mut Frame, app: &App, area: Rect) {
    let row = app.selected_row();
    let mut tabs = vec![Span::raw(" ")];
    for tab in Tab::ALL {
        let title = format!(" {} ", tab.title());
        tabs.push(if tab == app.tab {
            title.black().on_blue().bold()
        } else {
            title.fg(DIM)
        });
        tabs.push("│".fg(DIM));
    }
    let tabs_area = Rect { height: 1, ..area };
    frame.render_widget(Paragraph::new(Line::from(tabs)), tabs_area);
    if let Some(row) = &row {
        frame.render_widget(
            Paragraph::new(Line::from(format!("{} ", row.name).bold()).right_aligned()),
            tabs_area,
        );
    }

    let content = Rect {
        y: area.y + 1,
        height: area.height - 1,
        ..area
    };
    let Some(row) = row else {
        return no_accounts(frame, app, content);
    };
    match app.engine(&row.name) {
        None | Some(EngineView::Offline) => no_engine(frame, &row.name, content),
        Some(EngineView::Failed(error @ Error::ProtocolMismatch { .. })) => {
            failure(frame, "Protocol mismatch", &error.to_string(), content)
        }
        Some(EngineView::Failed(error)) => failure(frame, "Engine not answering", &error.to_string(), content),
        Some(EngineView::Up { hello, status }) => {
            let detail = app
                .snapshot
                .as_ref()
                .map(|s| &s.detail)
                .filter(|d| d.engine.as_deref() == Some(row.name.as_str()));
            match app.tab {
                Tab::Overview => overview(frame, hello, status, detail, content),
                Tab::Inventory => inventory(frame, status, detail, content),
                Tab::Quests => quests(frame, status, detail, content),
                Tab::Logs => {
                    let block =
                        panel("Logs + events").title_top(dim_right(&format!("logs --tail {LOG_TAIL}, following")));
                    logs(frame, block, detail, content)
                }
                Tab::Game => game(frame, status, detail, content),
            }
        }
    }
}

fn no_accounts(frame: &mut Frame, app: &App, area: Rect) {
    let block = panel("No accounts");
    let text = if app.filter.is_empty() {
        vec![
            Line::from(format!(
                "No accounts in {}, and no Engine under {}.",
                MANAGER_FILE,
                app.skua_dir.join("engines").display()
            )),
            Line::styled(
                "Add accounts in the Skua Manager (Skua --manager); skua-tui only reads them.",
                Style::new().fg(DIM),
            ),
        ]
    } else {
        vec![Line::from(format!(
            "No account matches /{}; esc clears the filter.",
            app.filter
        ))]
    };
    frame.render_widget(Paragraph::new(text).wrap(Wrap { trim: true }).block(block), area);
}

fn no_engine(frame: &mut Frame, name: &str, area: Rect) {
    let mut lines = vec![
        Line::from(format!("{name} is offline: no Engine runs for it.")),
        Line::raw(""),
    ];
    for (key, what) in [
        ("E", "start a windowless Engine for it (not yet)"),
        ("L", "then log in, picking a server (not yet)"),
        ("s", "then start a Script, with its options (not yet)"),
        ("space", "mark several accounts to act on them all"),
    ] {
        lines.push(Line::from(vec![format!("{key:<6} ").blue().bold(), what.fg(DIM)]));
    }
    frame.render_widget(Paragraph::new(lines).block(panel("No Engine")), area);
}

fn failure(frame: &mut Frame, title: &str, message: &str, area: Rect) {
    let block = panel(title)
        .border_style(Style::new().red())
        .title_style(Style::new().red().bold());
    let text = Paragraph::new(Line::styled(message.to_owned(), Style::new().red().bold())).wrap(Wrap { trim: true });
    frame.render_widget(text.block(block), area);
}

fn overview(frame: &mut Frame, hello: &Hello, status: &Status, detail: Option<&Detail>, area: Rect) {
    let top = 8.min(area.height);
    let mut rest = Rect {
        y: area.y + top,
        height: area.height - top,
        ..area
    };
    match playing(status) {
        Some(player) => {
            let half = area.width / 2;
            let title = match status.game.player_age_sec {
                Some(age) => format!("Player · stale {age:.0}s"),
                None => "Player".into(),
            };
            let player_area = Rect {
                width: half,
                height: top,
                ..area
            };
            frame.render_widget(
                Paragraph::new(player_lines(player, status, half.saturating_sub(4))).block(panel(&title)),
                player_area,
            );
            let script_area = Rect {
                x: area.x + half,
                width: area.width - half,
                height: top,
                ..area
            };
            let running = status.script.run.is_some();
            let block = panel("Script")
                .title_top(Line::from(if running { " running ".green() } else { " idle ".fg(DIM) }).right_aligned());
            frame.render_widget(Paragraph::new(script_lines(hello, status)).block(block), script_area);
        }
        None => {
            let block = panel("Engine")
                .title_top(Line::from(format!(" {} ", state(status).0).fg(state(status).1)).right_aligned());
            let mut lines = vec![
                Line::styled(engine_summary(hello, status), Style::new().fg(DIM)),
                Line::raw(""),
                match status.game.state {
                    GameState::LoginScreen => Line::from(vec![
                        "L  ".blue().bold(),
                        "log in, with a server picker (not yet)".fg(DIM),
                    ]),
                    GameState::NotStarted if !status.game.game_host_up => {
                        Line::styled("… starting the Game Host", Style::new().fg(DIM))
                    }
                    GameState::NotStarted => Line::styled("… the game is loading", Style::new().fg(DIM)),
                    GameState::Disconnected => Line::styled(
                        format!(
                            "disconnected{}",
                            status
                                .game
                                .server
                                .as_deref()
                                .map(|s| format!(" from {s}"))
                                .unwrap_or_default()
                        ),
                        Style::new().red(),
                    ),
                    _ => Line::styled(format!("… {}", state(status).0), Style::new().fg(DIM)),
                },
            ];
            if let Some(last) = &status.script.last_run {
                lines.push(Line::raw(""));
                lines.push(Line::styled(
                    format!("last Script: {} {}", base_name(&last.script), last.outcome),
                    Style::new().yellow(),
                ));
            }
            frame.render_widget(Paragraph::new(lines).block(block), Rect { height: top, ..area });
        }
    }
    if let Some(question) = status.pending_dialogs.first() {
        let height = (question.choices.len() as u16 + 3).min(rest.height);
        let block = panel(&format!("Question · {} · answering: not yet", question.caption))
            .border_style(Style::new().yellow())
            .title_style(Style::new().yellow().bold());
        let mut lines = vec![Line::styled(question.text.clone(), Style::new().yellow().bold())];
        lines.extend(
            question
                .choices
                .iter()
                .enumerate()
                .map(|(i, c)| Line::raw(format!("  {}  {c}", i + 1))),
        );
        frame.render_widget(Paragraph::new(lines).block(block), Rect { height, ..rest });
        rest = Rect {
            y: rest.y + height,
            height: rest.height - height,
            ..rest
        };
    }
    logs(
        frame,
        panel("Logs").title_top(dim_right(&format!("logs --tail {LOG_TAIL}"))),
        detail,
        rest,
    );
}

fn engine_summary(hello: &Hello, status: &Status) -> String {
    format!(
        "{} · build {} · pid {} · up {}",
        status.engine.name,
        hello.build,
        status.engine.pid,
        duration(status.engine.uptime_sec)
    )
}

fn player_lines(player: &Player, status: &Status, gauge_width: u16) -> Vec<Line<'static>> {
    let tag = if !player.alive {
        "dead".red().bold()
    } else if player.in_combat {
        "in combat".light_red().bold()
    } else {
        "idle".fg(DIM)
    };
    let xp = match player.xp_percent {
        Some(percent) => (
            player.xp as f64 / player.required_xp.max(1) as f64,
            format!("{percent}%"),
        ),
        None => (1.0, "max level".into()),
    };
    let hp = ratio(player.hp, player.max_hp);
    vec![
        Line::from(vec![
            player.name.clone().bold(),
            format!(
                "  Lv {}  {}  ",
                player.level,
                player.class.as_deref().unwrap_or("no class")
            )
            .fg(DIM),
            tag,
        ]),
        gauge(
            "HP",
            gauge_width,
            hp,
            if hp > 0.5 {
                Color::Green
            } else if hp > 0.25 {
                Color::Yellow
            } else {
                Color::Red
            },
            format!("{}/{}", player.hp, player.max_hp),
        ),
        gauge(
            "MP",
            gauge_width,
            ratio(player.mp, player.max_mp),
            Color::Blue,
            format!("{}/{}", player.mp, player.max_mp),
        ),
        gauge("XP", gauge_width, xp.0, Color::Magenta, xp.1),
        Line::styled(format!("gold {}", thousands(player.gold)), Style::new().yellow()),
        Line::raw(format!(
            "{} · {} · {} · {}",
            player.map,
            player.cell,
            player.pad,
            status.game.server.as_deref().unwrap_or("?")
        )),
    ]
}

fn script_lines(hello: &Hello, status: &Status) -> Vec<Line<'static>> {
    let mut lines = match (&status.script.run, &status.script.last_run) {
        (Some(run), _) => vec![
            Line::styled(run.script.clone(), Style::new().magenta().bold()),
            Line::raw(format!(
                "run {} · {}{}{}",
                run.number,
                duration(run.elapsed_sec),
                if run.relogins > 0 {
                    format!(" · {} relogins", run.relogins)
                } else {
                    String::new()
                },
                if run.relogging_in { " · relogging in" } else { "" }
            )),
        ],
        (None, last) => vec![
            Line::styled("no Script running", Style::new().fg(DIM)),
            match last {
                Some(last) => Line::styled(
                    format!("last: {} {}", base_name(&last.script), last.outcome),
                    if last.outcome == "completed" {
                        Style::new().fg(DIM)
                    } else {
                        Style::new().yellow()
                    },
                ),
                None => Line::styled("last outcome: —", Style::new().fg(DIM)),
            },
        ],
    };
    lines.push(Line::raw(""));
    lines.push(Line::styled(
        format!("Engine {} · pid {}", status.engine.name, status.engine.pid),
        Style::new().fg(DIM),
    ));
    lines.push(Line::styled(
        format!("build {} · up {}", hello.build, duration(status.engine.uptime_sec)),
        Style::new().fg(DIM),
    ));
    lines
}

fn gauge(label: &str, width: u16, ratio: f64, color: Color, text: String) -> Line<'static> {
    let width = width as usize;
    let filled = (ratio.clamp(0.0, 1.0) * width as f64).round() as usize;
    let start = width.saturating_sub(text.chars().count()) / 2;
    let mut spans = vec![Span::styled(format!("{label} "), Style::new().fg(DIM))];
    let cells: Vec<char> = (0..width)
        .map(|i| {
            text.chars()
                .nth(i.wrapping_sub(start))
                .filter(|_| i >= start)
                .unwrap_or(' ')
        })
        .collect();
    let (done, todo) = cells.split_at(filled.min(width));
    spans.push(Span::styled(
        done.iter().collect::<String>(),
        Style::new().black().bg(color).bold(),
    ));
    spans.push(Span::styled(todo.iter().collect::<String>(), Style::new().bg(SELECTED)));
    Line::from(spans)
}

fn inventory(frame: &mut Frame, status: &Status, detail: Option<&Detail>, area: Rect) {
    let read = detail.and_then(|d| d.inventory.as_ref());
    let mut block = panel("Inventory");
    if let Some(Ok(inv)) = read {
        let slots = inv
            .total_slots
            .map_or(format!("{}", inv.used_slots), |t| format!("{}/{t}", inv.used_slots));
        let full = inv.total_slots.is_some_and(|t| inv.used_slots >= t);
        block = block.title_top(
            Line::from(format!(" {slots}{} ", if full { " FULL" } else { "" }))
                .style(if full { Style::new().red().bold() } else { Style::new() })
                .right_aligned(),
        );
    }
    let lines = match read {
        _ if playing(status).is_none() => vec![not_playing()],
        None => vec![loading()],
        Some(Err(e)) => vec![read_error(e)],
        Some(Ok(inv)) => {
            let mut lines = vec![Line::styled(
                format!("{:<34}{:<14}{}", "ITEM", "QTY", "CATEGORY"),
                Style::new().fg(DIM).bold(),
            )];
            lines.extend(inv.items.iter().map(|item| {
                let qty = if item.max_stack > 1 {
                    format!("{}/{}", item.qty, item.max_stack)
                } else {
                    item.qty.to_string()
                };
                let name = format!("{}{}", truncate(&item.name, 31), if item.equipped { " ✓" } else { "" });
                Line::raw(format!("{name:<34}{qty:<14}{}", item.category))
            }));
            lines
        }
    };
    frame.render_widget(Paragraph::new(lines).block(block), area);
}

fn quests(frame: &mut Frame, status: &Status, detail: Option<&Detail>, area: Rect) {
    let lines = match detail.and_then(|d| d.quests.as_ref()) {
        _ if playing(status).is_none() => vec![not_playing()],
        None => vec![loading()],
        Some(Err(e)) => vec![read_error(e)],
        Some(Ok(q)) if q.quests.is_empty() => vec![Line::styled("no quests loaded", Style::new().fg(DIM))],
        Some(Ok(q)) => q
            .quests
            .iter()
            .flat_map(|quest| {
                let (status, color) = match quest.status.as_str() {
                    "completable" => ("completable", Color::Green),
                    "inProgress" => ("in progress", Color::Yellow),
                    _ => ("not accepted", DIM),
                };
                let needs = quest
                    .requirements
                    .iter()
                    .map(|r| format!("{} {}/{}", r.name, r.have, r.qty))
                    .collect::<Vec<_>>()
                    .join(" · ");
                [
                    Line::from(vec![
                        format!("{:<7}{}  ", quest.id, quest.name).bold(),
                        Span::styled(status, Style::new().fg(color)),
                    ]),
                    Line::styled(format!("       {needs}"), Style::new().fg(DIM)),
                ]
            })
            .collect(),
    };
    frame.render_widget(Paragraph::new(lines).block(panel("Quests · loaded")), area);
}

fn game(frame: &mut Frame, status: &Status, detail: Option<&Detail>, area: Rect) {
    let read = detail.and_then(|d| d.map.as_ref());
    let title = match read {
        Some(Ok(map)) if playing(status).is_some() => format!("Game · {} · room {}", map.name, map.room_id),
        _ => "Game".into(),
    };
    let mut lines = match read {
        _ if playing(status).is_none() => vec![not_playing()],
        None => vec![loading()],
        Some(Err(e)) => vec![read_error(e)],
        Some(Ok(map)) => {
            let mut lines = vec![Line::raw(format!("cells: {}", map.cells.join(", "))), Line::raw("")];
            lines.push(Line::styled(
                format!("PLAYERS · {}", map.players.len()),
                Style::new().fg(DIM).bold(),
            ));
            lines.extend(map.players.iter().map(|p| {
                Line::raw(format!(
                    "  {:<20}Lv {:<4}{:<12}{}/{}{}",
                    p.name,
                    p.level,
                    p.cell,
                    p.hp,
                    p.max_hp,
                    if p.afk { "  afk" } else { "" }
                ))
            }));
            lines.push(Line::raw(""));
            lines.push(Line::styled(
                format!("MONSTERS · {}", map.monsters.len()),
                Style::new().fg(DIM).bold(),
            ));
            lines.extend(map.monsters.iter().map(|m| {
                let style = if m.alive { Style::new() } else { Style::new().fg(DIM) };
                Line::styled(
                    format!("  {:<4}{:<24}{:<12}{}/{}", m.map_id, m.name, m.cell, m.hp, m.max_hp),
                    style,
                )
            }));
            lines
        }
    };
    lines.push(Line::raw(""));
    lines.push(Line::styled("The game's picture: not yet.", Style::new().fg(DIM)));
    frame.render_widget(Paragraph::new(lines).block(panel(&title)), area);
}

fn logs(frame: &mut Frame, block: Block, detail: Option<&Detail>, area: Rect) {
    let inner = block.inner(area);
    let lines: Vec<Line> = match detail {
        None => vec![loading()],
        Some(d) if d.logs.is_empty() => vec![Line::styled("no entries yet", Style::new().fg(DIM))],
        Some(d) => {
            let skip = d.logs.len().saturating_sub(inner.height as usize);
            d.logs[skip..].iter().map(log_line).collect()
        }
    };
    frame.render_widget(Paragraph::new(lines).block(block), area);
}

fn log_line(entry: &LogEntry) -> Line<'static> {
    if entry.kind == "gap" {
        return Line::styled(entry.text.clone().unwrap_or_default(), Style::new().yellow());
    }
    let (label, color, text) = match &entry.event_type {
        Some(event) => (
            event.clone(),
            event_color(event),
            entry.data.as_ref().map(fields).unwrap_or_default(),
        ),
        None => (
            entry.kind.clone(),
            match entry.kind.as_str() {
                "debug" => DIM,
                "flash" => Color::Cyan,
                _ => Color::Reset,
            },
            entry.text.clone().unwrap_or_default(),
        ),
    };
    Line::from(vec![
        format!("{} ", local_time(entry.ts)).fg(DIM),
        Span::styled(
            format!("{label:<18} "),
            Style::new().fg(color).add_modifier(if entry.event_type.is_some() {
                Modifier::BOLD
            } else {
                Modifier::empty()
            }),
        ),
        Span::styled(
            text,
            if entry.event_type.is_some() {
                Style::new().fg(DIM)
            } else {
                Style::new()
            },
        ),
    ])
}

fn event_color(event: &str) -> Color {
    match event.split('.').next().unwrap_or("") {
        "player" => Color::Red,
        "inventory" => Color::LightRed,
        "script" => Color::Magenta,
        "question" | "notice" => Color::Yellow,
        "game" | "map" => Color::Blue,
        _ => Color::Cyan,
    }
}

/// An event's fields as `key=value`, strings unquoted.
fn fields(data: &Value) -> String {
    match data {
        Value::Object(map) => map
            .iter()
            .map(|(k, v)| match v {
                Value::String(s) => format!("{k}={s}"),
                v => format!("{k}={v}"),
            })
            .collect::<Vec<_>>()
            .join(" "),
        v => v.to_string(),
    }
}

fn status_line(frame: &mut Frame, app: &App, area: Rect) {
    let line = match &app.toast {
        Some(toast) => Line::styled(format!(" {toast}"), Style::new().yellow().bold()),
        None => Line::styled(
            format!(
                " acting on: {}   ·   space mark  a mark group  : commands  / filter  ? keys",
                app.targets_label()
            ),
            Style::new().fg(DIM),
        ),
    };
    frame.render_widget(Paragraph::new(line), area);
}

fn footer(frame: &mut Frame, area: Rect) {
    let mut spans = vec![Span::raw(" ")];
    for (key, what) in [
        ("j/k", "move"),
        ("space", "mark"),
        ("tab", "tab"),
        (":", "commands"),
        ("/", "filter"),
        ("?", "keys"),
        ("q", "quit"),
    ] {
        spans.push(key.blue().bold());
        spans.push(format!(" {what}  ").fg(DIM));
    }
    frame.render_widget(Paragraph::new(Line::from(spans)), area);
}

fn draw_modal(frame: &mut Frame, app: &App, modal: &Modal) {
    let screen = frame.area();
    let (title, lines, hint): (String, Vec<Line>, &str) = match modal {
        Modal::Palette { query, selected } => {
            let mut lines = vec![Line::from(format!("> {query}▏").bold()), Line::raw("")];
            let matches = App::palette_matches(query);
            if matches.is_empty() {
                lines.push(Line::styled("no command matches", Style::new().fg(DIM)));
            }
            for (i, command) in matches.iter().enumerate() {
                let line = Line::raw(format!("{:<40}{}", command.title, command.key));
                lines.push(if i == *selected {
                    line.black().on_blue().bold()
                } else {
                    line
                });
            }
            (
                format!("Commands · on {}", app.targets_label()),
                lines,
                "type to search · ↑↓ · enter run · esc",
            )
        }
        Modal::Filter { query } => (
            "Filter accounts".into(),
            vec![
                Line::from(format!("> {query}▏").bold()),
                Line::styled("part of a name; empty clears", Style::new().fg(DIM)),
            ],
            "enter ok · esc cancel",
        ),
        Modal::Help => (
            "Keys".into(),
            KEYS.iter()
                .map(|(key, what)| Line::from(vec![format!("{key:<10}").blue().bold(), Span::raw(*what)]))
                .collect(),
            "any key closes",
        ),
    };
    let width = 64.min(screen.width.saturating_sub(6));
    let height = (lines.len() as u16 + 4).min(screen.height.saturating_sub(2));
    let area = Rect {
        x: (screen.width - width) / 2,
        y: ((screen.height - height) / 3).max(1),
        width,
        height,
    };
    let block = panel(&title)
        .border_style(Style::new().blue())
        .title_bottom(Line::styled(format!(" {hint} "), Style::new().fg(DIM)));
    frame.render_widget(Clear, area);
    frame.render_widget(Paragraph::new(lines).block(block), area);
}

fn panel(title: &str) -> Block<'static> {
    Block::new()
        .borders(Borders::ALL)
        .border_type(BorderType::Rounded)
        .border_style(Style::new().fg(DIM))
        .title(Span::styled(format!(" {title} "), Style::new().bold()))
}

fn dim_right(text: &str) -> Line<'static> {
    Line::styled(format!(" {text} "), Style::new().fg(DIM)).right_aligned()
}

fn not_playing() -> Line<'static> {
    Line::styled("unknown while not playing", Style::new().fg(DIM))
}

fn loading() -> Line<'static> {
    Line::styled("reading…", Style::new().fg(DIM))
}

fn read_error(error: &Error) -> Line<'static> {
    match error {
        Error::Remote {
            code: NOT_LOGGED_IN, ..
        } => not_playing(),
        e => Line::styled(e.to_string(), Style::new().red()),
    }
}

/// `HH:MM:SS` in this Mac's time zone, of UTC milliseconds since the Unix epoch.
fn local_time(ts: i64) -> String {
    let secs = ts.div_euclid(1000) as libc::time_t;
    // SAFETY: localtime_r only writes the tm it is given.
    let mut tm: libc::tm = unsafe { std::mem::zeroed() };
    if unsafe { libc::localtime_r(&secs, &mut tm) }.is_null() {
        let day = secs.rem_euclid(86_400);
        return format!("{:02}:{:02}:{:02}", day / 3600, day / 60 % 60, day % 60);
    }
    format!("{:02}:{:02}:{:02}", tm.tm_hour, tm.tm_min, tm.tm_sec)
}

fn ratio(value: i64, max: i64) -> f64 {
    if max > 0 { value as f64 / max as f64 } else { 0.0 }
}

fn base_name(path: &str) -> &str {
    path.rsplit(['/', '\\']).next().unwrap_or(path)
}

fn truncate(text: &str, width: usize) -> String {
    if text.chars().count() <= width {
        text.to_owned()
    } else {
        let mut out: String = text.chars().take(width.saturating_sub(1)).collect();
        out.push('…');
        out
    }
}

fn duration(sec: f64) -> String {
    let sec = sec.max(0.0) as u64;
    match (sec / 3600, sec / 60 % 60, sec % 60) {
        (0, 0, s) => format!("{s}s"),
        (0, m, s) => format!("{m}m {s:02}s"),
        (h, m, _) => format!("{h}h {m:02}m"),
    }
}

fn thousands(n: i64) -> String {
    let digits = n.unsigned_abs().to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(c);
    }
    if n < 0 { format!("-{out}") } else { out }
}
