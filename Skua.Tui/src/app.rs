//! skua-tui's state and keys: accounts by group, selection, marks, tabs, the command palette and the actions' pickers. It does no I/O: an
//! action queues [`Job`]s in [`App::jobs`], and their [`Outcome`]s come back through [`App::on_outcome`].

use std::collections::{BTreeMap, BTreeSet, HashSet, VecDeque};
use std::path::PathBuf;

use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyEventKind, KeyModifiers};

use crate::actions::{Job, Op, Outcome, Reply};
use crate::chat::{ChatUpdate, MAX_CHAT_ENTRIES, Update};
use crate::dto::{LogEntry, Question, ScriptOption, ScriptsSearch, Server};
use crate::engine::{Error, SCRIPT_RUNNING};
use crate::poller::{EngineView, Focus, Snapshot};

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub enum Tab {
    #[default]
    Overview,
    Inventory,
    Quests,
    Logs,
    Game,
    Chat,
}

impl Tab {
    pub const ALL: [Tab; 6] = [
        Tab::Overview,
        Tab::Inventory,
        Tab::Quests,
        Tab::Logs,
        Tab::Game,
        Tab::Chat,
    ];

    pub fn title(self) -> &'static str {
        match self {
            Tab::Overview => "Overview",
            Tab::Inventory => "Inventory",
            Tab::Quests => "Quests",
            Tab::Logs => "Logs",
            Tab::Game => "Game",
            Tab::Chat => "Chat",
        }
    }

    fn index(self) -> usize {
        Tab::ALL.iter().position(|t| *t == self).unwrap_or(0)
    }
}

/// An account, or an Engine no account names, under its group.
#[derive(Debug, Clone, PartialEq)]
pub struct Row {
    pub group: String,
    pub name: String,
}

pub const UNGROUPED: &str = "Ungrouped";
pub const OTHER_ENGINES: &str = "Other Engines";

/// A command palette entry; choosing it presses its key.
pub struct Command {
    pub title: &'static str,
    pub key: char,
}

pub const COMMANDS: &[Command] = &[
    Command {
        title: "start Engine",
        key: 'E',
    },
    Command {
        title: "stop Engine",
        key: 'X',
    },
    Command {
        title: "log in…",
        key: 'L',
    },
    Command {
        title: "log out",
        key: 'O',
    },
    Command {
        title: "start Script…",
        key: 's',
    },
    Command {
        title: "stop Script",
        key: 'x',
    },
    Command {
        title: "join map…",
        key: 'J',
    },
    Command {
        title: "answer Question",
        key: 'd',
    },
    Command {
        title: "update Scripts",
        key: 'U',
    },
    Command {
        title: "mark whole group",
        key: 'a',
    },
    Command {
        title: "filter accounts…",
        key: '/',
    },
    Command {
        title: "show the game",
        key: 'S',
    },
    Command {
        title: "keys",
        key: '?',
    },
    Command {
        title: "quit",
        key: 'q',
    },
];

pub const KEYS: &[(&str, &str)] = &[
    ("j/k ↑/↓", "move"),
    ("space", "mark account"),
    ("a", "mark its whole group"),
    ("esc", "clear marks and filter"),
    ("tab ⇧tab", "Overview, Inventory, Quests, Logs, Game, Chat"),
    ("1–6", "pick a tab"),
    ("enter", "Chat: type, then enter sends; /w name text whispers"),
    (":", "command palette"),
    ("/", "filter accounts"),
    ("E / X", "start / stop Engine (stop asks first)"),
    ("L / O", "log in (server picker) / log out"),
    ("s / x", "start Script (search, options) / stop"),
    ("J", "join a map: map[-room] [cell] [pad]"),
    ("d", "answer the selected account's Question"),
    ("U", "update the Scripts from the Script Source"),
    ("q", "quit"),
];

/// The Chat tab: the followed Engine's game messages, oldest first, and the input line.
#[derive(Debug, Default)]
pub struct Chat {
    /// The Engine followed, and which follow.
    following: Option<(String, u64)>,
    pub entries: VecDeque<LogEntry>,
    /// Why the follow ended, once it has.
    pub ended: Option<String>,
    pub typing: bool,
    pub input: String,
    /// What became of the last message sent, or why it wasn't.
    pub note: Option<Note>,
}

impl Chat {
    pub fn engine(&self) -> Option<&str> {
        self.following.as_ref().map(|(engine, _)| engine.as_str())
    }

    pub fn follow(&mut self, engine: String, id: u64) {
        if self.engine() != Some(engine.as_str()) {
            self.note = None;
        }
        self.following = Some((engine, id));
        self.entries.clear();
        self.ended = None;
    }

    pub fn unfollow(&mut self) {
        self.following = None;
        self.entries.clear();
        self.ended = None;
        self.typing = false;
    }
}

/// What a picker shows until its Engine answers: None while it reads.
pub type Reading<T> = Option<Result<T, Error>>;

pub enum Modal {
    Palette {
        query: String,
        selected: usize,
    },
    Filter {
        query: String,
    },
    Help,
    /// Runs `jobs` on `y`.
    Confirm {
        text: String,
        jobs: Vec<Job>,
    },
    /// The servers, read from `engine`, to log the targets in to.
    Servers {
        engine: String,
        servers: Reading<Vec<Server>>,
        selected: usize,
    },
    /// The Script Source's Scripts matching `query`, searched in `engine`.
    Scripts {
        engine: String,
        query: String,
        found: Reading<ScriptsSearch>,
        selected: usize,
    },
    /// `script`'s options, read from `engine`, to start it on the targets with.
    Options {
        engine: String,
        script: String,
        fields: Reading<Vec<Field>>,
        selected: usize,
    },
    Join {
        query: String,
    },
    /// The pending Questions of one Engine; the oldest is answered first.
    Question {
        engine: String,
        questions: Reading<Vec<Question>>,
        selected: usize,
    },
}

/// One Script option in the options form, with its value as edited.
#[derive(Debug, Clone)]
pub struct Field {
    pub option: ScriptOption,
    pub value: String,
}

impl Field {
    pub fn editable(&self) -> bool {
        !self.option.transient
    }

    fn is_text(&self) -> bool {
        !matches!(self.option.kind.as_str(), "bool" | "enum")
    }

    fn cycle(&mut self, by: isize) {
        let choices: Vec<String> = match self.option.kind.as_str() {
            "bool" => vec!["True".into(), "False".into()],
            "enum" => self.option.choices.clone().unwrap_or_default(),
            _ => return,
        };
        if choices.is_empty() {
            return;
        }
        let at = choices
            .iter()
            .position(|c| c.eq_ignore_ascii_case(&self.value))
            .unwrap_or(0);
        self.value = choices[(at as isize + by).rem_euclid(choices.len() as isize) as usize].clone();
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Tone {
    Info,
    Pending,
    Done,
    Failed,
}

/// A line about what an action did, or is doing, to an account's Engine.
#[derive(Debug, Clone, PartialEq)]
pub struct Note {
    pub text: String,
    pub tone: Tone,
}

impl Note {
    fn new(text: impl Into<String>, tone: Tone) -> Note {
        Note {
            text: text.into(),
            tone,
        }
    }
}

pub struct App {
    pub skua_dir: PathBuf,
    /// None until the first poll.
    pub snapshot: Option<Snapshot>,
    pub selected: usize,
    pub marks: BTreeSet<String>,
    pub tab: Tab,
    pub filter: String,
    pub modal: Option<Modal>,
    /// Shown until the next key.
    pub toast: Option<Note>,
    /// The last action on each account's Engine, until the next one or esc.
    pub activity: BTreeMap<String, Note>,
    /// The jobs the actions queued, for the caller to run.
    pub jobs: Vec<Job>,
    pub chat: Chat,
    pub quit: bool,
}

impl App {
    pub fn new(skua_dir: PathBuf) -> App {
        App {
            skua_dir,
            snapshot: None,
            selected: 0,
            marks: BTreeSet::new(),
            tab: Tab::Overview,
            filter: String::new(),
            modal: None,
            toast: None,
            activity: BTreeMap::new(),
            jobs: Vec::new(),
            chat: Chat::default(),
            quit: false,
        }
    }

    /// Keeps the selected account selected, wherever the new rows put it.
    pub fn set_snapshot(&mut self, snapshot: Snapshot) {
        let selected = self.selected_row();
        self.snapshot = Some(snapshot);
        if let Some(row) = selected
            && let Some(i) = self.rows().iter().position(|r| *r == row)
        {
            self.selected = i;
        }
        self.clamp_selection();
    }

    /// The accounts in the Manager's groups, then the ungrouped ones, then the Engines no account names; filtered.
    pub fn rows(&self) -> Vec<Row> {
        let Some(snapshot) = &self.snapshot else {
            return Vec::new();
        };
        let mut rows = Vec::new();
        if let Ok(list) = &snapshot.accounts {
            let mut grouped = HashSet::new();
            for group in &list.groups {
                for username in &group.usernames {
                    if let Some(account) = list.accounts.iter().find(|a| a.username.eq_ignore_ascii_case(username)) {
                        rows.push(Row {
                            group: group.name.clone(),
                            name: account.name.clone(),
                        });
                        grouped.insert(account.name.as_str());
                    }
                }
            }
            for account in list.accounts.iter().filter(|a| !grouped.contains(a.name.as_str())) {
                rows.push(Row {
                    group: UNGROUPED.into(),
                    name: account.name.clone(),
                });
            }
        }
        for name in snapshot.engines.keys() {
            if !rows.iter().any(|r| &r.name == name) {
                rows.push(Row {
                    group: OTHER_ENGINES.into(),
                    name: name.clone(),
                });
            }
        }
        let filter = self.filter.to_lowercase();
        rows.retain(|r| r.name.contains(&filter));
        rows
    }

    pub fn selected_row(&self) -> Option<Row> {
        self.rows().into_iter().nth(self.selected)
    }

    pub fn engine(&self, name: &str) -> Option<&EngineView> {
        self.snapshot.as_ref()?.engines.get(name)
    }

    pub fn focus(&self) -> Focus {
        let engine = self
            .selected_row()
            .map(|r| r.name)
            .filter(|name| self.engine(name).is_some());
        Focus { engine, tab: self.tab }
    }

    /// The Engine whose game messages the Chat tab follows, by name and pid: the selected one while it is up and the tab is shown.
    pub fn chat_target(&self) -> Option<(String, i64)> {
        if self.tab != Tab::Chat {
            return None;
        }
        let name = self.selected_row()?.name;
        match self.engine(&name)? {
            EngineView::Up { hello, .. } => Some((name, hello.pid)),
            _ => None,
        }
    }

    pub fn on_chat(&mut self, update: ChatUpdate) {
        if self.chat.following.as_ref().map(|(_, id)| *id) != Some(update.follow) {
            return;
        }
        match update.update {
            Update::Page { entries, gap } => {
                if gap {
                    self.chat.entries.push_back(LogEntry {
                        seq: 0,
                        ts: 0,
                        kind: "gap".into(),
                        run: None,
                        text: None,
                        event_type: None,
                        data: None,
                    });
                }
                self.chat.entries.extend(entries);
                let excess = self.chat.entries.len().saturating_sub(MAX_CHAT_ENTRIES);
                self.chat.entries.drain(..excess);
            }
            Update::Ended(why) => self.chat.ended = Some(why),
        }
    }

    /// The marked accounts, else the selected one: what an action acts on.
    pub fn targets(&self) -> Vec<String> {
        if self.marks.is_empty() {
            self.selected_row().map(|r| r.name).into_iter().collect()
        } else {
            self.marks.iter().cloned().collect()
        }
    }

    pub fn targets_label(&self) -> String {
        match self.targets().as_slice() {
            [] => "nobody".into(),
            [one] => one.clone(),
            many => format!("{} accounts", many.len()),
        }
    }

    pub fn palette_matches(query: &str) -> Vec<&'static Command> {
        COMMANDS.iter().filter(|c| fuzzy(c.title, query)).collect()
    }

    pub fn on_key(&mut self, key: KeyEvent) {
        if key.kind != KeyEventKind::Press {
            return;
        }
        if key.modifiers.contains(KeyModifiers::CONTROL) && key.code == KeyCode::Char('c') {
            self.quit = true;
            return;
        }
        self.toast = None;
        match self.modal.take() {
            Some(modal) => self.on_modal_key(modal, key),
            None if self.chat.typing && self.tab == Tab::Chat => self.on_chat_key(key),
            None => self.on_main_key(key),
        }
    }

    fn on_chat_key(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Esc => self.chat.typing = false,
            KeyCode::Enter => self.send_chat(),
            KeyCode::Backspace => _ = self.chat.input.pop(),
            KeyCode::Char(c) => self.chat.input.push(c),
            _ => {}
        }
    }

    /// Sends the input line to the followed Engine; an empty one stops typing.
    fn send_chat(&mut self) {
        let Some(engine) = self.chat.engine().map(str::to_owned) else {
            return;
        };
        if self.chat.input.trim().is_empty() {
            self.chat.typing = false;
            return;
        }
        match chat_op(&self.chat.input) {
            Ok(op) => {
                self.chat.input.clear();
                self.chat.note = Some(Note::new("sending…", Tone::Pending));
                self.jobs.push(Job { engine, op });
            }
            Err(why) => self.chat.note = Some(Note::new(format!("not sent: {why}"), Tone::Failed)),
        }
    }

    fn on_main_key(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Char('j') | KeyCode::Down => self.move_selection(1),
            KeyCode::Char('k') | KeyCode::Up => self.move_selection(-1),
            KeyCode::Enter if self.tab == Tab::Chat && self.chat.engine().is_some() => self.chat.typing = true,
            KeyCode::Tab => self.tab = Tab::ALL[(self.tab.index() + 1) % Tab::ALL.len()],
            KeyCode::BackTab => self.tab = Tab::ALL[(self.tab.index() + Tab::ALL.len() - 1) % Tab::ALL.len()],
            KeyCode::Esc => {
                self.marks.clear();
                self.filter.clear();
                self.activity.retain(|_, note| note.tone == Tone::Pending);
            }
            KeyCode::Char(c) => self.press(c),
            _ => {}
        }
    }

    /// A key on the main screen, also what a palette command presses.
    fn press(&mut self, c: char) {
        match c {
            'q' => self.quit = true,
            ' ' => {
                if let Some(row) = self.selected_row()
                    && !self.marks.remove(&row.name)
                {
                    self.marks.insert(row.name);
                }
            }
            'a' => self.mark_group(),
            ':' => {
                self.modal = Some(Modal::Palette {
                    query: String::new(),
                    selected: 0,
                })
            }
            '/' => {
                self.modal = Some(Modal::Filter {
                    query: self.filter.clone(),
                })
            }
            '?' => self.modal = Some(Modal::Help),
            'S' => self.tab = Tab::Game,
            '1'..='6' => self.tab = Tab::ALL[c as usize - '1' as usize],
            'E' => self.start_engines(),
            'X' => self.confirm_stop_engines(),
            'L' => {
                if let Some(engine) = self.source("log in") {
                    self.jobs.push(Job {
                        engine: engine.clone(),
                        op: Op::Servers,
                    });
                    self.modal = Some(Modal::Servers {
                        engine,
                        servers: None,
                        selected: 0,
                    });
                }
            }
            'O' => self.act(Op::Logout, "logging out…"),
            's' => {
                if let Some(engine) = self.source("start a Script") {
                    self.search(&engine, String::new());
                    self.modal = Some(Modal::Scripts {
                        engine,
                        query: String::new(),
                        found: None,
                        selected: 0,
                    });
                }
            }
            'x' => self.act(Op::ScriptStop, "stopping the Script…"),
            'J' => {
                if self.source("join a map").is_some() {
                    self.modal = Some(Modal::Join { query: String::new() });
                }
            }
            'd' => {
                let engine = self.selected_row().map(|r| r.name);
                match engine.filter(|name| matches!(self.engine(name), Some(EngineView::Up { .. }))) {
                    Some(engine) => {
                        self.jobs.push(Job {
                            engine: engine.clone(),
                            op: Op::Dialogs,
                        });
                        self.modal = Some(Modal::Question {
                            engine,
                            questions: None,
                            selected: 0,
                        });
                    }
                    None => self.toast = Some(Note::new("answer: the selected account has no Engine up", Tone::Failed)),
                }
            }
            'U' => {
                // Every Engine of this data folder shares its Scripts, so one update serves them all.
                if let Some(engine) = self.source("update Scripts") {
                    self.queue(engine, Op::ScriptsUpdate, "updating the Scripts…");
                }
            }
            _ => {}
        }
    }

    /// The first target whose Engine is up, for a picker to read from; else says so.
    fn source(&mut self, what: &str) -> Option<String> {
        let up = self
            .targets()
            .into_iter()
            .find(|name| matches!(self.engine(name), Some(EngineView::Up { .. })));
        if up.is_none() {
            self.toast = Some(Note::new(
                format!("{what}: no Engine is up for {}", self.targets_label()),
                Tone::Failed,
            ));
        }
        up
    }

    /// Queues `op` for each target whose Engine is up, and notes why the others are left alone.
    fn act(&mut self, op: Op, pending: &str) {
        let mut queued = 0;
        for name in self.targets() {
            match self.engine(&name) {
                Some(EngineView::Up { .. }) => {
                    self.queue(name, op.clone(), pending);
                    queued += 1;
                }
                view => {
                    let reason = left_alone(view);
                    self.fail(name, reason);
                }
            }
        }
        if queued > 1 {
            self.toast = Some(Note::new(format!("{pending} {queued} accounts"), Tone::Pending));
        }
    }

    fn fail(&mut self, engine: String, text: String) {
        self.toast = Some(Note::new(format!("{engine}: {text}"), Tone::Failed));
        self.activity.insert(engine, Note::new(text, Tone::Failed));
    }

    fn queue(&mut self, engine: String, op: Op, pending: &str) {
        self.activity.insert(engine.clone(), Note::new(pending, Tone::Pending));
        self.toast = Some(Note::new(format!("{engine}: {pending}"), Tone::Pending));
        self.jobs.push(Job { engine, op });
    }

    fn search(&mut self, engine: &str, query: String) {
        self.jobs.push(Job {
            engine: engine.to_owned(),
            op: Op::ScriptsSearch { query },
        });
    }

    /// Starts an Engine for each target that has none; one that answers, or is of another protocol, is left as it is.
    fn start_engines(&mut self) {
        for name in self.targets() {
            match self.engine(&name) {
                None | Some(EngineView::Offline) => self.queue(name, Op::StartEngine, "starting a windowless Engine…"),
                Some(EngineView::Up { .. }) => {
                    self.toast = Some(Note::new(format!("{name}: its Engine already runs"), Tone::Info));
                }
                view => {
                    let reason = left_alone(view);
                    self.fail(name, reason);
                }
            }
        }
    }

    fn confirm_stop_engines(&mut self) {
        let mut names = Vec::new();
        for name in self.targets() {
            match self.engine(&name) {
                Some(EngineView::Up { .. }) => names.push(name),
                None | Some(EngineView::Offline) => {}
                view => {
                    let reason = left_alone(view);
                    self.fail(name, reason);
                }
            }
        }
        if names.is_empty() {
            if self.toast.is_none() {
                self.toast = Some(Note::new(
                    format!("stop Engine: no Engine runs for {}", self.targets_label()),
                    Tone::Info,
                ));
            }
            return;
        }
        let text = format!(
            "Stop the Engine of {}? Its game closes. An Engine running a Script or a command refuses; x stops the Script first.",
            names.join(", ")
        );
        let jobs = names
            .into_iter()
            .map(|engine| Job {
                engine,
                op: Op::StopEngine,
            })
            .collect();
        self.modal = Some(Modal::Confirm { text, jobs });
    }

    /// Applies what an Engine answered: a picker's data, or an action's result on its account.
    pub fn on_outcome(&mut self, outcome: Outcome) {
        let Outcome { job, result } = outcome;
        let engine = job.engine;
        match (&mut self.modal, &job.op, result) {
            (Some(Modal::Servers { engine: e, servers, .. }), Op::Servers, result) if *e == engine => {
                *servers = Some(result.and_then(|r| match r {
                    Reply::Servers(list) => Ok(list),
                    r => Err(unexpected(r)),
                }));
            }
            (_, Op::Servers, _) => {}
            (
                Some(Modal::Scripts {
                    engine: e,
                    query,
                    found,
                    selected,
                }),
                Op::ScriptsSearch { query: asked },
                result,
            ) if *e == engine && query == asked => {
                *selected = 0;
                *found = Some(result.and_then(|r| match r {
                    Reply::Scripts(found) => Ok(found),
                    r => Err(unexpected(r)),
                }));
            }
            (_, Op::ScriptsSearch { .. }, _) => {}
            (
                Some(Modal::Options {
                    engine: e,
                    script,
                    fields,
                    ..
                }),
                Op::ScriptOptions { script: asked },
                result,
            ) if *e == engine && script == asked => {
                *fields = Some(result.and_then(|r| {
                    match r {
                        Reply::Options(options) => Ok(options
                            .options
                            .into_iter()
                            .map(|option| Field {
                                value: option.value.clone(),
                                option,
                            })
                            .collect()),
                        r => Err(unexpected(r)),
                    }
                }));
            }
            (_, Op::ScriptOptions { .. }, _) => {}
            (
                Some(Modal::Question {
                    engine: e, questions, ..
                }),
                Op::Dialogs,
                result,
            ) if *e == engine => match result {
                Ok(Reply::Dialogs(list)) if list.is_empty() => {
                    self.modal = None;
                    self.toast = Some(Note::new(format!("{engine}: no pending Questions"), Tone::Info));
                }
                result => {
                    *questions = Some(result.and_then(|r| match r {
                        Reply::Dialogs(list) => Ok(list),
                        r => Err(unexpected(r)),
                    }))
                }
            },
            (_, Op::Dialogs, _) => {}
            (_, Op::ChatSend { .. }, result) if self.chat.engine() == Some(engine.as_str()) => {
                self.chat.note = Some(match result {
                    Ok(reply) => App::done(reply),
                    Err(e) => Note::new(format!("not sent: {e}"), Tone::Failed),
                });
            }
            (_, op, result) => {
                let note = match result {
                    Ok(reply) => App::done(reply),
                    Err(e) => Note::new(failed(op, &e), Tone::Failed),
                };
                self.toast = Some(Note::new(format!("{engine}: {}", first_line(&note.text)), note.tone));
                self.activity.insert(engine, note);
            }
        }
    }

    fn done(reply: Reply) -> Note {
        let ok = |text: String| Note::new(text, Tone::Done);
        match reply {
            Reply::Started(hello) => ok(format!("Engine started (build {}, pid {})", hello.build, hello.pid)),
            Reply::Stopping => ok("Engine stopping".into()),
            Reply::LoggedIn(login) if login.already_logged_in => {
                ok(format!("already playing as {} on {}", login.username, login.server))
            }
            Reply::LoggedIn(login) => ok(format!("logged in as {} on {}", login.username, login.server)),
            Reply::LoggedOut(logout) if logout.was_logged_in => ok("logged out".into()),
            Reply::LoggedOut(_) => ok("wasn't logged in".into()),
            Reply::ScriptStarted(start) => ok(format!("Script started, run {}", start.run)),
            Reply::ScriptStopped(stop) if !stop.was_running => ok("no Script was running".into()),
            Reply::ScriptStopped(stop) if !stop.ended => {
                Note::new("the Script didn't stop in time (stopTimedOut)", Tone::Failed)
            }
            Reply::ScriptStopped(_) => ok("Script stopped".into()),
            Reply::Answered(answer) => ok(format!("answered Question {}: {}", answer.id, answer.choice)),
            Reply::Joined(at) => ok(format!(
                "{} {} · {} · {}",
                if at.already_there { "already in" } else { "joined" },
                at.map,
                at.cell,
                at.pad
            )),
            Reply::ChatSent(sent) => ok(match sent.to {
                Some(to) => format!("whispered {to}"),
                None => format!("sent to {} chat", sent.channel),
            }),
            Reply::Updated(update) => Note::new(
                format!(
                    "Scripts updated ({}): {} downloaded, {} new, {} changed{}",
                    update.mode,
                    update.downloaded,
                    update.added.len(),
                    update.changed.len(),
                    if update.failed.is_empty() {
                        String::new()
                    } else {
                        format!(", {} failed: {}", update.failed.len(), update.failed.join(", "))
                    }
                ),
                if update.failed.is_empty() {
                    Tone::Done
                } else {
                    Tone::Failed
                },
            ),
            r => Note::new(unexpected(r).to_string(), Tone::Failed),
        }
    }

    fn on_modal_key(&mut self, modal: Modal, key: KeyEvent) {
        if key.code == KeyCode::Esc {
            return;
        }
        match modal {
            Modal::Help => {}
            Modal::Confirm { text, jobs } => match key.code {
                KeyCode::Char('y') | KeyCode::Enter => {
                    for job in jobs {
                        self.queue(job.engine, job.op, "stopping its Engine…");
                    }
                }
                KeyCode::Char('n') => {}
                _ => self.modal = Some(Modal::Confirm { text, jobs }),
            },
            Modal::Servers {
                engine,
                servers,
                mut selected,
            } => {
                let count = servers.as_ref().and_then(|s| s.as_ref().ok()).map_or(0, Vec::len);
                match key.code {
                    KeyCode::Enter => {
                        if let Some(Ok(list)) = &servers
                            && let Some(server) = list.get(selected)
                        {
                            let server = server.name.clone();
                            self.act(
                                Op::Login { server: server.clone() },
                                &format!("logging in to {server}…"),
                            );
                            return;
                        }
                    }
                    KeyCode::Down | KeyCode::Char('j') => selected = step(selected, 1, count),
                    KeyCode::Up | KeyCode::Char('k') => selected = step(selected, -1, count),
                    _ => {}
                }
                self.modal = Some(Modal::Servers {
                    engine,
                    servers,
                    selected,
                });
            }
            Modal::Scripts {
                engine,
                mut query,
                mut found,
                mut selected,
            } => {
                let count = found
                    .as_ref()
                    .and_then(|f| f.as_ref().ok())
                    .map_or(0, |f| f.scripts.len());
                match key.code {
                    KeyCode::Enter => {
                        if let Some(Ok(found)) = &found
                            && let Some(script) = found.scripts.get(selected)
                        {
                            let script = script.path.clone();
                            self.jobs.push(Job {
                                engine: engine.clone(),
                                op: Op::ScriptOptions { script: script.clone() },
                            });
                            self.modal = Some(Modal::Options {
                                engine,
                                script,
                                fields: None,
                                selected: 0,
                            });
                            return;
                        }
                    }
                    KeyCode::Down => selected = step(selected, 1, count),
                    KeyCode::Up => selected = step(selected, -1, count),
                    KeyCode::Backspace | KeyCode::Char(_) => {
                        match key.code {
                            KeyCode::Char(c) => query.push(c),
                            _ => _ = query.pop(),
                        }
                        found = None;
                        selected = 0;
                        self.search(&engine, query.clone());
                    }
                    _ => {}
                }
                self.modal = Some(Modal::Scripts {
                    engine,
                    query,
                    found,
                    selected,
                });
            }
            Modal::Options {
                engine,
                script,
                mut fields,
                mut selected,
            } => {
                if let Some(Ok(fields)) = &mut fields {
                    let count = fields.len();
                    match key.code {
                        KeyCode::Enter => {
                            let options: BTreeMap<String, String> = fields
                                .iter()
                                .filter(|f| f.editable() && f.value != f.option.value)
                                .map(|f| (f.option.key.clone(), f.value.clone()))
                                .collect();
                            self.act(
                                Op::ScriptStart {
                                    script: script.clone(),
                                    options,
                                },
                                &format!("starting {script}…"),
                            );
                            return;
                        }
                        KeyCode::Down => selected = step(selected, 1, count),
                        KeyCode::Up => selected = step(selected, -1, count),
                        code => {
                            if let Some(field) = fields.get_mut(selected).filter(|f| f.editable()) {
                                match code {
                                    KeyCode::Left if !field.is_text() => field.cycle(-1),
                                    KeyCode::Right if !field.is_text() => field.cycle(1),
                                    KeyCode::Char(' ') if !field.is_text() => field.cycle(1),
                                    KeyCode::Char(c) if field.is_text() => field.value.push(c),
                                    KeyCode::Backspace if field.is_text() => _ = field.value.pop(),
                                    _ => {}
                                }
                            }
                        }
                    }
                }
                self.modal = Some(Modal::Options {
                    engine,
                    script,
                    fields,
                    selected,
                });
            }
            Modal::Join { mut query } => match key.code {
                KeyCode::Enter => {
                    let mut words = query.split_whitespace().map(str::to_owned);
                    if let Some(map) = words.next() {
                        let (cell, pad) = (words.next(), words.next());
                        self.act(
                            Op::Join {
                                map: map.clone(),
                                cell,
                                pad,
                            },
                            &format!("joining {map}…"),
                        );
                    }
                }
                KeyCode::Backspace => {
                    query.pop();
                    self.modal = Some(Modal::Join { query });
                }
                KeyCode::Char(c) => {
                    query.push(c);
                    self.modal = Some(Modal::Join { query });
                }
                _ => self.modal = Some(Modal::Join { query }),
            },
            Modal::Question {
                engine,
                questions,
                mut selected,
            } => {
                let question = questions.as_ref().and_then(|q| q.as_ref().ok()).and_then(|q| q.first());
                let choices = question.map_or(0, |q| q.choices.len());
                let choice = match key.code {
                    KeyCode::Char(c @ '1'..='9') => question.and_then(|q| q.choices.get(c as usize - '1' as usize)),
                    KeyCode::Enter => question.and_then(|q| q.choices.get(selected)),
                    _ => None,
                };
                if let (Some(question), Some(choice)) = (question, choice) {
                    let op = Op::DialogAnswer {
                        id: question.id,
                        choice: choice.clone(),
                    };
                    self.queue(engine, op, &format!("answering {choice}…"));
                    return;
                }
                match key.code {
                    KeyCode::Down | KeyCode::Char('j') => selected = step(selected, 1, choices),
                    KeyCode::Up | KeyCode::Char('k') => selected = step(selected, -1, choices),
                    _ => {}
                }
                self.modal = Some(Modal::Question {
                    engine,
                    questions,
                    selected,
                });
            }
            Modal::Filter { mut query } => match key.code {
                KeyCode::Enter => {
                    self.filter = query;
                    self.selected = 0;
                }
                KeyCode::Backspace => {
                    query.pop();
                    self.modal = Some(Modal::Filter { query });
                }
                KeyCode::Char(c) => {
                    query.push(c);
                    self.modal = Some(Modal::Filter { query });
                }
                _ => self.modal = Some(Modal::Filter { query }),
            },
            Modal::Palette {
                mut query,
                mut selected,
            } => {
                let count = App::palette_matches(&query).len();
                match key.code {
                    KeyCode::Enter => {
                        if let Some(command) = App::palette_matches(&query).get(selected) {
                            self.press(command.key);
                        }
                        return;
                    }
                    KeyCode::Down => selected = step(selected, 1, count),
                    KeyCode::Up => selected = step(selected, -1, count),
                    KeyCode::Backspace => {
                        query.pop();
                        selected = 0;
                    }
                    KeyCode::Char(c) => {
                        query.push(c);
                        selected = 0;
                    }
                    _ => {}
                }
                self.modal = Some(Modal::Palette { query, selected });
            }
        }
    }

    fn move_selection(&mut self, by: isize) {
        self.selected = self.selected.saturating_add_signed(by);
        self.clamp_selection();
    }

    fn clamp_selection(&mut self) {
        self.selected = self.selected.min(self.rows().len().saturating_sub(1));
    }

    fn mark_group(&mut self) {
        let Some(row) = self.selected_row() else { return };
        let group: Vec<String> = self
            .rows()
            .into_iter()
            .filter(|r| r.group == row.group)
            .map(|r| r.name)
            .collect();
        if group.iter().all(|name| self.marks.contains(name)) {
            group.iter().for_each(|name| _ = self.marks.remove(name));
        } else {
            self.marks.extend(group);
        }
    }
}

/// Why an action leaves an account's Engine alone.
fn left_alone(view: Option<&EngineView>) -> String {
    match view {
        None | Some(EngineView::Offline) => "no Engine runs; E starts one".into(),
        Some(EngineView::Failed(e)) => format!("left alone: {e}"),
        Some(EngineView::Up { .. }) => unreachable!("an Engine that is up is acted on"),
    }
}

/// What a chat line sends: `/w <name> <text>` whispers, and other text is zone chat. Nothing else starting with `/` is sent, so a
/// mistyped command never goes out as zone chat.
fn chat_op(input: &str) -> Result<Op, String> {
    if !input.starts_with('/') {
        return Ok(Op::ChatSend {
            text: input.to_owned(),
            to: None,
        });
    }
    match input
        .strip_prefix("/w ")
        .and_then(|rest| rest.trim_start().split_once(' '))
    {
        Some((name, text)) if !text.trim().is_empty() => Ok(Op::ChatSend {
            text: text.trim_start().to_owned(),
            to: Some(name.to_owned()),
        }),
        _ => Err("/w <name> <text> whispers; nothing else starting with / is sent".into()),
    }
}

/// An action's error, with what to do for the usual refusals.
fn failed(op: &Op, error: &Error) -> String {
    match (op, error) {
        (
            Op::StopEngine,
            Error::Remote {
                code: SCRIPT_RUNNING,
                message,
            },
        ) => {
            format!("{message} The Engine keeps running; x stops the Script first.")
        }
        _ => error.to_string(),
    }
}

fn unexpected(reply: Reply) -> Error {
    Error::Malformed(format!("an answer to another op: {reply:?}"))
}

pub fn first_line(text: &str) -> &str {
    text.lines().next().unwrap_or("")
}

/// A picker's selection moved `by`, within its `count` rows.
fn step(selected: usize, by: isize, count: usize) -> usize {
    selected.saturating_add_signed(by).min(count.saturating_sub(1))
}

/// Whether `query`'s characters appear in `text` in order, ignoring case.
fn fuzzy(text: &str, query: &str) -> bool {
    let mut chars = text.chars().flat_map(char::to_lowercase);
    query
        .chars()
        .flat_map(char::to_lowercase)
        .all(|q| chars.any(|c| c == q))
}
