//! JSON-RPC 2.0 over a Unix socket, framed as StreamJsonRpc's `HeaderDelimitedMessageHandler` frames it:
//! `Content-Length: N\r\n\r\n` and then N bytes of UTF-8 JSON.

use std::io::{self, BufRead, BufReader, Write};
use std::os::unix::net::UnixStream;
use std::path::Path;
use std::time::Duration;

use serde_json::{Value, json};

/// A failed call: the transport broke, or the Engine answered with an error.
#[derive(Debug)]
pub enum CallError {
    Io(io::Error),
    Remote { code: i64, message: String },
    Malformed(String),
}

pub fn write_message(writer: &mut impl Write, body: &[u8]) -> io::Result<()> {
    write!(writer, "Content-Length: {}\r\n\r\n", body.len())?;
    writer.write_all(body)?;
    writer.flush()
}

/// Reads one message's body, or `None` at the end of the stream.
pub fn read_message(reader: &mut impl BufRead) -> io::Result<Option<Vec<u8>>> {
    let mut length = None;
    let mut first = true;
    loop {
        let mut line = String::new();
        if reader.read_line(&mut line)? == 0 {
            return if first {
                Ok(None)
            } else {
                Err(io::ErrorKind::UnexpectedEof.into())
            };
        }
        first = false;
        let line = line.trim_end_matches(['\r', '\n']);
        if line.is_empty() {
            break;
        }
        if let Some((name, value)) = line.split_once(':')
            && name.trim().eq_ignore_ascii_case("content-length")
        {
            length = value.trim().parse::<usize>().ok();
        }
    }
    let length =
        length.ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "a message without a Content-Length"))?;
    let mut body = vec![0; length];
    reader.read_exact(&mut body)?;
    Ok(Some(body))
}

/// One connection, making one call at a time.
pub struct Rpc {
    writer: UnixStream,
    reader: BufReader<UnixStream>,
    next_id: u64,
}

impl Rpc {
    pub fn connect(socket: &Path, timeout: Duration) -> io::Result<Rpc> {
        let stream = UnixStream::connect(socket)?;
        stream.set_read_timeout(Some(timeout))?;
        stream.set_write_timeout(Some(timeout))?;
        Ok(Rpc {
            reader: BufReader::new(stream.try_clone()?),
            writer: stream,
            next_id: 0,
        })
    }

    /// Waits for each answer however long it takes, as a subscription waits for what is pushed.
    pub fn wait_forever(&mut self) -> io::Result<()> {
        self.writer.set_read_timeout(None)
    }

    /// A handle that closes this connection from another thread, ending a call that waits.
    pub fn closer(&self) -> io::Result<UnixStream> {
        self.writer.try_clone()
    }

    pub fn set_timeout(&mut self, timeout: Duration) -> io::Result<()> {
        self.writer.set_read_timeout(Some(timeout))?;
        self.writer.set_write_timeout(Some(timeout))
    }

    /// Calls `method` with positional `params`, as the C# client's proxy does, and returns its result.
    pub fn call(&mut self, method: &str, params: Value) -> Result<Value, CallError> {
        self.next_id += 1;
        let id = self.next_id;
        let request = json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params });
        write_message(&mut self.writer, request.to_string().as_bytes()).map_err(CallError::Io)?;
        loop {
            let body = read_message(&mut self.reader)
                .map_err(CallError::Io)?
                .ok_or_else(|| CallError::Io(io::ErrorKind::UnexpectedEof.into()))?;
            let message: Value =
                serde_json::from_slice(&body).map_err(|e| CallError::Malformed(format!("not JSON: {e}")))?;
            // A notification isn't this call's answer.
            if message.get("id").and_then(Value::as_u64) != Some(id) {
                continue;
            }
            if let Some(error) = message.get("error") {
                return Err(CallError::Remote {
                    code: error.get("code").and_then(Value::as_i64).unwrap_or(0),
                    message: error.get("message").and_then(Value::as_str).unwrap_or("").to_owned(),
                });
            }
            return Ok(message.get("result").cloned().unwrap_or(Value::Null));
        }
    }
}
