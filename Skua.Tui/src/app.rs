//! The shell's state and keys: accounts by group, selection, marks, tabs and the command palette. It does no I/O.

use std::collections::{BTreeSet, HashSet};
use std::path::PathBuf;

use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyEventKind, KeyModifiers};

use crate::poller::{EngineView, Focus, Snapshot};

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub enum Tab {
    #[default]
    Overview,
    Inventory,
    Quests,
    Logs,
    Game,
}

impl Tab {
    pub const ALL: [Tab; 5] = [Tab::Overview, Tab::Inventory, Tab::Quests, Tab::Logs, Tab::Game];

    pub fn title(self) -> &'static str {
        match self {
            Tab::Overview => "Overview",
            Tab::Inventory => "Inventory",
            Tab::Quests => "Quests",
            Tab::Logs => "Logs",
            Tab::Game => "Game",
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

/// The keys of actions skua-tui doesn't have yet.
const ACTIONS: &[(char, &str)] = &[
    ('E', "start Engine"),
    ('X', "stop Engine"),
    ('L', "log in"),
    ('O', "log out"),
    ('s', "start Script"),
    ('x', "stop Script"),
    ('J', "join map"),
    ('d', "answer Question"),
    ('U', "update Scripts"),
];

pub const KEYS: &[(&str, &str)] = &[
    ("j/k ↑/↓", "move"),
    ("space", "mark account"),
    ("a", "mark its whole group"),
    ("esc", "clear marks and filter"),
    ("tab ⇧tab", "Overview, Inventory, Quests, Logs, Game"),
    ("1–5", "pick a tab"),
    (":", "command palette"),
    ("/", "filter accounts"),
    ("E / X", "start / stop Engine (not yet)"),
    ("L / O", "log in / log out (not yet)"),
    ("s / x", "start / stop Script (not yet)"),
    ("J d U", "join, answer, update Scripts (not yet)"),
    ("q", "quit"),
];

pub enum Modal {
    Palette { query: String, selected: usize },
    Filter { query: String },
    Help,
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
    pub toast: Option<String>,
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
            None => self.on_main_key(key),
        }
    }

    fn on_main_key(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Char('j') | KeyCode::Down => self.move_selection(1),
            KeyCode::Char('k') | KeyCode::Up => self.move_selection(-1),
            KeyCode::Tab => self.tab = Tab::ALL[(self.tab.index() + 1) % Tab::ALL.len()],
            KeyCode::BackTab => self.tab = Tab::ALL[(self.tab.index() + Tab::ALL.len() - 1) % Tab::ALL.len()],
            KeyCode::Esc => {
                self.marks.clear();
                self.filter.clear();
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
            '1'..='5' => self.tab = Tab::ALL[c as usize - '1' as usize],
            _ => {
                if let Some((_, name)) = ACTIONS.iter().find(|(key, _)| *key == c) {
                    self.toast = Some(format!(
                        "{name} on {}: not in skua-tui yet; it only shows for now",
                        self.targets_label()
                    ));
                }
            }
        }
    }

    fn on_modal_key(&mut self, modal: Modal, key: KeyEvent) {
        match modal {
            Modal::Help => {}
            Modal::Filter { mut query } => match key.code {
                KeyCode::Esc => {}
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
                    KeyCode::Esc => return,
                    KeyCode::Enter => {
                        if let Some(command) = App::palette_matches(&query).get(selected) {
                            self.press(command.key);
                        }
                        return;
                    }
                    KeyCode::Down => selected = (selected + 1).min(count.saturating_sub(1)),
                    KeyCode::Up => selected = selected.saturating_sub(1),
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

/// Whether `query`'s characters appear in `text` in order, ignoring case.
fn fuzzy(text: &str, query: &str) -> bool {
    let mut chars = text.chars().flat_map(char::to_lowercase);
    query
        .chars()
        .flat_map(char::to_lowercase)
        .all(|q| chars.any(|c| c == q))
}
