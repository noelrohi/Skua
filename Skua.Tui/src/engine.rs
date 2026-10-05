//! A connection to one Engine, after `hello`, with typed calls for what skua-tui shows and does.

use std::collections::{BTreeMap, VecDeque};
use std::fmt;
use std::io;
use std::os::unix::net::UnixStream;
use std::path::Path;
use std::time::Duration;

use serde::Deserialize;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};

use crate::dto::{
    ChatSendResult, DialogAnswer, Dialogs, EngineHost, Hello, Inventory, Location, LogPage, LoginResult, LogoutResult,
    Map, Quests, Screenshot, ScriptOptions, ScriptStartResult, ScriptStopResult, ScriptsSearch, ScriptsUpdate, Servers,
    Status,
};
use crate::rpc::{CallError, Rpc};

/// The Control Surface protocol this build speaks (`ControlProtocol.Version`).
pub const PROTOCOL: i64 = 18;

/// `ErrorCode.NotLoggedIn` on the wire: `ErrorCodes.ToWire` adds 1000.
pub const NOT_LOGGED_IN: i64 = 1001;
/// `ErrorCode.ScriptRunning` on the wire.
pub const SCRIPT_RUNNING: i64 = 1002;

#[derive(Debug, Clone, PartialEq)]
pub enum Error {
    /// Nothing answers on the socket: it is gone, or stale.
    Offline,
    /// The Engine speaks another protocol, so nothing it says is read.
    ProtocolMismatch {
        engine: String,
        build: String,
        protocol: i64,
        host: Option<EngineHost>,
    },
    /// The Engine refused the call, with an `ErrorCode` on the wire.
    Remote { code: i64, message: String },
    /// The connection broke or timed out.
    Unavailable(String),
    /// The Engine answered with something this build can't read.
    Malformed(String),
    /// skua-tui refused to do what was asked, or couldn't start an Engine, and why.
    Refused(String),
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
        match self {
            Error::Offline => write!(f, "no Engine answers"),
            Error::ProtocolMismatch {
                engine,
                build,
                protocol,
                host,
            } => {
                write!(
                    f,
                    "Protocol mismatch: Engine '{engine}' (build {build}) speaks protocol {protocol}, but this skua-tui speaks {PROTOCOL}. \
                     Nothing it reports is shown. "
                )?;
                match host {
                    Some(EngineHost::App) => write!(f, "Build skua-tui from the Skua app's checkout, or quit the app."),
                    _ => write!(
                        f,
                        "Build skua-tui from the Engine's checkout, or stop the Engine (skua engine stop --engine {engine})."
                    ),
                }
            }
            Error::Remote { message, .. } => write!(f, "{message}"),
            Error::Unavailable(message) => write!(f, "the Engine stopped answering: {message}"),
            Error::Malformed(message) => write!(f, "the Engine sent something this skua-tui can't read: {message}"),
            Error::Refused(message) => write!(f, "{message}"),
        }
    }
}

impl From<CallError> for Error {
    fn from(e: CallError) -> Error {
        match e {
            CallError::Io(e) => Error::Unavailable(e.to_string()),
            CallError::Remote { code, message } => Error::Remote { code, message },
            CallError::Malformed(message) => Error::Malformed(message),
        }
    }
}

pub struct Engine {
    rpc: Rpc,
    pub hello: Hello,
}

impl Engine {
    /// Connects and says `hello`; an Engine of another protocol is an error, never a connection.
    pub fn connect(socket: &Path, timeout: Duration) -> Result<Engine, Error> {
        let mut rpc = Rpc::connect(socket, timeout).map_err(|e| match e.kind() {
            io::ErrorKind::NotFound | io::ErrorKind::ConnectionRefused => Error::Offline,
            _ => Error::Unavailable(e.to_string()),
        })?;
        let hello: Hello = decode(rpc.call("hello", json!([PROTOCOL]))?)?;
        if hello.protocol != PROTOCOL {
            return Err(Error::ProtocolMismatch {
                engine: hello.engine_name,
                build: hello.build,
                protocol: hello.protocol,
                host: hello.host,
            });
        }
        Ok(Engine { rpc, hello })
    }

    pub fn status(&mut self) -> Result<Status, Error> {
        self.call("status", json!([]))
    }

    /// Every kind of entry: with `tail`, the newest `tail` after the cursor; without, the page after it.
    pub fn logs(&mut self, after: Option<&str>, tail: Option<u32>) -> Result<LogPage, Error> {
        self.logs_of("all", after, tail)
    }

    /// The entries of one `kind`, as [`Engine::logs`] reads them.
    pub fn logs_of(&mut self, kind: &str, after: Option<&str>, tail: Option<u32>) -> Result<LogPage, Error> {
        self.call("logs", json!([kind, after, null, tail]))
    }

    /// Follows the entries of `kinds` after the cursor on this connection, which then only waits for what the Engine pushes.
    pub fn subscribe(mut self, kinds: &[&str], after: Option<&str>) -> Result<Subscription, Error> {
        let reply = self.rpc.call("subscribe", json!([kinds, after]))?;
        self.rpc.wait_forever().map_err(|e| Error::Unavailable(e.to_string()))?;
        let mut subscription = Subscription {
            rpc: self.rpc,
            token: None,
            pages: VecDeque::new(),
        };
        subscription.take(reply)?;
        Ok(subscription)
    }

    /// A handle that closes this connection from another thread.
    pub fn closer(&self) -> Result<UnixStream, Error> {
        self.rpc.closer().map_err(|e| Error::Unavailable(e.to_string()))
    }

    /// The newest `tail` events, as `logs events --tail`.
    pub fn events(&mut self, tail: u32) -> Result<LogPage, Error> {
        self.call("logs", json!(["events", null, null, tail]))
    }

    pub fn inventory(&mut self) -> Result<Inventory, Error> {
        self.call("inventory", json!(["inventory"]))
    }

    pub fn quests(&mut self) -> Result<Quests, Error> {
        self.call("quests", json!(["loaded"]))
    }

    /// The game's picture, scaled down to `max_width` when wider.
    pub fn screenshot(&mut self, max_width: u32) -> Result<Screenshot, Error> {
        self.call("screenshot", json!([max_width]))
    }

    pub fn map(&mut self) -> Result<Map, Error> {
        self.call("map", json!([]))
    }

    /// How long each later call may wait: the Engine's own waits (a login's 120 s) need longer than a read.
    pub fn set_timeout(&mut self, timeout: Duration) -> Result<(), Error> {
        self.rpc
            .set_timeout(timeout)
            .map_err(|e| Error::Unavailable(e.to_string()))
    }

    /// Shuts the Engine down, unless a Script runs (`SCRIPT_RUNNING`) or a command holds it (`BUSY`).
    pub fn shutdown_if_idle(&mut self) -> Result<(), Error> {
        self.rpc.call("shutdown_if_idle", json!([]))?;
        Ok(())
    }

    pub fn servers(&mut self) -> Result<Servers, Error> {
        self.call("servers", json!([]))
    }

    /// Logs the named account in, or else the Engine's Active Account, as a developer (not an agent); without a server, the Engine picks one.
    pub fn login(&mut self, server: Option<&str>, account: Option<&str>) -> Result<LoginResult, Error> {
        self.call("login", json!([server, null, false, account]))
    }

    pub fn logout(&mut self) -> Result<LogoutResult, Error> {
        self.call("logout", json!([]))
    }

    pub fn scripts_search(&mut self, query: &str) -> Result<ScriptsSearch, Error> {
        self.call("scripts_search", json!([query, null]))
    }

    pub fn script_options(&mut self, script: &str) -> Result<ScriptOptions, Error> {
        self.call("script_options", json!([script]))
    }

    /// Starts `script` with `options` stored first; its Questions wait for an answer (the Engine's default).
    pub fn script_start(
        &mut self,
        script: &str,
        options: &BTreeMap<String, String>,
    ) -> Result<ScriptStartResult, Error> {
        self.call("script_start", json!([script, options, null, null]))
    }

    pub fn script_stop(&mut self) -> Result<ScriptStopResult, Error> {
        self.call("script_stop", json!([]))
    }

    pub fn dialogs(&mut self) -> Result<Dialogs, Error> {
        self.call("dialogs", json!([]))
    }

    pub fn dialog_answer(&mut self, id: i64, choice: &str) -> Result<DialogAnswer, Error> {
        self.call("dialog_answer", json!([id, choice]))
    }

    pub fn join(&mut self, map: &str, cell: Option<&str>, pad: Option<&str>) -> Result<Location, Error> {
        self.call("join", json!([map, cell, pad, null]))
    }

    /// Sends zone chat, or with `to` a whisper, as the player.
    pub fn chat_send(&mut self, text: &str, to: Option<&str>) -> Result<ChatSendResult, Error> {
        self.call("chat_send", json!([text, to]))
    }

    pub fn scripts_update(&mut self) -> Result<ScriptsUpdate, Error> {
        self.call("scripts_update", json!([]))
    }

    fn call<T: DeserializeOwned>(&mut self, method: &str, params: Value) -> Result<T, Error> {
        decode(self.rpc.call(method, params)?)
    }
}

/// The pages a `subscribe` pushes, read as StreamJsonRpc streams an `IAsyncEnumerable`: the call answers with a token, and each
/// `$/enumerator/next` with that token waits for the next values.
pub struct Subscription {
    rpc: Rpc,
    /// None once the Engine has said the stream is finished.
    token: Option<Value>,
    pages: VecDeque<LogPage>,
}

#[derive(Deserialize)]
struct Batch {
    #[serde(default)]
    token: Option<Value>,
    #[serde(default)]
    values: Vec<LogPage>,
    #[serde(default)]
    finished: bool,
}

impl Subscription {
    /// The next page, waiting for the Engine to push one; None when it ended the stream.
    pub fn next_page(&mut self) -> Result<Option<LogPage>, Error> {
        loop {
            if let Some(page) = self.pages.pop_front() {
                return Ok(Some(page));
            }
            let Some(token) = &self.token else {
                return Ok(None);
            };
            let reply = self.rpc.call("$/enumerator/next", json!([token]))?;
            self.take(reply)?;
        }
    }

    /// Queues a batch's pages. The first batch carries the token, unless the stream finished before it was answered.
    fn take(&mut self, reply: Value) -> Result<(), Error> {
        let batch: Batch = decode(reply)?;
        self.pages.extend(batch.values);
        if batch.finished {
            self.token = None;
        } else if batch.token.is_some() {
            self.token = batch.token;
        }
        Ok(())
    }
}

fn decode<T: DeserializeOwned>(value: Value) -> Result<T, Error> {
    serde_json::from_value(value).map_err(|e| Error::Malformed(e.to_string()))
}
