//! Accounts by group on the left; the selected account's Engine in tabs on the right; a status line and the keys at the bottom.

use ratatui::Frame;
use ratatui::layout::{Alignment, Rect};
use ratatui::style::{Color, Modifier, Style, Stylize};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, BorderType, Borders, Clear, Paragraph, Wrap};
use serde_json::Value;

use crate::app::{App, Field, KEYS, Modal, Note, Row, Tab, Tone, first_line};
use crate::discovery::MANAGER_FILE;
use crate::dto::{GameState, Hello, HookRun, LogEntry, Player, Quest, ScriptRun, Status};
use crate::engine::{Error, NOT_LOGGED_IN, PROTOCOL};
use crate::poller::{Detail, EngineView, LOG_TAIL};

const DIM: Color = Color::DarkGray;
const SELECTED: Color = Color::Indexed(237);
/// How long a run's quests go without progress before they are stalled, as the Engine's `quest.stalled` says by default.
const STALL_SEC: f64 = 600.0;

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
        app,
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
        Span::raw(format!(" alert{} · ", if alerts == 1 { "" } else { "s" })),
        if hook_runner(app) {
            Span::raw("hooks running")
        } else {
            Span::styled("hooks off", Style::new().yellow())
        },
        Span::raw(format!(" · protocol {PROTOCOL} ")),
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
    let tone = app.activity.get(&row.name).map(|n| n.tone);
    let flag = match view {
        Some(EngineView::Up { status, .. }) if !status.pending_dialogs.is_empty() => "?".yellow().bold(),
        _ if tone == Some(Tone::Failed) => "!".red().bold(),
        Some(v) if is_alert(v) => "▲".red().bold(),
        _ if tone == Some(Tone::Pending) => "…".blue(),
        _ => " ".into(),
    };
    let up = matches!(view, Some(EngineView::Up { .. }));
    let stalled = match view {
        Some(EngineView::Up { status, .. }) => status.script.run.as_ref().and_then(stalled_for),
        _ => None,
    };
    let stall = stalled
        .map(|sec| format!(" {}", short_duration(sec)))
        .unwrap_or_default();
    let fixed = 3 + 2 + 2 + 13 + 1 + stall.chars().count();
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
        stall.red(),
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
        EngineView::Up { status, .. } => {
            state(status).1 == Color::Red || status.script.run.as_ref().and_then(stalled_for).is_some()
        }
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
                Tab::Chat => chat(frame, app, &row.name, content),
                Tab::Hooks => hooks(frame, app, &row.name, detail, content),
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
        ("E", "start a windowless Engine for it (skua-engine)"),
        ("L", "then log in, picking a server"),
        ("s", "then start a Script, with its options"),
        ("space", "mark several accounts to do any of this to all of them"),
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
                    GameState::LoginScreen => {
                        Line::from(vec!["L  ".blue().bold(), "log in, with a server picker".fg(DIM)])
                    }
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
        let block = panel(&format!("Question · {} · d to answer", question.caption))
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
            match run.quest_idle_sec {
                Some(sec) if sec >= STALL_SEC => Line::styled(
                    format!("quests stalled {}", short_duration(sec)),
                    Style::new().red().bold(),
                ),
                Some(sec) => Line::styled(
                    format!("quests: last progress {} ago", short_duration(sec)),
                    Style::new().fg(idle_color(sec, true)),
                ),
                None => Line::styled("quests: none in progress", Style::new().fg(DIM)),
            },
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
    let running = status.script.run.is_some();
    let width = area.width.saturating_sub(2) as usize;
    let lines = match detail.and_then(|d| d.quests.as_ref()) {
        _ if playing(status).is_none() => vec![not_playing()],
        None => vec![loading()],
        Some(Err(e)) => vec![read_error(e)],
        Some(Ok(q)) if q.quests.is_empty() => vec![Line::styled("no quests loaded", Style::new().fg(DIM))],
        Some(Ok(q)) => q
            .quests
            .iter()
            .flat_map(|quest| quest_lines(quest, running, width))
            .collect(),
    };
    frame.render_widget(Paragraph::new(lines).block(panel("Quests · loaded")), area);
}

/// A quest's name and status, then its requirements wrapped under it. An accepted quest's unmet requirements say how long they have gone
/// without a rise, and how fast they rise with the time left at that rate; the quest says how long since any of them rose.
fn quest_lines(quest: &Quest, running: bool, width: usize) -> Vec<Line<'static>> {
    const INDENT: usize = 7;
    let accepted = quest.status == "inProgress";
    let (status, color) = match quest.status.as_str() {
        "completable" => ("completable", Color::Green),
        "inProgress" => ("in progress", Color::Yellow),
        _ => ("not accepted", DIM),
    };
    let unmet = || quest.requirements.iter().filter(|r| accepted && r.have < r.qty);
    let mut head = vec![
        format!("{:<INDENT$}{}  ", quest.id, quest.name).bold(),
        Span::styled(status, Style::new().fg(color)),
    ];
    if let Some(idle) = unmet().filter_map(|r| r.idle_sec).reduce(f64::min) {
        head.push(Span::styled(
            format!(" · idle {}", short_duration(idle)),
            Style::new().fg(idle_color(idle, running)),
        ));
    }
    let mut lines = vec![Line::from(head)];
    let mut line: Vec<Span<'static>> = Vec::new();
    let mut used = INDENT;
    for r in &quest.requirements {
        let mut piece = vec![Span::styled(
            format!("{} {}/{}", r.name, r.have, r.qty),
            Style::new().fg(DIM),
        )];
        if accepted && r.have < r.qty {
            if let Some(idle) = r.idle_sec {
                piece.push(Span::styled(
                    format!(" {}", short_duration(idle)),
                    Style::new().fg(idle_color(idle, running)),
                ));
            }
            if let Some(rate) = r.gain_per_hour.filter(|rate| *rate > 0.0) {
                let left = (r.qty - r.have) as f64 / rate * 3600.0;
                piece.push(Span::styled(
                    format!(" +{}/h ~{}", rate_text(rate), short_duration(left)),
                    Style::new().fg(DIM),
                ));
            }
        }
        let piece_width: usize = piece.iter().map(|s| s.content.chars().count()).sum();
        if !line.is_empty() && used + 3 + piece_width > width {
            lines.push(Line::from(std::mem::take(&mut line)));
            used = INDENT;
        }
        if line.is_empty() {
            line.push(Span::raw(" ".repeat(INDENT)));
        } else {
            line.push(Span::styled(" · ", Style::new().fg(DIM)));
            used += 3;
        }
        used += piece_width;
        line.extend(piece);
    }
    if !line.is_empty() {
        lines.push(Line::from(line));
    }
    lines
}

/// How long `run`'s quests have gone without progress, once that is a stall.
fn stalled_for(run: &ScriptRun) -> Option<f64> {
    run.quest_idle_sec.filter(|sec| *sec >= STALL_SEC)
}

/// Dim while progress is recent, yellow past half the stall time and red once stalled; dim throughout when no Script runs.
fn idle_color(sec: f64, running: bool) -> Color {
    match sec {
        _ if !running => DIM,
        s if s >= STALL_SEC => Color::Red,
        s if s >= STALL_SEC / 2.0 => Color::Yellow,
        _ => DIM,
    }
}

fn rate_text(rate: f64) -> String {
    if rate >= 10.0 {
        format!("{rate:.0}")
    } else {
        format!("{rate:.1}")
    }
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

fn chat(frame: &mut Frame, app: &App, name: &str, area: Rect) {
    let following = app.chat.engine() == Some(name);
    let state = match &app.chat.ended {
        Some(why) if following => Line::styled(format!(" follow ended: {why} "), Style::new().red()).right_aligned(),
        _ => dim_right("game messages, following"),
    };
    let block = panel("Chat").title_top(state);
    let inner = block.inner(area);
    frame.render_widget(block, area);
    let bottom = 2.min(inner.height);
    let messages = Rect {
        height: inner.height - bottom,
        ..inner
    };
    let mut lines: Vec<Line> = Vec::new();
    if !following {
        lines.push(loading());
    } else if app.chat.entries.is_empty() {
        lines.push(Line::styled("no game messages yet", Style::new().fg(DIM)));
    } else {
        // The newest at the bottom: only as many as fit are laid out.
        for entry in app.chat.entries.iter().rev() {
            let mut entry_lines = chat_lines(entry, inner.width as usize);
            entry_lines.append(&mut lines);
            lines = entry_lines;
            if lines.len() >= messages.height as usize {
                break;
            }
        }
        lines.drain(..lines.len().saturating_sub(messages.height as usize));
    }
    frame.render_widget(Paragraph::new(lines), messages);

    let mut footer = Vec::new();
    footer.push(
        app.chat
            .note
            .as_ref()
            .map_or(Line::raw(""), |note| note_line(String::new(), note)),
    );
    footer.push(if app.chat.typing {
        Line::from(format!("> {}▏", app.chat.input).bold())
    } else {
        Line::styled(
            format!(
                "enter to chat · /w <name> <text> whispers{}",
                if app.chat.input.is_empty() {
                    ""
                } else {
                    " · a draft waits"
                }
            ),
            Style::new().fg(DIM),
        )
    });
    footer.drain(..footer.len() - bottom as usize);
    frame.render_widget(
        Paragraph::new(footer),
        Rect {
            y: messages.y + messages.height,
            height: bottom,
            ..inner
        },
    );
}

/// A game message as `time [channel] from → to: text`, wrapped under its text.
fn chat_lines(entry: &LogEntry, width: usize) -> Vec<Line<'static>> {
    if entry.kind == "gap" {
        return vec![Line::styled(
            "… messages missed here: evicted, or the Engine restarted",
            Style::new().yellow(),
        )];
    }
    let field = |key: &str| entry.data.as_ref().and_then(|d| d.get(key)).and_then(Value::as_str);
    let channel = field("channel").unwrap_or("?");
    let color = match channel {
        "whisper" => Color::Magenta,
        "party" => Color::Cyan,
        "guild" => Color::Green,
        "server" => Color::Yellow,
        "warning" => Color::Red,
        "zone" => Color::Reset,
        _ => Color::Blue,
    };
    let text = entry.text.clone().unwrap_or_default();
    let body = match (field("from"), field("to")) {
        (Some(from), Some(to)) => format!("{from} → {to}: {text}"),
        (Some(from), None) => format!("{from}: {text}"),
        (None, _) => text,
    };
    let tag = format!("{:<9} ", format!("[{channel}]"));
    let indent = 9 + tag.chars().count();
    let body_style = if channel == "zone" {
        Style::new()
    } else {
        Style::new().fg(color)
    };
    wrap(&body, width.saturating_sub(indent))
        .into_iter()
        .enumerate()
        .map(|(i, part)| {
            let lead = if i == 0 {
                vec![
                    format!("{} ", local_time(entry.ts)).fg(DIM),
                    Span::styled(tag.clone(), Style::new().fg(color).bold()),
                ]
            } else {
                vec![Span::raw(" ".repeat(indent))]
            };
            Line::from([lead, vec![Span::styled(part, body_style)]].concat())
        })
        .collect()
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

fn hook_runner(app: &App) -> bool {
    app.snapshot.as_ref().is_some_and(|s| s.hook_runner)
}

/// The Hook Runner's state, and the selected Engine's Hook runs, newest first, with the newest one's output below.
fn hooks(frame: &mut Frame, app: &App, name: &str, detail: Option<&Detail>, area: Rect) {
    let runner = if hook_runner(app) {
        Line::styled(" skua hooks: running ", Style::new().green()).right_aligned()
    } else {
        Line::styled(" skua hooks: not running · H starts it ", Style::new().yellow().bold()).right_aligned()
    };
    let runs: &[LogEntry] = detail.map_or(&[], |d| &d.hook_runs);
    let output_height = if runs.is_empty() {
        0
    } else {
        (area.height / 3).max(4).min(area.height)
    };
    let list_area = Rect {
        height: area.height - output_height,
        ..area
    };
    let mut lines = Vec::new();
    match detail {
        None => lines.push(loading()),
        Some(_) if runs.is_empty() => {
            lines.push(Line::from(format!("No hook has run for {name} yet.")));
            lines.push(Line::raw(""));
            lines.push(Line::styled(
                format!(
                    "A hook is an executable in {} named after an event type, such as inventory.full. The Hook Runner runs it on \
                     each such event, with the event's JSON on stdin.",
                    crate::discovery::hooks_dir(&app.skua_dir).display()
                ),
                Style::new().fg(DIM),
            ));
        }
        Some(_) => {
            lines.push(Line::styled(
                format!(
                    "{:<8} {:<18} {:<5} {:>7}  {}",
                    "STARTED", "HOOK", "EXIT", "TOOK", "OUTPUT"
                ),
                Style::new().fg(DIM).bold(),
            ));
            lines.extend(runs.iter().rev().map(|entry| hook_run_line(&HookRun::of(entry))));
        }
    }
    let mut list = Paragraph::new(lines).block(panel("Hooks").title_top(runner));
    if runs.is_empty() {
        list = list.wrap(Wrap { trim: true });
    }
    frame.render_widget(list, list_area);

    if let Some(newest) = runs.last().map(HookRun::of) {
        let output_area = Rect {
            y: area.y + list_area.height,
            height: output_height,
            ..area
        };
        let block = panel(&format!("Output · {}", newest.hook));
        let lines: Vec<&str> = newest.output.lines().collect();
        let skip = lines.len().saturating_sub(block.inner(output_area).height as usize);
        let text: Vec<Line> = lines[skip..].iter().map(|l| Line::raw(l.to_string())).collect();
        frame.render_widget(Paragraph::new(text).block(block), output_area);
    }
}

/// One `hook.ran`: when it started, the hook, its exit code (none when it couldn't start), how long it took and its last line of output.
fn hook_run_line(run: &HookRun) -> Line<'static> {
    let (exit, color) = match run.exit_code {
        Some(0) => ("0".to_owned(), Color::Green),
        Some(code) => (code.to_string(), Color::Red),
        None => ("—".to_owned(), Color::Red),
    };
    let took = run.duration_ms as f64 / 1000.0;
    let last = run.output.lines().rev().find(|l| !l.trim().is_empty()).unwrap_or("");
    Line::from(vec![
        format!("{} ", local_time(run.started_at)).fg(DIM),
        format!("{:<18} ", run.hook).bold(),
        Span::styled(format!("{exit:<5} "), Style::new().fg(color).bold()),
        format!(
            "{:>7}  ",
            if took < 60.0 {
                format!("{took:.1}s")
            } else {
                duration(took)
            }
        )
        .fg(DIM),
        Span::raw(last.to_owned()),
    ])
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
    let selected = app
        .selected_row()
        .and_then(|row| Some((app.activity.get(&row.name)?, row.name)));
    let line = match (&app.toast, selected) {
        (Some(toast), _) => note_line(String::new(), toast),
        (None, Some((note, name))) => note_line(format!("{name}: "), note),
        (None, None) => Line::styled(
            format!(
                " acting on: {}   ·   space mark  a mark group  : commands  / filter  ? keys",
                app.targets_label()
            ),
            Style::new().fg(DIM),
        ),
    };
    frame.render_widget(Paragraph::new(line), area);
}

fn note_line(prefix: String, note: &Note) -> Line<'static> {
    let style = match note.tone {
        Tone::Info => Style::new(),
        Tone::Pending => Style::new().blue(),
        Tone::Done => Style::new().green(),
        Tone::Failed => Style::new().red().bold(),
    };
    Line::styled(format!(" {prefix}{}", first_line(&note.text)), style)
}

/// The keys for the selected account's state, as layout E shows them.
fn footer(frame: &mut Frame, app: &App, area: Rect) {
    let view = app.selected_row().and_then(|r| app.engine(&r.name));
    let keys: &[(&str, &str)] = match view {
        Some(EngineView::Up { .. }) if app.tab == Tab::Chat && app.chat.typing => {
            &[("enter", "send"), ("/w name text", "whisper"), ("esc", "stop typing")]
        }
        Some(EngineView::Up { .. }) if app.tab == Tab::Chat => &[
            ("enter", "chat"),
            ("tab", "tab"),
            (":", "commands"),
            ("?", "keys"),
            ("q", "quit"),
        ],
        None | Some(EngineView::Offline) => &[
            ("E", "start Engine"),
            ("space", "mark"),
            (":", "commands"),
            ("tab", "tab"),
            ("?", "keys"),
            ("q", "quit"),
        ],
        Some(EngineView::Up { status, .. }) if playing(status).is_some() => &[
            ("s", "Script"),
            ("x", "stop"),
            ("J", "join"),
            ("d", "answer"),
            ("O", "log out"),
            ("X", "stop Engine"),
            (":", "commands"),
            ("?", "keys"),
        ],
        Some(EngineView::Up { .. }) => &[
            ("L", "log in"),
            ("X", "stop Engine"),
            ("U", "update Scripts"),
            (":", "commands"),
            ("?", "keys"),
            ("q", "quit"),
        ],
        Some(EngineView::Failed(_)) => &[
            ("j/k", "move"),
            ("space", "mark"),
            (":", "commands"),
            ("?", "keys"),
            ("q", "quit"),
        ],
    };
    let mut spans = vec![Span::raw(" ")];
    for &(key, what) in keys {
        spans.push(key.blue().bold());
        spans.push(format!(" {what}  ").fg(DIM));
    }
    frame.render_widget(Paragraph::new(Line::from(spans)), area);
}

fn draw_modal(frame: &mut Frame, app: &App, modal: &Modal) {
    let screen = frame.area();
    let width = 72.min(screen.width.saturating_sub(6));
    let inner = width.saturating_sub(2) as usize;
    let on = format!("on {}", app.targets_label());
    let (title, right, lines, hint): (String, String, Vec<Line>, &str) = match modal {
        Modal::Palette { query, selected } => {
            let mut lines = vec![Line::from(format!("> {query}▏").bold()), Line::raw("")];
            let matches = App::palette_matches(query);
            if matches.is_empty() {
                lines.push(Line::styled("no command matches", Style::new().fg(DIM)));
            }
            for (i, command) in matches.iter().enumerate() {
                let line = Line::raw(format!("{:<40}{}", command.title, command.key));
                lines.push(highlight(line, i == *selected));
            }
            (
                format!("Commands · {on}"),
                String::new(),
                lines,
                "type to search · ↑↓ · enter run · esc",
            )
        }
        Modal::Filter { query } => (
            "Filter accounts".into(),
            String::new(),
            vec![
                Line::from(format!("> {query}▏").bold()),
                Line::styled("part of a name; empty clears", Style::new().fg(DIM)),
            ],
            "enter ok · esc cancel",
        ),
        Modal::Help => (
            "Keys".into(),
            String::new(),
            KEYS.iter()
                .map(|(key, what)| Line::from(vec![format!("{key:<10}").blue().bold(), Span::raw(*what)]))
                .collect(),
            "any key closes",
        ),
        Modal::Confirm { text, .. } => (
            "Confirm".into(),
            String::new(),
            wrap(text, inner).into_iter().map(Line::raw).collect(),
            "y yes · n no",
        ),
        Modal::Servers {
            engine,
            servers,
            selected,
        } => {
            let lines = match servers {
                None => vec![reading_from(engine)],
                Some(Err(e)) => error_lines(e, inner),
                Some(Ok(list)) if list.is_empty() => vec![Line::styled("no servers", Style::new().fg(DIM))],
                Some(Ok(list)) => window(list.len(), *selected, 12)
                    .map(|i| {
                        let server = &list[i];
                        let fill = (ratio(server.player_count, server.max_players) * 12.0).round() as usize;
                        let line = Line::raw(format!(
                            "{:<14}{:>11}  {:·<12}{}{}",
                            server.name,
                            format!("{}/{}", server.player_count, server.max_players),
                            "█".repeat(fill.min(12)),
                            if server.member_only { "  member" } else { "" },
                            if server.online { "" } else { "  offline" },
                        ));
                        highlight(if server.online { line } else { line.fg(DIM) }, i == *selected)
                    })
                    .collect(),
            };
            ("Log in to".into(), on, lines, "↑↓ · enter log in · esc")
        }
        Modal::Scripts {
            engine,
            query,
            found,
            selected,
        } => {
            let mut lines = vec![Line::from(format!("> {query}▏").bold()), Line::raw("")];
            match found {
                None => lines.push(reading_from(engine)),
                Some(Err(e)) => lines.extend(error_lines(e, inner)),
                Some(Ok(found)) if found.scripts.is_empty() => {
                    lines.push(Line::styled("no Script matches", Style::new().fg(DIM)))
                }
                Some(Ok(found)) => {
                    for i in window(found.scripts.len(), *selected, 10) {
                        let script = &found.scripts[i];
                        let mut spans = vec![Span::raw(script.path.clone())];
                        if !script.downloaded {
                            spans.push("  not downloaded: U".fg(DIM));
                        } else if script.outdated {
                            spans.push("  outdated: U".fg(DIM));
                        }
                        lines.push(highlight(Line::from(spans), i == *selected));
                    }
                    if found.matched as usize > found.scripts.len() {
                        lines.push(Line::styled(
                            format!("… {} matched; type to narrow", found.matched),
                            Style::new().fg(DIM),
                        ));
                    }
                }
            }
            (
                "Start a Script".into(),
                on,
                lines,
                "type to search · ↑↓ · enter options · esc",
            )
        }
        Modal::Options {
            engine,
            script,
            fields,
            selected,
        } => {
            let lines = match fields {
                None => vec![Line::styled(
                    format!("compiling {script} in {engine} to read its options…"),
                    Style::new().fg(DIM),
                )],
                Some(Err(e)) => error_lines(e, inner),
                Some(Ok(fields)) => {
                    let mut lines: Vec<Line> = if fields.is_empty() {
                        vec![Line::styled("This Script has no options.", Style::new().fg(DIM))]
                    } else {
                        window(fields.len(), *selected, 12)
                            .map(|i| option_line(&fields[i], i == *selected))
                            .collect()
                    };
                    if let Some(description) = fields.get(*selected).and_then(|f| f.option.description.as_deref()) {
                        lines.push(Line::styled(truncate(description, inner), Style::new().fg(DIM)));
                    }
                    lines.push(Line::raw(""));
                    lines.push(Line::styled(
                        format!("starts {on}; changed values are stored as its options"),
                        Style::new().fg(DIM),
                    ));
                    lines
                }
            };
            (
                format!("Options · {}", base_name(script)),
                on,
                lines,
                "↑↓ field · ←→ space value · type to edit · enter start · esc",
            )
        }
        Modal::Join { query } => (
            "Join a map".into(),
            on,
            vec![
                Line::from(format!("> {query}▏").bold()),
                Line::styled(
                    "map[-room] [cell] [pad], e.g. battleon-9999 Enter Spawn",
                    Style::new().fg(DIM),
                ),
            ],
            "enter join · esc cancel",
        ),
        Modal::Question {
            engine,
            questions,
            selected,
        } => {
            let (title, lines) = match questions {
                None => (format!("Question · {engine}"), vec![reading_from(engine)]),
                Some(Err(e)) => (format!("Question · {engine}"), error_lines(e, inner)),
                Some(Ok(list)) => {
                    let question = &list[0];
                    let mut lines: Vec<Line> = wrap(&question.text, inner)
                        .into_iter()
                        .map(|l| Line::styled(l, Style::new().yellow().bold()))
                        .collect();
                    lines.push(Line::raw(""));
                    for (i, choice) in question.choices.iter().enumerate() {
                        lines.push(highlight(Line::raw(format!("{}  {choice}", i + 1)), i == *selected));
                    }
                    if let Some(script) = &question.script {
                        lines.push(Line::raw(""));
                        lines.push(Line::styled(format!("from {script}"), Style::new().fg(DIM)));
                    }
                    let more = if list.len() > 1 {
                        format!(" · 1 of {}", list.len())
                    } else {
                        String::new()
                    };
                    (format!("Question · {engine} · {}{more}", question.caption), lines)
                }
            };
            (title, String::new(), lines, "1–9 or ↑↓ enter · esc later")
        }
    };
    let height = (lines.len() as u16 + 4).min(screen.height.saturating_sub(2));
    let area = Rect {
        x: (screen.width - width) / 2,
        y: ((screen.height - height) / 3).max(1),
        width,
        height,
    };
    let mut block = panel(&title)
        .border_style(Style::new().blue())
        .title_bottom(Line::styled(format!(" {hint} "), Style::new().fg(DIM)));
    if !right.is_empty() {
        block = block.title_top(Line::styled(format!(" {right} "), Style::new().blue()).right_aligned());
    }
    frame.render_widget(Clear, area);
    frame.render_widget(Paragraph::new(lines).block(block), area);
}

fn option_line(field: &Field, selected: bool) -> Line<'static> {
    let name = format!("{:<28}", truncate(&field.option.display_name, 27));
    let value = match field.option.kind.as_str() {
        "bool" | "enum" => format!("‹ {} ›", field.value),
        _ if selected => format!("{}▏", field.value),
        _ => field.value.clone(),
    };
    let changed = if field.value != field.option.value { " *" } else { "" };
    let line = if field.editable() {
        Line::raw(format!("{name}{value}{changed}"))
    } else {
        Line::styled(format!("{name}{value}  (resets each start)"), Style::new().fg(DIM))
    };
    highlight(line, selected)
}

fn highlight(line: Line<'static>, selected: bool) -> Line<'static> {
    if selected { line.black().on_blue().bold() } else { line }
}

/// The indices of a list of `len` to show in `rows`, keeping `selected` in view.
fn window(len: usize, selected: usize, rows: usize) -> std::ops::Range<usize> {
    let start = (selected + 1).saturating_sub(rows);
    start..len.min(start + rows)
}

fn reading_from(engine: &str) -> Line<'static> {
    Line::styled(format!("reading from {engine}…"), Style::new().fg(DIM))
}

fn error_lines(error: &Error, width: usize) -> Vec<Line<'static>> {
    error
        .to_string()
        .lines()
        .flat_map(|l| wrap(l, width))
        .map(|l| Line::styled(l, Style::new().red().bold()))
        .collect()
}

/// `text` in lines of at most `width` characters, broken at spaces where it can be.
fn wrap(text: &str, width: usize) -> Vec<String> {
    let width = width.max(1);
    let mut lines = Vec::new();
    for paragraph in text.lines() {
        let mut line = String::new();
        for word in paragraph.split(' ') {
            if !line.is_empty() && line.chars().count() + 1 + word.chars().count() > width {
                lines.push(std::mem::take(&mut line));
            }
            if !line.is_empty() {
                line.push(' ');
            }
            line.push_str(word);
            while line.chars().count() > width {
                let rest: String = line.chars().skip(width).collect();
                lines.push(line.chars().take(width).collect());
                line = rest;
            }
        }
        lines.push(line);
    }
    lines
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

/// A duration in its largest unit or two: `45s`, `14m`, `2h13m`, `3d4h`.
fn short_duration(sec: f64) -> String {
    let sec = sec.max(0.0) as u64;
    match (sec / 86400, sec / 3600 % 24, sec / 60 % 60) {
        (0, 0, 0) => format!("{sec}s"),
        (0, 0, m) => format!("{m}m"),
        (0, h, m) => format!("{h}h{m:02}m"),
        (d, h, _) => format!("{d}d{h}h"),
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
