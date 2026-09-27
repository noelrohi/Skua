//! The Bridge's stdin/stdout ends: a writer thread for outgoing frames, a reader thread for requests,
//! and the lifecycle rules (exit on stdin EOF, abort on any panic).

use crate::frame::{self, Request};
use std::io::{BufReader, BufWriter, Write};
use std::sync::OnceLock;
use std::sync::mpsc::{self, Sender, SyncSender};
use std::time::Duration;

enum Out {
    Frame(Vec<u8>),
    /// Flush, then acknowledge.
    Flush(SyncSender<()>),
}

static OUT: OnceLock<Sender<Out>> = OnceLock::new();

/// Queues an encoded frame for stdout.
pub fn send(frame: Vec<u8>) {
    if let Some(tx) = OUT.get() {
        let _ = tx.send(Out::Frame(frame));
    }
}

/// Sends an 'L' log line.
pub fn log(level: u8, text: &str) {
    send(frame::encode_log(level, text));
}

/// Starts the stdout writer. A failed write means the Engine is gone, so the host exits.
pub fn start_writer() {
    let (tx, rx) = mpsc::channel::<Out>();
    OUT.set(tx).expect("writer already started");
    std::thread::Builder::new()
        .name("stdout-writer".into())
        .spawn(move || {
            let mut out = BufWriter::with_capacity(1 << 16, std::io::stdout().lock());
            let write = |out: &mut BufWriter<_>, item: Out| match item {
                Out::Frame(buf) => out.write_all(&buf).is_ok(),
                Out::Flush(ack) => {
                    let ok = out.flush().is_ok();
                    let _ = ack.send(());
                    ok
                }
            };
            while let Ok(item) = rx.recv() {
                // Coalesce whatever is queued, then flush once.
                let mut ok = write(&mut out, item);
                while ok && let Ok(more) = rx.try_recv() {
                    ok = write(&mut out, more);
                }
                if !ok || out.flush().is_err() {
                    std::process::exit(0);
                }
            }
        })
        .expect("writer thread");
}

/// Starts the stdin reader, which hands each request to `deliver`. On stdin EOF (the Engine closed the
/// pipe or died) the host exits at once; a corrupt stream exits with status 2.
pub fn start_reader(deliver: impl Fn(Request) -> Result<(), ()> + Send + 'static) {
    std::thread::Builder::new()
        .name("stdin-reader".into())
        .spawn(move || {
            let mut inp = BufReader::with_capacity(1 << 16, std::io::stdin().lock());
            loop {
                match frame::read_frame(&mut inp) {
                    Ok(Some((kind, payload))) => match frame::parse_request(kind, &payload) {
                        Ok(req) => {
                            if deliver(req).is_err() {
                                std::process::exit(0);
                            }
                        }
                        Err(e) => tracing::warn!("skipped a Bridge frame: {e}"),
                    },
                    Ok(None) => std::process::exit(0),
                    Err(e) => {
                        eprintln!("skua-gamehost: corrupt Bridge stream: {e}");
                        std::process::exit(2);
                    }
                }
            }
        })
        .expect("reader thread");
}

/// Any panic aborts the process: a panic inside Ruffle leaves the `Player` mid-update (and its mutex
/// poisoned), so there is no sound state to continue from. The panic and its backtrace go to stderr
/// and, as an 'L' frame, to the Engine first.
pub fn abort_on_panic() {
    std::panic::set_hook(Box::new(|info| {
        let bt = std::backtrace::Backtrace::force_capture();
        let text = format!("[panic] {info}\n{bt}");
        eprintln!("{text}");
        log(frame::LOG_ERROR, &text);
        if let Some(tx) = OUT.get() {
            let (ack_tx, ack_rx) = mpsc::sync_channel(1);
            if tx.send(Out::Flush(ack_tx)).is_ok() {
                let _ = ack_rx.recv_timeout(Duration::from_millis(500));
            }
        }
        std::process::abort();
    }));
}
