//! Follows the Chat tab's Engine's game messages with `subscribe`, which the Engine pushes to, on a thread and a connection of its own.
//! [`Follower::sync`] keeps that one follow on what the app shows, and hands the app what arrived.

use std::net::Shutdown;
use std::os::unix::net::UnixStream;
use std::path::PathBuf;
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

use crate::app::App;
use crate::discovery;
use crate::dto::LogEntry;
use crate::engine::{Engine, Error};

/// How many game messages the tab reads first, before following.
pub const CHAT_TAIL: u32 = 200;
/// How many the tab keeps; older ones drop.
pub const MAX_CHAT_ENTRIES: usize = 500;

#[derive(Debug)]
pub enum Update {
    /// Entries in seq order; `gap` when entries before them were missed.
    Page { entries: Vec<LogEntry>, gap: bool },
    /// The follow ended, and why; it isn't retried until the Engine restarts or the tab is opened again.
    Ended(String),
}

#[derive(Debug)]
pub struct ChatUpdate {
    /// Which follow it came from, so a replaced follow's last updates are dropped.
    pub follow: u64,
    pub update: Update,
}

/// What [`Follow`]'s drop closes, once its thread has connected.
#[derive(Default)]
struct Closer {
    stopped: bool,
    stream: Option<UnixStream>,
}

/// One follow of an Engine, by name and pid; dropping it closes its connection, which ends its thread and the Engine's subscription.
struct Follow {
    target: (String, i64),
    closer: Arc<Mutex<Closer>>,
}

impl Drop for Follow {
    fn drop(&mut self) {
        let mut closer = self.closer.lock().unwrap();
        closer.stopped = true;
        if let Some(stream) = &closer.stream {
            _ = stream.shutdown(Shutdown::Both);
        }
    }
}

pub struct Follower {
    skua_dir: PathBuf,
    follow: Option<Follow>,
    started: u64,
    sender: Sender<ChatUpdate>,
    updates: Receiver<ChatUpdate>,
}

impl Follower {
    pub fn new(skua_dir: PathBuf) -> Follower {
        let (sender, updates) = mpsc::channel();
        Follower {
            skua_dir,
            follow: None,
            started: 0,
            sender,
            updates,
        }
    }

    /// Follows the Engine the Chat tab shows, afresh when it restarts, and nothing when the tab is closed; then applies what arrived.
    pub fn sync(&mut self, app: &mut App) {
        let target = app.chat_target();
        if self.follow.as_ref().map(|f| &f.target) != target.as_ref() {
            self.follow = None;
            match target {
                Some(target) => {
                    self.started += 1;
                    app.chat.follow(target.0.clone(), self.started);
                    self.follow = Some(self.start(target));
                }
                None => app.chat.unfollow(),
            }
        }
        for update in self.updates.try_iter() {
            app.on_chat(update);
        }
    }

    fn start(&self, target: (String, i64)) -> Follow {
        let closer = Arc::new(Mutex::new(Closer::default()));
        let socket = discovery::socket_path(&self.skua_dir, &target.0);
        let (id, sender, shared) = (self.started, self.sender.clone(), closer.clone());
        thread::spawn(move || {
            let send = |update| sender.send(ChatUpdate { follow: id, update }).is_ok();
            let result = (|| -> Result<(), Error> {
                let mut engine = Engine::connect(&socket, Duration::from_secs(5))?;
                {
                    let mut closer = shared.lock().unwrap();
                    if closer.stopped {
                        return Ok(());
                    }
                    closer.stream = Some(engine.closer()?);
                }
                let tail = engine.logs_of("game", None, Some(CHAT_TAIL))?;
                // A tail's gap only says that older entries were evicted.
                if !send(Update::Page {
                    entries: tail.entries,
                    gap: false,
                }) {
                    return Ok(());
                }
                let mut pages = engine.subscribe(&["game"], Some(&tail.next))?;
                while let Some(page) = pages.next_page()? {
                    if !send(Update::Page {
                        entries: page.entries,
                        gap: page.gap,
                    }) {
                        return Ok(());
                    }
                }
                Err(Error::Refused("the Engine ended it".into()))
            })();
            if let Err(e) = result
                && !shared.lock().unwrap().stopped
            {
                send(Update::Ended(e.to_string()));
            }
        });
        Follow { target, closer }
    }
}
