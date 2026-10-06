//! Accounts by group on the left; the selected account's Engine in tabs on the right; a status line and the keys at the bottom.

use ratatui::Frame;
use ratatui::layout::{Alignment, Constraint, Layout, Rect};
use ratatui::style::{Color, Modifier, Style, Stylize};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, BorderType, Borders, Clear, Paragraph, Wrap};
use serde_json::Value;

use crate::app::{App, Field, KEYS, Modal, Note, Row, Tab, Tone, first_line};
use crate::bags::{Bags, Change, Kind, now_ms};
use crate::discovery::MANAGER_FILE;
use crate::dto::{GameState, Hello, HookRun, LogEntry, Map, Player, Quest, ScriptGoal, ScriptRun, Status};
use crate::engine::{Error, NOT_LOGGED_IN, PROTOCOL};
use crate::inventory::Shelf;
use crate::poller::{Detail, EngineView, LOG_TAIL};

const DIM: Color = Color::DarkGray;
const SELECTED: Color = Color::Indexed(237);
/// How long a run's quests go without progress before they are stalled, as the Engine's `quest.stalled` says by default.
const STALL_SEC: f64 = 600.0;
/// How long the Overview shows the newest quest progress, as the game's line over the fight.
const PROGRESS_SHOWN: std::time::Duration = std::time::Duration::from_secs(5);
/// The player's frame: four lines and its border; and how wide it is.
const PLAYER_H: u16 = 6;
const PLAYER_W: u16 = 36;
/// How wide the run's panel must be for its goal tree's lines, rates and times left included.
const RUN_W: u16 = 84;
/// How wide Current Quests is, beside the player or as the right column down to the chat.
const QUESTS_W: u16 = 46;
/// How tall the chat strip is at most, its border included.
const CHAT_H: u16 = 10;
/// How wide the accounts are, and an account's name in them at most.
const ACCOUNTS_W: u16 = 24;
const NAME_W: usize = 11;
/// How many players the Overview lists left of the monsters; the rest are counted.
const MAX_MEMBERS: usize = 4;
/// How many players outside the player's cell the Room names.
const MAX_ROOM: usize = 4;

pub fn draw(frame: &mut Frame, app: &App) {
    // The Game tab sets it again while it shows the picture.
    app.hits.borrow_mut().picture = None;
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
    let left = ACCOUNTS_W.min(area.width / 2);
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
    // The names take as much as the longest needs, up to NAME_W; the state gets the rest.
    let name_w = rows
        .iter()
        .map(|r| r.name.chars().count())
        .max()
        .unwrap_or(0)
        .clamp(4, NAME_W);
    let mut selected_line = 0;
    let mut row_lines = Vec::new();
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
        row_lines.push((lines.len(), i));
        lines.push(account_line(app, row, i == app.selected, name_w, inner.width));
    }
    let scroll = (selected_line + 1).saturating_sub(inner.height as usize) as u16;
    {
        let mut hits = app.hits.borrow_mut();
        hits.accounts = area;
        hits.rows = row_lines
            .into_iter()
            .filter_map(|(line, i)| (line as u16).checked_sub(scroll).map(|y| (inner.y + y, i)))
            .filter(|(y, _)| *y < inner.bottom())
            .collect();
    }
    frame.render_widget(Paragraph::new(lines).scroll((scroll, 0)), inner);
}

fn account_line(app: &App, row: &Row, selected: bool, name_w: usize, width: u16) -> Line<'static> {
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
    let stall = match view {
        Some(EngineView::Up { status, .. }) => status.script.run.as_ref().and_then(stall),
        _ => None,
    };
    let stall = match stall {
        Some(Stall::Stuck(sec)) => format!(" {}", short_duration(sec)).red(),
        Some(Stall::Grinding(sec)) => format!(" {}", short_duration(sec)).yellow(),
        None => Span::raw(""),
    };
    // The dot, the name, a space, the state, then the stall and the flag.
    let fixed = 2 + name_w + 1 + stall.content.chars().count() + 1;
    let state_width = (width as usize).saturating_sub(fixed);
    // A marked account's name is blue, as the marks' count in the title.
    let name_style = if marked {
        Style::new().blue().bold()
    } else if up {
        Style::new().bold()
    } else {
        Style::new().fg(DIM)
    };
    let mut line = Line::from(vec![
        Span::styled("●", Style::new().fg(color)),
        " ".into(),
        Span::styled(format!("{:<name_w$}", truncate(&row.name, name_w)), name_style),
        " ".into(),
        Span::styled(
            format!("{:<state_width$}", truncate(&state, state_width)),
            Style::new().fg(color),
        ),
        stall,
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
            state(status).1 == Color::Red || matches!(status.script.run.as_ref().and_then(stall), Some(Stall::Stuck(_)))
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
    let mut tab_hits = Vec::new();
    let mut x = area.x + 1;
    for tab in Tab::ALL {
        let title = format!(" {} ", tab.title());
        let width = title.chars().count() as u16;
        tab_hits.push((Rect::new(x, area.y, width, 1), tab));
        x += width + 1;
        tabs.push(if tab == app.tab {
            title.black().on_blue().bold()
        } else {
            title.fg(DIM)
        });
        tabs.push("│".fg(DIM));
    }
    app.hits.borrow_mut().tabs = tab_hits;
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
                Tab::Overview => overview(frame, app, hello, status, detail, content),
                Tab::Inventory => inventory(frame, app, status, detail, content),
                Tab::Quests => quests(frame, status, detail, content),
                Tab::Logs => {
                    let block =
                        panel("Logs + events").title_top(dim_right(&format!("logs --tail {LOG_TAIL}, following")));
                    logs(frame, block, detail, content)
                }
                Tab::Game => game(frame, app, &row.name, status, detail, content),
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

/// The selected account as the game's screen shows it, in text: the player and its Script's run across the top; any Question; the newest
/// quest progress; the accounts on its map and the others in its cell on the left, the cell's monsters on the right; then what the run
/// gained. Current Quests runs down the right, and the game's chat across the bottom, with who else is on the map in its title.
fn overview(frame: &mut Frame, app: &App, hello: &Hello, status: &Status, detail: Option<&Detail>, area: Rect) {
    let Some(me) = playing(status) else {
        return frame.render_widget(
            Paragraph::new(not_playing_lines(hello, status)).block(panel("Overview")),
            area,
        );
    };
    let map = answered(detail.and_then(|d| d.map.as_ref()));
    let place = match &map {
        Ok(map) => format!("{} · {}", room_name(map), me.cell),
        Err(_) => format!("{} · {}", me.map, me.cell),
    };
    let block = panel(&format!("Overview · {place}"));
    let inner = block.inner(area);
    frame.render_widget(block, area);

    let run = run_lines(status, detail);
    let accounts = on_map(app, me, status);
    let members = members(me, &accounts, map.as_ref().ok().copied());
    let shown = members.len().min(MAX_MEMBERS);
    let hidden = members.len() - shown;
    // A member takes two rows and a blank one; the count of those that don't fit goes under the last.
    let members_h = if hidden > 0 {
        shown as u16 * 3
    } else {
        (shown as u16 * 3).saturating_sub(1)
    };
    let question_h = status.pending_dialogs.first().map_or(0, |q| q.choices.len() as u16 + 3);
    let run_h = run.len() as u16 + 2;

    // Where the run's goal tree and the quests fit side by side, Current Quests runs down the right, and the run goes beside the player
    // where it fits there too, else under it. On a narrower terminal the quests sit beside the player and the run goes under them.
    let tall_quests = inner.width >= RUN_W + QUESTS_W;
    let run_beside = tall_quests && inner.width >= PLAYER_W + RUN_W + QUESTS_W;
    let top_h = if run_beside { run_h.max(PLAYER_H) } else { PLAYER_H };
    let run_row_h = if run_beside { 0 } else { run_h };
    let cell_h = members_h.max(4);
    // The chat strip at the bottom shares what the rest leaves with Bags, and gives way first on a short terminal.
    let left_over = inner.height.saturating_sub(top_h + run_row_h + question_h + 1 + cell_h);
    let chat_h = (left_over / 2).clamp(3, CHAT_H);
    let [upper, chat_area] = Layout::vertical([Constraint::Fill(1), Constraint::Length(chat_h)]).areas(inner);
    let [left, side] = Layout::horizontal([
        Constraint::Fill(1),
        Constraint::Length(if tall_quests { QUESTS_W } else { 0 }),
    ])
    .areas(upper);
    let [top, run_row, question_area, progress_area, cell, bags_area] = Layout::vertical([
        Constraint::Length(top_h),
        Constraint::Length(run_row_h),
        Constraint::Length(question_h),
        Constraint::Length(1),
        Constraint::Length(cell_h),
        Constraint::Fill(1),
    ])
    .areas(left);
    let [player_area, beside] = Layout::horizontal([Constraint::Length(PLAYER_W), Constraint::Fill(1)]).areas(top);
    let (run_area, quests_area) = match (run_beside, tall_quests) {
        (true, _) => (beside, side),
        (false, true) => (run_row, side),
        (false, false) => (run_row, beside),
    };
    player_frame(frame, me, status, player_area);
    frame.render_widget(Paragraph::new(run).block(panel("Script")), run_area);
    let tracker_rows = quests_area.height.saturating_sub(2) as usize;
    frame.render_widget(
        Paragraph::new(tracker_lines(detail, tracker_rows)).block(panel("Current Quests")),
        quests_area,
    );

    if let Some(question) = status.pending_dialogs.first() {
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
        frame.render_widget(Paragraph::new(lines).block(block), question_area);
    }
    if let Some((text, _)) = detail
        .and_then(|d| d.progress.as_ref())
        .filter(|(_, at)| at.elapsed() < PROGRESS_SHOWN)
    {
        frame.render_widget(
            Paragraph::new(Line::styled(text.clone(), Style::new().yellow().bold()).centered()),
            progress_area,
        );
    }

    let members_width = (cell.width * 11 / 20).clamp(30, 48).min(cell.width.saturating_sub(1));
    let members_area = Rect {
        x: cell.x + 1,
        width: members_width,
        ..cell
    };
    let mut lines = Vec::new();
    for member in &members[..shown] {
        lines.extend(member_lines(member));
        lines.push(Line::raw(""));
    }
    lines.pop();
    if hidden > 0 {
        lines.push(Line::styled(format!("+{hidden} more here"), Style::new().fg(DIM)));
    }
    frame.render_widget(Paragraph::new(lines), members_area);
    let monsters_area = Rect {
        x: members_area.right() + 2,
        width: cell.right().saturating_sub(members_area.right() + 2),
        ..cell
    };
    match &map {
        Ok(map) => monster_plates(frame, me, &accounts, map, monsters_area),
        Err(line) => frame.render_widget(Paragraph::new(line.clone()), monsters_area),
    }

    if let Some(detail) = detail {
        bags(frame, &detail.bags, bags_area);
    }

    let chat_width = chat_area.width.saturating_sub(2) as usize;
    let mut chat: Vec<Line> = detail
        .map(|d| d.chat.iter().flat_map(|e| chat_lines(e, chat_width)).collect())
        .unwrap_or_default();
    if chat.is_empty() {
        chat.push(Line::styled("no chat yet", Style::new().fg(DIM)));
    }
    let skip = chat.len().saturating_sub(chat_area.height.saturating_sub(2) as usize);
    let block = match &map {
        Ok(map) => panel("Chat").title_top(room_title(me, map).right_aligned()),
        Err(_) => panel("Chat"),
    };
    frame.render_widget(Paragraph::new(chat.split_off(skip)).block(block), chat_area);
}

fn not_playing_lines(hello: &Hello, status: &Status) -> Vec<Line<'static>> {
    let mut lines = vec![
        Line::styled(
            format!("{} · {}", status.engine.name, state(status).0),
            Style::new().fg(state(status).1).bold(),
        ),
        Line::styled(engine_summary(hello, status), Style::new().fg(DIM)),
        Line::raw(""),
        match status.game.state {
            GameState::LoginScreen => Line::from(vec!["L  ".blue().bold(), "log in, with a server picker".fg(DIM)]),
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
        lines.push(Line::styled(
            format!("last Script: {} {}", base_name(&last.script), last.outcome),
            Style::new().yellow(),
        ));
    }
    lines
}

/// The player's frame, as the game's top left: name, level, class, where, HP and MP, and gold.
fn player_frame(frame: &mut Frame, me: &Player, status: &Status, area: Rect) {
    let block = panel(&me.name)
        .title_style(Style::new().yellow().bold())
        .title(Line::from(format!(" Lv {} ", me.level).bold()).right_aligned())
        .title_bottom(
            Line::styled(format!(" {} gold ", thousands(me.gold)), Style::new().yellow().bold()).right_aligned(),
        );
    let width = block.inner(area).width.saturating_sub(3);
    let hp = ratio(me.hp, me.max_hp);
    let lines = vec![
        Line::styled(me.class.clone().unwrap_or_else(|| "—".into()), Style::new().fg(DIM)),
        Line::styled(
            format!(
                "{} {} · {}",
                me.map,
                me.cell,
                status.game.server.as_deref().unwrap_or("?")
            ),
            Style::new().fg(DIM),
        ),
        gauge(
            "HP",
            width,
            hp,
            hp_color(hp, me.alive),
            hp_text(me.hp, me.max_hp, me.alive),
        ),
        gauge(
            "MP",
            width,
            ratio(me.mp, me.max_mp),
            Color::Blue,
            format!("{}/{}", me.mp, me.max_mp),
        ),
    ];
    frame.render_widget(Paragraph::new(lines).block(block), area);
}

/// The run in a few plain lines: the Script with its time, kills and deaths; whether it progresses, grinds or is stuck; then its goal as
/// a tree, or its newest log line.
fn run_lines(status: &Status, detail: Option<&Detail>) -> Vec<Line<'static>> {
    let mut lines = Vec::new();
    match (&status.script.run, &status.script.last_run) {
        (Some(run), _) => {
            lines.push(Line::from(vec![
                Span::styled(
                    base_name(&run.script).trim_end_matches(".cs").to_owned(),
                    Style::new().magenta().bold(),
                ),
                Span::raw(format!(
                    "  running {} · {} kills · {}/min · {} deaths{}",
                    duration(run.elapsed_sec),
                    thousands(run.kills),
                    run.kills_per_min.map(rate_text).unwrap_or_else(|| "—".into()),
                    run.deaths,
                    if run.relogging_in { " · relogging in" } else { "" }
                )),
            ]));
            lines.push(match (stall(run), run.quest_idle_sec) {
                (Some(Stall::Stuck(sec)), _) => Line::styled(
                    format!("stuck · no quest progress or kills for {}", short_duration(sec)),
                    Style::new().red().bold(),
                ),
                (Some(Stall::Grinding(sec)), _) => Line::styled(
                    format!(
                        "grinding · no quest progress for {}, still killing",
                        short_duration(sec)
                    ),
                    Style::new().yellow(),
                ),
                (None, Some(sec)) => Line::styled(
                    format!("progressing · last quest progress {} ago", short_duration(sec)),
                    Style::new().fg(idle_color(sec, true)),
                ),
                (None, None) => Line::styled("no quest in progress", Style::new().fg(DIM)),
            });
            match &run.goal {
                Some(goal) => lines.extend(goal_tree(goal)),
                None => lines.push(doing(run, detail)),
            }
        }
        (None, Some(last)) => lines.push(Line::styled(
            format!("no Script running · last: {} {}", base_name(&last.script), last.outcome),
            Style::new().fg(DIM),
        )),
        (None, None) => lines.push(Line::styled("no Script running", Style::new().fg(DIM))),
    }
    lines
}

/// The quests in progress or ready to turn in, as the game's Current Quests, in `rows`: each with its requirements and their counts, the
/// bank's copies included, and how many lines didn't fit.
fn tracker_lines(detail: Option<&Detail>, rows: usize) -> Vec<Line<'static>> {
    let quests = match answered(detail.and_then(|d| d.quests.as_ref())) {
        Ok(quests) => quests,
        Err(line) => return vec![line],
    };
    let mut lines = Vec::new();
    for quest in quests.quests.iter().filter(|q| q.status != "notAccepted") {
        let ready = if quest.status == "completable" {
            "  ✓ ready"
        } else {
            ""
        };
        lines.push(Line::styled(
            format!("{}{ready}", quest.name),
            Style::new().green().bold(),
        ));
        for r in &quest.requirements {
            let done = r.owned() >= r.qty;
            lines.push(Line::styled(
                format!("  {} {}/{}", r.name, r.owned(), r.qty),
                if done { Style::new().green() } else { Style::new() },
            ));
        }
    }
    if lines.is_empty() {
        lines.push(Line::styled("no quest in progress", Style::new().fg(DIM)));
    }
    if lines.len() > rows && rows > 0 {
        let hidden = lines.len() - (rows - 1);
        lines.truncate(rows - 1);
        lines.push(Line::styled(format!("… {hidden} more lines"), Style::new().fg(DIM)));
    }
    lines
}

/// Every account playing on the player's map with an Engine here, by Engine name, the selected account first.
fn on_map<'a>(app: &'a App, me: &Player, status: &'a Status) -> Vec<(String, &'a Status)> {
    std::iter::once((status.engine.name.clone(), status))
        .chain(
            app.snapshot
                .iter()
                .flat_map(|s| &s.engines)
                .filter_map(|(name, view)| match view {
                    EngineView::Up { status: other, .. }
                        if *name != status.engine.name
                            && playing(other).is_some_and(|p| p.map.eq_ignore_ascii_case(&me.map)) =>
                    {
                        Some((name.clone(), other.as_ref()))
                    }
                    _ => None,
                }),
        )
        .collect()
}

/// A player the Overview lists on the left: an account on the map, with its run, or someone else in the player's cell.
struct Member<'a> {
    name: String,
    /// An account's class, and its cell when it isn't the player's; someone else's level.
    about: String,
    hp: i64,
    max_hp: i64,
    alive: bool,
    selected: bool,
    afk: bool,
    run: Option<&'a ScriptRun>,
}

/// The accounts on the player's map, the selected one first, then the others the map lists in the player's cell.
fn members<'a>(me: &Player, accounts: &[(String, &'a Status)], map: Option<&Map>) -> Vec<Member<'a>> {
    let mut members: Vec<Member> = accounts
        .iter()
        .filter_map(|(name, status)| Some((name, status, playing(status)?)))
        .enumerate()
        .map(|(i, (name, status, p))| {
            let class = p.class.clone().unwrap_or_else(|| "—".into());
            Member {
                name: name.clone(),
                about: if p.cell == me.cell {
                    class
                } else {
                    format!("{class} · in {}", p.cell)
                },
                hp: p.hp,
                max_hp: p.max_hp,
                alive: p.alive,
                selected: i == 0,
                afk: false,
                run: status.script.run.as_ref(),
            }
        })
        .collect();
    let players: Vec<&str> = accounts
        .iter()
        .filter_map(|(_, s)| playing(s))
        .map(|p| p.name.as_str())
        .collect();
    members.extend(
        map.iter()
            .flat_map(|map| &map.players)
            .filter(|p| p.cell == me.cell && !players.iter().any(|n| n.eq_ignore_ascii_case(&p.name)))
            .map(|p| Member {
                name: p.name.clone(),
                about: format!("Lv {}", p.level),
                hp: p.hp,
                max_hp: p.max_hp,
                alive: p.hp > 0,
                selected: false,
                afk: p.afk,
                run: None,
            }),
    );
    members
}

/// A member's two lines: its name and what else is known of it, then its HP and, for an account running a Script, its kills and deaths.
fn member_lines(member: &Member) -> [Line<'static>; 2] {
    let name_style = if member.selected {
        Style::new().yellow().bold()
    } else {
        Style::new().bold()
    };
    let mut first = vec![
        Span::styled(truncate(&member.name, 16), name_style),
        Span::styled(format!("  {}", truncate(&member.about, 24)), Style::new().fg(DIM)),
    ];
    if member.afk {
        first.push(Span::styled("  afk", Style::new().yellow()));
    }
    let hp = ratio(member.hp, member.max_hp);
    let mut second = bar(
        12,
        hp,
        hp_color(hp, member.alive),
        hp_text(member.hp, member.max_hp, member.alive),
    );
    if let Some(run) = member.run {
        second.push(Span::styled(
            format!(
                "  {} kills · {}/min · ",
                thousands(run.kills),
                run.kills_per_min.map(rate_text).unwrap_or_else(|| "—".into())
            ),
            Style::new().fg(DIM),
        ));
        second.push(Span::styled(
            format!("{} deaths", run.deaths),
            if run.deaths > 0 {
                Style::new().red()
            } else {
                Style::new().fg(DIM)
            },
        ));
    }
    [Line::from(first), Line::from(second)]
}

/// The monsters in the player's cell, a box each across `area`: its HP, and which accounts there target it; the player's target outlined.
/// Those that don't fit are counted.
fn monster_plates(frame: &mut Frame, me: &Player, accounts: &[(String, &Status)], map: &Map, area: Rect) {
    const PLATE_MIN: u16 = 20;
    const COUNT_W: u16 = 9;
    let here: Vec<_> = map.monsters.iter().filter(|m| m.cell == me.cell).collect();
    if here.is_empty() {
        return frame.render_widget(
            Paragraph::new(Line::styled(
                format!("no monsters in {}", me.cell),
                Style::new().fg(DIM),
            )),
            area,
        );
    }
    let fitting = |width: u16| ((width + 1) / (PLATE_MIN + 1)).max(1) as usize;
    let shown = if here.len() <= fitting(area.width) {
        here.len()
    } else {
        fitting(area.width.saturating_sub(COUNT_W))
    };
    let plates_width = if shown < here.len() {
        area.width.saturating_sub(COUNT_W)
    } else {
        area.width
    };
    let width = ((plates_width + 1) / shown as u16)
        .saturating_sub(1)
        .clamp(PLATE_MIN, 28);
    for (i, monster) in here.iter().take(shown).enumerate() {
        let x = area.x + i as u16 * (width + 1);
        let rect = Rect {
            x,
            width: width.min(area.right().saturating_sub(x)),
            height: 4.min(area.height),
            ..area
        };
        let targeted = me.target_id == Some(monster.map_id);
        let block = Block::new()
            .borders(Borders::ALL)
            .border_type(BorderType::Rounded)
            .border_style(if targeted {
                Style::new().yellow()
            } else {
                Style::new().fg(DIM)
            })
            .title(Span::styled(
                truncate(
                    &format!("{}{}", if targeted { "▶ " } else { "" }, monster.name),
                    width as usize - 2,
                ),
                if monster.alive {
                    Style::new().bold()
                } else {
                    Style::new().fg(DIM)
                },
            ));
        let inner = block.inner(rect);
        frame.render_widget(block, rect);
        let lines = if monster.alive {
            let attackers: Vec<&str> = accounts
                .iter()
                .filter(|(_, s)| playing(s).is_some_and(|p| p.cell == me.cell && p.target_id == Some(monster.map_id)))
                .map(|(name, _)| name.as_str())
                .collect();
            let hp = format!("{}/{}", short_number(monster.hp), short_number(monster.max_hp));
            vec![
                Line::from(bar(inner.width, ratio(monster.hp, monster.max_hp), Color::Red, hp)),
                if attackers.is_empty() {
                    Line::styled("no one on it", Style::new().fg(DIM))
                } else {
                    Line::styled(
                        truncate(&format!("◀ {}", attackers.join(", ")), inner.width as usize),
                        Style::new().yellow(),
                    )
                },
            ]
        } else {
            vec![Line::styled("dead, respawning", Style::new().fg(DIM))]
        };
        frame.render_widget(Paragraph::new(lines), inner);
    }
    if shown < here.len() && area.height >= 2 {
        let x = area.x + shown as u16 * (width + 1);
        frame.render_widget(
            Paragraph::new(Line::styled(
                format!("+{} more", here.len() - shown),
                Style::new().fg(DIM),
            )),
            Rect {
                x,
                y: area.y + 1,
                width: area.right().saturating_sub(x),
                height: 1,
            },
        );
    }
}

/// The Room, as the chat's title: how many players the map has, as the game's bottom right says, then who else is on it outside the
/// player's cell, as many as `MAX_ROOM` with their cells.
fn room_title(me: &Player, map: &Map) -> Line<'static> {
    let mut spans = vec![
        Span::styled(" Room · ", Style::new().bold()),
        Span::raw(format!("{} player(s) in ", map.players.len())),
        Span::styled(room_name(map), Style::new().yellow()),
    ];
    let others: Vec<String> = map
        .players
        .iter()
        .filter(|p| p.cell != me.cell)
        .take(MAX_ROOM)
        .map(|p| {
            format!(
                "{} ({}{})",
                truncate(&p.name, 15),
                truncate(&p.cell, 8),
                if p.afk { ", afk" } else { "" }
            )
        })
        .collect();
    if !others.is_empty() {
        spans.push(Span::styled(format!(" · {}", others.join(", ")), Style::new().fg(DIM)));
    }
    spans.push(" ".into());
    Line::from(spans)
}

/// What the run changed in the inventory, under the cell: each stack it is filling as a bar toward its max stack, with the gain, and the
/// rate since it first rose and when it will be full at that rate once it has risen for `RATE_AFTER`, the fullest first; then what it
/// filled up and what's new, a line each, and what was spent or banked on one line that fits, the most spent first; those keep their
/// place before the bars on a short terminal. An item that just changed is yellow.
fn bags(frame: &mut Frame, bags: &Bags, area: Rect) {
    /// The name's column, and what the gain and the rate take after the bar.
    const NAME_COLUMN: usize = 24;
    const GAIN_W: u16 = 8;
    const RATE_W: u16 = 22;
    /// How wide a bar is, at least and at most.
    const BAR_MIN: u16 = 12;
    const BAR_MAX: u16 = 40;
    if area.height < 3 {
        return;
    }
    let now = now_ms();
    let title = match bags.start() {
        Some(start) => {
            let took = short_duration(start.elapsed_sec(now));
            match (start.run, start.ended_ms) {
                (Some(run), None) => format!("Bags · run {run} · {took}"),
                (Some(run), Some(_)) => format!("Bags · run {run} · ended after {took}"),
                (None, _) => format!("Bags · since skua-tui opened · {took}"),
            }
        }
        None => "Bags · reading…".into(),
    };
    let block = panel(&title);
    let inner = block.inner(area);
    let changes = bags.changes();
    let of = |kind: Kind| changes.iter().filter(move |c| c.kind == kind);
    let name_style = |change: &Change| {
        if change.fresh {
            Style::new().yellow().bold()
        } else {
            Style::new()
        }
    };

    let mut filling: Vec<&Change> = of(Kind::Filling).collect();
    filling.sort_by(|a, b| ratio(b.now, b.max).total_cmp(&ratio(a.now, a.max)));
    let bar_w = inner
        .width
        .saturating_sub(NAME_COLUMN as u16 + GAIN_W + RATE_W)
        .clamp(BAR_MIN, BAR_MAX);
    let mut bars: Vec<Line> = filling
        .iter()
        .map(|c| {
            let mut spans = vec![Span::styled(
                format!("{:<NAME_COLUMN$}", truncate(&c.name, NAME_COLUMN - 1)),
                name_style(c),
            )];
            spans.extend(bar(
                bar_w,
                ratio(c.now, c.max),
                Color::Cyan,
                format!("{}/{}", c.now, c.max),
            ));
            spans.push(Span::styled(format!(" {:>+7}", c.delta()), Style::new().green()));
            if let Some(rate) = bags.start().and_then(|s| c.per_hour(s, now)) {
                let left = (c.max - c.now) as f64 / rate * 3600.0;
                spans.push(Span::styled(
                    format!("  +{}/h ~{}", rate_text(rate), short_duration(left)),
                    Style::new().fg(DIM),
                ));
            }
            Line::from(spans)
        })
        .collect();
    if bars.is_empty() {
        bars.push(Line::styled("no stack filling yet", Style::new().fg(DIM)));
    }

    // A line each for what filled up and what's new, each item named as `show` says.
    let listed = |kind: Kind, head: Span<'static>, show: &dyn Fn(&Change) -> String| -> Option<Line<'static>> {
        let mut spans = vec![head];
        for (i, c) in of(kind).enumerate() {
            if i > 0 {
                spans.push(Span::raw(", "));
            }
            spans.push(Span::styled(show(c), name_style(c)));
        }
        (spans.len() > 1).then(|| Line::from(spans))
    };
    // What was spent or banked, on one line that fits: the stacks, most first, then the single items as one; the items that don't fit
    // are counted at the end, and where not even one piece fits, the line is only the count.
    let mut spent: Vec<&Change> = of(Kind::Spent).collect();
    spent.sort_by_key(|c| c.delta());
    let (singles, stacks): (Vec<&Change>, Vec<&Change>) = spent.into_iter().partition(|c| c.is_single());
    // Each piece, with how many items it names.
    let mut pieces: Vec<(String, Style, usize)> = stacks
        .iter()
        .map(|c| (format!("{} {}", c.name, c.delta()), name_style(c), 1))
        .collect();
    if !singles.is_empty() {
        let names: Vec<&str> = singles.iter().map(|c| c.name.as_str()).collect();
        let style = singles.iter().find(|c| c.fresh).map_or(Style::new(), |c| name_style(c));
        pieces.push((format!("{} -1 each", compact_names(&names)), style, singles.len()));
    }
    let spent_line = (!pieces.is_empty()).then(|| {
        let head = "▼ spent or banked: ";
        let width = inner.width as usize;
        let sep = |i: usize| if i == 0 { "" } else { " · " };
        // The most pieces that fit with the count of what they leave out.
        let fit = (1..=pieces.len()).rev().find_map(|shown| {
            let hidden: usize = pieces[shown..].iter().map(|p| p.2).sum();
            let more = (hidden > 0).then(|| format!(" · +{hidden} more"));
            let used = head.chars().count()
                + pieces[..shown]
                    .iter()
                    .enumerate()
                    .map(|(i, p)| sep(i).chars().count() + p.0.chars().count())
                    .sum::<usize>()
                + more.as_ref().map_or(0, |m| m.chars().count());
            (used <= width).then_some((shown, more))
        });
        let Some((shown, more)) = fit else {
            let items: usize = pieces.iter().map(|p| p.2).sum();
            return Line::styled(
                truncate(&format!("▼ {items} spent or banked"), width),
                Style::new().red(),
            );
        };
        let mut spans = vec![Span::styled(head, Style::new().red())];
        for (i, (text, style, _)) in pieces.into_iter().take(shown).enumerate() {
            spans.push(Span::styled(sep(i), Style::new().fg(DIM)));
            spans.push(Span::styled(text, style));
        }
        spans.extend(more.map(|more| Span::styled(more, Style::new().fg(DIM))));
        Line::from(spans)
    });

    let tail: Vec<Line> = [
        listed(Kind::Filled, "✓ filled this run: ".green(), &|c| {
            format!("{} (+{})", c.name, c.delta())
        }),
        listed(Kind::New, "★ new: ".magenta(), &|c| c.name.clone()),
        spent_line,
    ]
    .into_iter()
    .flatten()
    .collect();

    // The bars give way to the lines under them, a blank line between; where not even one bar fits with those, a line of counts.
    let rows = inner.height as usize;
    let rows_for_bars = rows.saturating_sub(tail.len() + usize::from(!tail.is_empty()));
    let lines = if rows_for_bars == 0 {
        let count = |kind: Kind, what: &str| {
            let n = of(kind).count();
            (n > 0).then(|| format!("{n} {what}"))
        };
        let counts: Vec<String> = [
            count(Kind::Filling, "filling"),
            count(Kind::Filled, "filled"),
            count(Kind::New, "new"),
            count(Kind::Spent, "spent"),
        ]
        .into_iter()
        .flatten()
        .collect();
        vec![Line::styled(counts.join(" · "), Style::new().fg(DIM))]
    } else {
        if bars.len() > rows_for_bars {
            let hidden = bars.len() - (rows_for_bars - 1);
            bars.truncate(rows_for_bars - 1);
            bars.push(Line::styled(format!("… {hidden} more filling"), Style::new().fg(DIM)));
        }
        let mut lines = bars;
        if !tail.is_empty() {
            lines.push(Line::raw(""));
        }
        lines.extend(tail);
        lines
    };
    frame.render_widget(Paragraph::new(lines).block(block), area);
}

/// Names that differ only by a trailing number, as one, the numbers in order: `Unidentified 1, 6, 9`; the others as they are, `; ` between.
fn compact_names(names: &[&str]) -> String {
    let mut groups: Vec<(&str, Option<Vec<u32>>)> = Vec::new();
    for name in names {
        match name
            .rsplit_once(' ')
            .and_then(|(stem, n)| Some((stem, n.parse::<u32>().ok()?)))
        {
            Some((stem, n)) => match groups.iter_mut().find_map(|(s, ns)| ns.as_mut().filter(|_| *s == stem)) {
                Some(ns) => ns.push(n),
                None => groups.push((stem, Some(vec![n]))),
            },
            None => groups.push((name, None)),
        }
    }
    groups
        .into_iter()
        .map(|(stem, ns)| match ns {
            Some(mut ns) => {
                ns.sort_unstable();
                let ns: Vec<String> = ns.iter().map(u32::to_string).collect();
                format!("{stem} {}", ns.join(", "))
            }
            None => stem.to_owned(),
        })
        .collect::<Vec<_>>()
        .join("; ")
}

/// The map's name with its room, as the game names it: `battleon-9999`.
fn room_name(map: &Map) -> String {
    format!("{}-{}", map.name, map.room_id)
}

/// What the Engine answered, or the line that says it hasn't yet or couldn't.
fn answered<T>(read: Option<&Result<T, Error>>) -> Result<&T, Line<'static>> {
    match read {
        None => Err(loading()),
        Some(Err(e)) => Err(read_error(e)),
        Some(Ok(value)) => Ok(value),
    }
}

/// Green over half, yellow over a quarter, else red, as is a dead one.
fn hp_color(hp: f64, alive: bool) -> Color {
    if !alive || hp <= 0.25 {
        Color::Red
    } else if hp > 0.5 {
        Color::Green
    } else {
        Color::Yellow
    }
}

fn hp_text(hp: i64, max_hp: i64, alive: bool) -> String {
    if alive {
        format!("{}/{}", short_number(hp), short_number(max_hp))
    } else {
        "dead".into()
    }
}

/// The Script's goal as a tree, each step what the one above needs, with counts, pace and time left; then how often a death reset the
/// wave, which is why a farm loops.
fn goal_tree(goal: &ScriptGoal) -> Vec<Line<'static>> {
    let mut lines = Vec::new();
    let mut depth = 0;
    let mut step = |kind: &str, text: String, right: Vec<Span<'static>>| {
        let mut spans = vec![
            Span::styled(format!("{}└ {kind:<6}", "   ".repeat(depth)), Style::new().fg(DIM)),
            Span::raw(format!("{:<34}", truncate(&text, 33))),
        ];
        spans.extend(right);
        lines.push(Line::from(spans));
        depth += 1;
    };
    if let Some(quest) = &goal.quest {
        step("quest", quest.clone(), vec![]);
    }
    for (kind, item) in [("buy", &goal.buy), ("farm", &goal.farm)] {
        let Some(item) = item else { continue };
        let have = item.have.unwrap_or(0);
        let mut right = vec![Span::styled(
            format!("{:>13}", format!("{}/{}", short_number(have), short_number(item.want))),
            Style::new().bold(),
        )];
        if let Some(rate) = item.per_hour.filter(|r| *r > 0.0) {
            let left = ((item.want - have).max(0) as f64 / rate * 3600.0).round();
            right.push(Span::styled(
                format!("   +{}/h  ~{}", short_number(rate.round() as i64), short_duration(left)),
                Style::new().fg(DIM),
            ));
        }
        step(kind, item.item.clone(), right);
    }
    if let Some(now) = &goal.now {
        step("now", now.clone(), vec![]);
    }
    if goal.resets > 0 {
        lines.push(Line::styled(
            format!(
                "⟲ wave reset {}× since this farm began: each leader death restarts the wave",
                goal.resets
            ),
            Style::new().yellow(),
        ));
    }
    lines
}

/// A big number in a few characters: `833`, `20k`, `1.0m`.
fn short_number(n: i64) -> String {
    match n {
        n if n >= 1_000_000 => format!("{:.1}m", n as f64 / 1e6),
        n if n >= 10_000 => format!("{}k", n / 1000),
        n => n.to_string(),
    }
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

fn gauge(label: &str, width: u16, ratio: f64, color: Color, text: String) -> Line<'static> {
    let mut spans = vec![Span::styled(format!("{label} "), Style::new().fg(DIM))];
    spans.extend(bar(width, ratio, color, text));
    Line::from(spans)
}

/// A bar `width` cells long, filled to `ratio` in `color`, with `text` centred on it.
fn bar(width: u16, ratio: f64, color: Color, text: String) -> Vec<Span<'static>> {
    let width = width as usize;
    let filled = (ratio.clamp(0.0, 1.0) * width as f64).round() as usize;
    let start = width.saturating_sub(text.chars().count()) / 2;
    let cells: Vec<char> = (0..width)
        .map(|i| {
            text.chars()
                .nth(i.wrapping_sub(start))
                .filter(|_| i >= start)
                .unwrap_or(' ')
        })
        .collect();
    let (done, todo) = cells.split_at(filled.min(width));
    vec![
        Span::styled(done.iter().collect::<String>(), Style::new().black().bg(color).bold()),
        Span::styled(todo.iter().collect::<String>(), Style::new().bg(SELECTED)),
    ]
}

/// The items on the chosen category's shelf, scrolled, under a row of the categories that hold something; a click on the list puts the keys
/// on it, and its border turns blue.
fn inventory(frame: &mut Frame, app: &App, status: &Status, detail: Option<&Detail>, area: Rect) {
    let read = detail.and_then(|d| d.inventory.as_ref());
    let mut block = panel("Inventory");
    if app.inventory_focused {
        block = block.border_style(Style::new().blue());
    }
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
    let inner = block.inner(area);
    app.hits.borrow_mut().inventory = area;
    let inv = match read {
        _ if playing(status).is_none() => return frame.render_widget(Paragraph::new(not_playing()).block(block), area),
        None => return frame.render_widget(Paragraph::new(loading()).block(block), area),
        Some(Err(e)) => return frame.render_widget(Paragraph::new(read_error(e)).block(block), area),
        Some(Ok(inv)) => inv,
    };

    let mut shelves = Vec::new();
    let mut spans = Vec::new();
    let mut x = inner.x;
    for (shelf, count) in Shelf::counts(&inv.items) {
        let text = format!(" {} {count} ", shelf.title());
        let width = text.chars().count() as u16;
        if x + width > inner.right() {
            break;
        }
        shelves.push((Rect::new(x, inner.y, width, 1), shelf));
        spans.push(if shelf == app.shelf {
            text.black().on_blue().bold()
        } else {
            text.fg(DIM)
        });
        spans.push(" ".into());
        x += width + 1;
    }
    let items: Vec<_> = inv.items.iter().filter(|i| app.shelf.holds(i)).collect();
    let page = inner.height.saturating_sub(3) as usize;
    let max_scroll = items.len().saturating_sub(page);
    let scroll = app.inventory_scroll.min(max_scroll);
    {
        let mut hits = app.hits.borrow_mut();
        hits.shelves = shelves;
        hits.inventory_max_scroll = max_scroll;
        hits.inventory_page = page;
    }
    if items.len() > page {
        block = block.title_bottom(dim_right(&format!(
            "{}–{} of {} ",
            scroll + 1,
            (scroll + page).min(items.len()),
            items.len()
        )));
    }

    let mut lines = vec![
        Line::from(spans),
        Line::raw(""),
        Line::styled(
            format!("{:<34}{:<14}{}", "ITEM", "QTY", "CATEGORY"),
            Style::new().fg(DIM).bold(),
        ),
    ];
    lines.extend(items.iter().skip(scroll).take(page).map(|item| {
        let qty = if item.max_stack > 1 {
            format!("{}/{}", item.qty, item.max_stack)
        } else {
            item.qty.to_string()
        };
        let name = format!("{}{}", truncate(&item.name, 31), if item.equipped { " ✓" } else { "" });
        Line::raw(format!("{name:<34}{qty:<14}{}", item.category))
    }));
    if items.is_empty() {
        lines.push(Line::styled("nothing on this shelf", Style::new().fg(DIM)));
    }
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
        Some(Ok(q)) => quest_lines(&q.quests, running, width),
    };
    frame.render_widget(Paragraph::new(lines).block(panel("Quests")), area);
}

/// The quests in progress, each with its unmet requirements one per line in columns: the count, the rate and the time left at that rate
/// once the Engine knows it, and how long since the count rose. The quest says how many requirements are done and how long since any of
/// them rose. The other loaded quests are only counted.
fn quest_lines(quests: &[Quest], running: bool, width: usize) -> Vec<Line<'static>> {
    const COUNT: usize = 12;
    const RATE: usize = 18;
    const IDLE: usize = 7;
    let name_width = width.saturating_sub(3 + 1 + COUNT + RATE + IDLE).min(32);
    let idle_style = |idle: Option<f64>| Style::new().fg(idle.map_or(DIM, |sec| idle_color(sec, running)));
    let mut lines = vec![Line::raw("")];
    let in_progress: Vec<&Quest> = quests.iter().filter(|q| q.status == "inProgress").collect();
    if in_progress.is_empty() {
        lines.push(Line::styled(" no quest in progress", Style::new().fg(DIM)));
        lines.push(Line::raw(""));
    }
    for quest in in_progress {
        // A requirement the bank covers is done: the Script takes it out for the turn-in.
        let unmet: Vec<_> = quest.requirements.iter().filter(|r| r.owned() < r.qty).collect();
        // The rise that met a requirement counts too, as the Engine counts it.
        let idle = quest.requirements.iter().filter_map(|r| r.idle_sec).reduce(f64::min);
        let done = format!(
            "{}/{} done",
            quest.requirements.len() - unmet.len(),
            quest.requirements.len()
        );
        lines.push(Line::from(vec![
            format!(" {:<w$} ", truncate(&quest.name, name_width + 2), w = name_width + 2).bold(),
            Span::styled(format!("{done:>COUNT$}{:RATE$}", ""), Style::new().fg(DIM)),
            Span::styled(
                format!("{:>IDLE$}", idle.map(short_duration).unwrap_or_default()),
                idle_style(idle),
            ),
        ]));
        for r in unmet {
            let rate = r.gain_per_hour.filter(|rate| *rate > 0.0).map(|rate| {
                let left = (r.qty - r.owned()) as f64 / rate * 3600.0;
                format!("+{}/h ~{}", rate_text(rate), short_duration(left))
            });
            let rate = rate.or_else(|| (r.in_bank > 0).then(|| format!("{} in bank", r.in_bank)));
            lines.push(Line::from(vec![
                Span::raw(format!(
                    "   {:<name_width$} {:>COUNT$}",
                    truncate(&r.name, name_width),
                    format!("{}/{}", r.owned(), r.qty)
                )),
                Span::styled(format!("{:>RATE$}", rate.unwrap_or_default()), Style::new().fg(DIM)),
                Span::styled(
                    format!("{:>IDLE$}", r.idle_sec.map(short_duration).unwrap_or_default()),
                    idle_style(r.idle_sec),
                ),
            ]));
        }
        lines.push(Line::raw(""));
    }
    let completable = quests.iter().filter(|q| q.status == "completable").count();
    let not_accepted = quests.iter().filter(|q| q.status == "notAccepted").count();
    let others: Vec<String> = [(completable, "completable"), (not_accepted, "not accepted")]
        .into_iter()
        .filter(|(n, _)| *n > 0)
        .map(|(n, what)| format!("{n} {what}"))
        .collect();
    if !others.is_empty() {
        lines.push(Line::styled(format!(" {}", others.join(" · ")), Style::new().fg(DIM)));
    }
    lines
}

/// What the run's Script last logged, and how long ago: what it is doing, or what it is stuck on.
fn doing(run: &ScriptRun, detail: Option<&Detail>) -> Line<'static> {
    let Some(entry) = detail
        .and_then(|d| d.script_line.as_ref())
        .filter(|e| e.run == Some(run.number))
    else {
        return Line::styled("no Script log yet", Style::new().fg(DIM));
    };
    let text = entry.text.as_deref().unwrap_or_default();
    // Scripts stamp their own lines with [hh:mm:ss]; the age says when.
    let text = match text.split_once("] ") {
        Some((stamp, rest)) if stamp.starts_with('[') && stamp.len() == 9 => rest,
        _ => text,
    };
    let age = (now_ms() - entry.ts) as f64 / 1000.0;
    Line::from(vec![
        Span::styled(
            format!("{} ago · ", short_duration(age)),
            Style::new().fg(idle_color(age, true)),
        ),
        Span::raw(first_line(text).to_owned()),
    ])
}

/// A run whose quests have gone the stall time without progress: stuck when it kills nothing either, else grinding, as for a rare drop.
#[derive(Debug, Clone, Copy, PartialEq)]
enum Stall {
    Stuck(f64),
    Grinding(f64),
}

/// How long `run`'s quests have gone without progress, once that is a stall, and whether it still kills; an Engine that doesn't count
/// kills says nothing about them, so its stall is stuck.
fn stall(run: &ScriptRun) -> Option<Stall> {
    let sec = run.quest_idle_sec.filter(|sec| *sec >= STALL_SEC)?;
    Some(if run.kills_per_min.is_some_and(|rate| rate > 0.0) {
        Stall::Grinding(sec)
    } else {
        Stall::Stuck(sec)
    })
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

/// The game's picture, as large as the tab allows at the game's shape, where the terminal draws pictures; the map's players and monsters
/// under it, as room allows.
fn game(frame: &mut Frame, app: &App, name: &str, status: &Status, detail: Option<&Detail>, area: Rect) {
    let read = detail.and_then(|d| d.map.as_ref());
    let title = match read {
        Some(Ok(map)) if playing(status).is_some() => format!("Game · {} · room {}", map.name, map.room_id),
        _ => "Game".into(),
    };
    let block = panel(&title).title_bottom(dim_right("p opens it in Preview "));
    let inner = block.inner(area);
    frame.render_widget(block, area);
    if playing(status).is_none() {
        return frame.render_widget(Paragraph::new(not_playing()), inner);
    }
    let mut below = inner;
    match detail.and_then(|d| d.picture.as_ref()) {
        _ if !crate::picture::supported() => {
            frame.render_widget(
                Paragraph::new(Line::styled(
                    "The game's picture shows here in Ghostty, kitty or WezTerm; p opens it in Preview.",
                    Style::new().fg(DIM),
                )),
                Rect { height: 1, ..inner },
            );
            below = Rect {
                y: inner.y + 2,
                height: inner.height.saturating_sub(2),
                ..inner
            };
        }
        None => {
            frame.render_widget(Paragraph::new(loading()), Rect { height: 1, ..inner });
            below = Rect {
                y: inner.y + 2,
                height: inner.height.saturating_sub(2),
                ..inner
            };
        }
        Some(Err(e)) => {
            frame.render_widget(Paragraph::new(read_error(e)), Rect { height: 1, ..inner });
            below = Rect {
                y: inner.y + 2,
                height: inner.height.saturating_sub(2),
                ..inner
            };
        }
        Some(Ok(picture)) if app.modal.is_none() => {
            // A cell is about twice as tall as it is wide.
            let aspect = picture.height.max(1) as f64 / picture.width.max(1) as f64;
            let mut width = inner.width;
            let mut height = ((width as f64 * aspect) / 2.0).round() as u16;
            if height > inner.height {
                height = inner.height;
                width = ((height as f64 * 2.0) / aspect).round() as u16;
            }
            let rect = Rect {
                x: inner.x + (inner.width - width.min(inner.width)) / 2,
                y: inner.y,
                width: width.min(inner.width),
                height,
            };
            app.hits.borrow_mut().picture = Some((name.to_owned(), picture.frame, rect));
            below = Rect {
                y: inner.y + height + 1,
                height: inner.height.saturating_sub(height + 1),
                ..inner
            };
        }
        Some(Ok(_)) => {}
    }
    let Some(Ok(map)) = read else { return };
    let mut lines = vec![Line::styled(
        format!(
            "PLAYERS · {}    MONSTERS · {}    cells: {}",
            map.players.len(),
            map.monsters.len(),
            map.cells.join(", ")
        ),
        Style::new().fg(DIM).bold(),
    )];
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
    lines.extend(map.monsters.iter().map(|m| {
        let style = if m.alive { Style::new() } else { Style::new().fg(DIM) };
        Line::styled(
            format!("  {:<4}{:<24}{:<12}{}/{}", m.map_id, m.name, m.cell, m.hp, m.max_hp),
            style,
        )
    }));
    frame.render_widget(Paragraph::new(lines), below);
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
    let used = line.width() as u16;
    frame.render_widget(Paragraph::new(line), area);
    // The selected Engine's details, which the Overview leaves out, where the status line leaves room for them.
    if let Some(row) = app.selected_row()
        && let Some(EngineView::Up { hello, status }) = app.engine(&row.name)
    {
        let build = hello
            .build
            .split_once('+')
            .map_or(hello.build.clone(), |(version, commit)| {
                format!("{version}+{}", commit.chars().take(7).collect::<String>())
            });
        let info = format!(
            "Engine {} · pid {} · {build} · up {} ",
            status.engine.name,
            status.engine.pid,
            duration(status.engine.uptime_sec)
        );
        let width = info.chars().count() as u16;
        if used + 2 + width <= area.width {
            let right = Rect {
                x: area.right() - width,
                width,
                ..area
            };
            frame.render_widget(Paragraph::new(Line::styled(info, Style::new().fg(DIM))), right);
        }
    }
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
