//! Reads the accounts and every Engine's status, and follows the selected Engine's logs and its tab's data. The UI never blocks on an Engine:
//! [`spawn`] runs the [`Poller`] on its own thread.

use std::collections::{BTreeMap, HashMap};
use std::path::PathBuf;
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::thread;
use std::time::{Duration, Instant};

use crate::app::Tab;
use crate::discovery::{self, ManagerAccounts};
use crate::dto::{Hello, Inventory, LogEntry, Map, Quests, Screenshot, Status};
use crate::engine::{Engine, Error};

/// How many entries `logs --tail` shows first.
pub const LOG_TAIL: u32 = 200;
const MAX_LOG_LINES: usize = 2000;

/// The event that records a Hook's run (`EventTypes.HookRan`).
pub const HOOK_RAN: &str = "hook.ran";
/// How many of the latest Hook runs the Hooks tab keeps.
const MAX_HOOK_RUNS: usize = 200;
/// How often the Game tab's picture is read, and how wide: the game's own width, which a terminal scales down.
const PICTURE_EVERY: Duration = Duration::from_secs(2);
const PICTURE_WIDTH: u32 = 960;
/// How old the last read of the quests may be for a rise since it to be news.
const QUESTS_RECENT: Duration = Duration::from_secs(5);
/// How many of the newest game messages the Overview keeps.
const MAX_CHAT: u32 = 100;

#[derive(Debug, Clone)]
pub enum EngineView {
    Offline,
    Up { hello: Hello, status: Box<Status> },
    Failed(Error),
}

/// What the UI asks for beyond every Engine's status.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Focus {
    pub engine: Option<String>,
    pub tab: Tab,
}

/// The selected Engine's logs and its tab's data. `None` is not read yet.
#[derive(Debug, Clone, Default)]
pub struct Detail {
    pub engine: Option<String>,
    pub logs: Vec<LogEntry>,
    /// Its `hook.ran` events, oldest first: those among its newest events, then each that arrives in its logs.
    pub hook_runs: Vec<LogEntry>,
    /// Its newest `script` log entry, which says what the Script is doing.
    pub script_line: Option<LogEntry>,
    /// Its newest game messages, oldest first, for the Overview's chat.
    pub chat: Vec<LogEntry>,
    pub inventory: Option<Result<Inventory, Error>>,
    pub quests: Option<Result<Quests, Error>>,
    /// The newest rise in a quest requirement between two reads, as the game says it (`Dragon's Will: Unyielding Slime 294/300`), and
    /// when it was seen.
    pub progress: Option<(String, Instant)>,
    pub map: Option<Result<Map, Error>>,
    /// The game's picture, read every 2 s while the Game tab shows.
    pub picture: Option<Result<Screenshot, Error>>,
}

#[derive(Debug, Clone)]
pub struct Snapshot {
    pub accounts: Result<ManagerAccounts, String>,
    /// Whether a Hook Runner (`skua hooks`) runs for this data folder.
    pub hook_runner: bool,
    pub engines: BTreeMap<String, EngineView>,
    pub detail: Detail,
}

pub struct Poller {
    skua_dir: PathBuf,
    timeout: Duration,
    connections: HashMap<String, Engine>,
    detail: Detail,
    cursor: Option<String>,
    /// When the picture was last read, so the Engine lifts its lag killer for one only every 2 s.
    pictured: Option<Instant>,
    /// When the quests were last read, so a rise is only told against a read just before it.
    quests_read: Option<Instant>,
}

impl Poller {
    pub fn new(skua_dir: PathBuf, timeout: Duration) -> Poller {
        Poller {
            skua_dir,
            timeout,
            connections: HashMap::new(),
            detail: Detail::default(),
            cursor: None,
            pictured: None,
            quests_read: None,
        }
    }

    pub fn poll(&mut self, focus: &Focus) -> Snapshot {
        let accounts = discovery::load_accounts(&self.skua_dir);
        let names = discovery::engine_names(&self.skua_dir);
        self.connections.retain(|name, _| names.contains(name));
        let engines = names.iter().map(|name| (name.clone(), self.view(name))).collect();

        if self.detail.engine != focus.engine {
            self.detail = Detail {
                engine: focus.engine.clone(),
                ..Detail::default()
            };
            self.cursor = None;
            self.pictured = None;
            self.quests_read = None;
        }
        if let Some(name) = &focus.engine {
            self.read_detail(&name.clone(), focus.tab);
        }
        Snapshot {
            accounts,
            hook_runner: discovery::hook_runner_running(&self.skua_dir),
            engines,
            detail: self.detail.clone(),
        }
    }

    fn view(&mut self, name: &str) -> EngineView {
        if !self.connections.contains_key(name) {
            match Engine::connect(&discovery::socket_path(&self.skua_dir, name), self.timeout) {
                Ok(engine) => _ = self.connections.insert(name.to_owned(), engine),
                Err(Error::Offline) => return EngineView::Offline,
                Err(e) => return EngineView::Failed(e),
            }
        }
        let engine = self.connections.get_mut(name).expect("connected above");
        match engine.status() {
            Ok(status) => EngineView::Up {
                hello: engine.hello.clone(),
                status: Box::new(status),
            },
            Err(e) => {
                self.connections.remove(name);
                EngineView::Failed(e)
            }
        }
    }

    fn read_detail(&mut self, name: &str, tab: Tab) {
        let Some(engine) = self.connections.get_mut(name) else {
            return;
        };
        let page = match &self.cursor {
            None => engine.events(LOG_TAIL).and_then(|events| {
                self.detail.hook_runs = events.entries.into_iter().filter(is_hook_run).collect();
                self.detail.script_line = engine
                    .logs_of("script", None, Some(1))?
                    .entries
                    .into_iter()
                    .rfind(|e| e.kind == "script");
                self.detail.chat = engine.logs_of("game", None, Some(MAX_CHAT))?.entries;
                engine.logs(None, Some(LOG_TAIL))
            }),
            Some(cursor) => engine.logs(Some(cursor), None),
        };
        match page {
            Ok(page) => {
                if page.gap && self.cursor.is_some() {
                    self.detail.logs.push(gap_entry());
                }
                let newest = self.detail.hook_runs.last().map_or(0, |run| run.seq);
                let arrived = page
                    .entries
                    .iter()
                    .filter(|e| is_hook_run(e) && (e.seq > newest || page.gap));
                self.detail.hook_runs.extend(arrived.cloned());
                let excess = self.detail.hook_runs.len().saturating_sub(MAX_HOOK_RUNS);
                self.detail.hook_runs.drain(..excess);
                if let Some(line) = page.entries.iter().rfind(|e| e.kind == "script") {
                    self.detail.script_line = Some(line.clone());
                }
                self.detail
                    .chat
                    .extend(page.entries.iter().filter(|e| e.kind == "game").cloned());
                let excess = self.detail.chat.len().saturating_sub(MAX_CHAT as usize);
                self.detail.chat.drain(..excess);
                self.detail.logs.extend(page.entries);
                let excess = self.detail.logs.len().saturating_sub(MAX_LOG_LINES);
                self.detail.logs.drain(..excess);
                self.cursor = Some(page.next);
            }
            // The cursor stays: an Engine that restarted since answers it with a gap.
            Err(_) => {
                self.connections.remove(name);
                return;
            }
        }
        match tab {
            Tab::Inventory => self.detail.inventory = Some(engine.inventory()),
            Tab::Quests => self.read_quests(name),
            // The Overview shows the quests in progress, and the players and monsters in the player's cell.
            Tab::Overview => {
                self.detail.map = Some(engine.map());
                self.read_quests(name);
            }
            Tab::Game => {
                self.detail.map = Some(engine.map());
                if self.pictured.is_none_or(|at| at.elapsed() >= PICTURE_EVERY) {
                    self.pictured = Some(Instant::now());
                    self.detail.picture = Some(engine.screenshot(PICTURE_WIDTH));
                }
            }
            Tab::Logs | Tab::Chat | Tab::Hooks => {}
        }
    }

    /// Reads the quests, and notes the newest requirement that rose since a read just before; one from before another tab showed would
    /// say an old rise is new.
    fn read_quests(&mut self, name: &str) {
        let Some(engine) = self.connections.get_mut(name) else {
            return;
        };
        let quests = engine.quests();
        let recent = self.quests_read.is_some_and(|at| at.elapsed() < QUESTS_RECENT);
        self.quests_read = Some(Instant::now());
        if let (true, Some(Ok(before)), Ok(now)) = (recent, &self.detail.quests, &quests)
            && let Some(text) = progress(before, now)
        {
            self.detail.progress = Some((text, Instant::now()));
        }
        self.detail.quests = Some(quests);
    }
}

/// The last requirement whose count in the inventory rose from `before` to `now`, as the game says it; a copy taken out of the bank is
/// no progress.
fn progress(before: &Quests, now: &Quests) -> Option<String> {
    now.quests
        .iter()
        .flat_map(|quest| quest.requirements.iter().map(move |r| (quest, r)))
        .rev()
        .find(|(quest, r)| {
            before
                .quests
                .iter()
                .find(|q| q.id == quest.id)
                .and_then(|q| q.requirements.iter().find(|old| old.name == r.name))
                .is_some_and(|old| r.have > old.have && r.owned() > old.owned())
        })
        .map(|(quest, r)| format!("{}: {} {}/{}", quest.name, r.name, r.have, r.qty))
}

fn is_hook_run(entry: &LogEntry) -> bool {
    entry.event_type.as_deref() == Some(HOOK_RAN)
}

/// Marks where entries were missed: evicted from the Engine's buffer, or lost to a restart.
pub fn gap_entry() -> LogEntry {
    LogEntry {
        seq: 0,
        ts: 0,
        kind: "gap".into(),
        run: None,
        text: Some("… entries missed here: evicted, or the Engine restarted".into()),
        event_type: None,
        data: None,
    }
}

/// Polls on its own thread every `interval`, and at once when the focus changes.
pub fn spawn(skua_dir: PathBuf, interval: Duration) -> (Sender<Focus>, Receiver<Snapshot>) {
    let (focus_tx, focus_rx) = mpsc::channel::<Focus>();
    let (snapshot_tx, snapshot_rx) = mpsc::channel();
    thread::spawn(move || {
        let mut poller = Poller::new(skua_dir, Duration::from_secs(5));
        let mut focus = Focus::default();
        loop {
            if snapshot_tx.send(poller.poll(&focus)).is_err() {
                return;
            }
            match focus_rx.recv_timeout(interval) {
                Ok(next) => focus = focus_rx.try_iter().last().unwrap_or(next),
                Err(RecvTimeoutError::Timeout) => {}
                Err(RecvTimeoutError::Disconnected) => return,
            }
        }
    });
    (focus_tx, snapshot_rx)
}
