//! Reads the accounts and every Engine's status, and follows the selected Engine's logs and its tab's data. The UI never blocks on an Engine:
//! [`spawn`] runs the [`Poller`] on its own thread.

use std::collections::{BTreeMap, HashMap};
use std::path::PathBuf;
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::thread;
use std::time::Duration;

use crate::app::Tab;
use crate::discovery::{self, ManagerAccounts};
use crate::dto::{Hello, Inventory, LogEntry, Map, Quests, Status};
use crate::engine::{Engine, Error};

/// How many entries `logs --tail` shows first.
pub const LOG_TAIL: u32 = 200;
const MAX_LOG_LINES: usize = 2000;

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
    pub inventory: Option<Result<Inventory, Error>>,
    pub quests: Option<Result<Quests, Error>>,
    pub map: Option<Result<Map, Error>>,
}

#[derive(Debug, Clone)]
pub struct Snapshot {
    pub accounts: Result<ManagerAccounts, String>,
    pub engines: BTreeMap<String, EngineView>,
    pub detail: Detail,
}

pub struct Poller {
    skua_dir: PathBuf,
    timeout: Duration,
    connections: HashMap<String, Engine>,
    detail: Detail,
    cursor: Option<String>,
}

impl Poller {
    pub fn new(skua_dir: PathBuf, timeout: Duration) -> Poller {
        Poller {
            skua_dir,
            timeout,
            connections: HashMap::new(),
            detail: Detail::default(),
            cursor: None,
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
        }
        if let Some(name) = &focus.engine {
            self.read_detail(&name.clone(), focus.tab);
        }
        Snapshot {
            accounts,
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
            None => engine.logs(None, Some(LOG_TAIL)),
            Some(cursor) => engine.logs(Some(cursor), None),
        };
        match page {
            Ok(page) => {
                if page.gap && self.cursor.is_some() {
                    self.detail.logs.push(gap_entry());
                }
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
            Tab::Quests => self.detail.quests = Some(engine.quests()),
            Tab::Game => self.detail.map = Some(engine.map()),
            Tab::Overview | Tab::Logs => {}
        }
    }
}

/// Marks where entries were missed: evicted from the Engine's buffer, or lost to a restart.
fn gap_entry() -> LogEntry {
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
