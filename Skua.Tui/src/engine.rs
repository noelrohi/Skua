//! A connection to one Engine, after `hello`, with typed calls for what the shell shows.

use std::fmt;
use std::io;
use std::path::Path;
use std::time::Duration;

use serde::de::DeserializeOwned;
use serde_json::{Value, json};

use crate::dto::{EngineHost, Hello, Inventory, LogPage, Map, Quests, Status};
use crate::rpc::{CallError, Rpc};

/// The Control Surface protocol this build speaks (`ControlProtocol.Version`).
pub const PROTOCOL: i64 = 13;

/// `ErrorCode.NotLoggedIn` on the wire: `ErrorCodes.ToWire` adds 1000.
pub const NOT_LOGGED_IN: i64 = 1001;

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
        self.call("logs", json!(["all", after, null, tail]))
    }

    pub fn inventory(&mut self) -> Result<Inventory, Error> {
        self.call("inventory", json!(["inventory"]))
    }

    pub fn quests(&mut self) -> Result<Quests, Error> {
        self.call("quests", json!(["loaded"]))
    }

    pub fn map(&mut self) -> Result<Map, Error> {
        self.call("map", json!([]))
    }

    fn call<T: DeserializeOwned>(&mut self, method: &str, params: Value) -> Result<T, Error> {
        decode(self.rpc.call(method, params)?)
    }
}

fn decode<T: DeserializeOwned>(value: Value) -> Result<T, Error> {
    serde_json::from_value(value).map_err(|e| Error::Malformed(e.to_string()))
}
