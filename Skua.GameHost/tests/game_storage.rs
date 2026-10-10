//! The game's local storage through the real host (#250, ADR 0008): skua-gamehost runs tests/fixtures/Storage.swf, which reads the game's
//! `AQLite_Data` SharedObject, counts the run in it and flushes it, and traces what it read as 'F' frames on stdout. Needs a Metal device.

use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::mpsc;
use std::time::Duration;

struct TempDir(PathBuf);

impl TempDir {
    fn new(tag: &str) -> TempDir {
        let dir = std::env::temp_dir().join(format!("skua-gamehost-test-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        TempDir(dir)
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// Kills the host if the test gives up on it.
struct Host(Child);

impl Drop for Host {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

/// Runs Storage.swf once, with `--storage=<folder>` if there is one, and returns its traces once it has flushed.
fn run(storage: Option<&Path>) -> Vec<String> {
    let swf = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Storage.swf");
    let mut command = Command::new(env!("CARGO_BIN_EXE_skua-gamehost"));
    if let Some(folder) = storage {
        command.arg(format!("--storage={}", folder.display()));
    }
    // stdin stays open while the host runs: it exits when the Engine's end closes.
    let mut host = Host(
        command
            .arg(swf)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .expect("skua-gamehost starts"),
    );
    let mut stdout = host.0.stdout.take().unwrap();

    // The Bridge's frames: a u32 LE length, then the kind and the payload it counts.
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let mut header = [0u8; 4];
        while stdout.read_exact(&mut header).is_ok() {
            let mut frame = vec![0u8; u32::from_le_bytes(header) as usize];
            if stdout.read_exact(&mut frame).is_err() {
                break;
            }
            if let [b'F', text @ ..] = &frame[..]
                && tx.send(String::from_utf8_lossy(text).into_owned()).is_err()
            {
                break;
            }
        }
    });

    let mut traces = Vec::new();
    while !traces.last().is_some_and(|t: &String| t.starts_with("flush ")) {
        traces.push(
            rx.recv_timeout(Duration::from_secs(60))
                .unwrap_or_else(|_| panic!("Storage.swf traced only {traces:?}")),
        );
    }
    traces
}

#[test]
fn a_shared_object_the_game_flushes_in_one_run_is_read_back_in_the_next_per_engine_name() {
    let skua_dir = TempDir::new("storage");
    let farm = skua_dir.0.join("engines/game-storage/farm");
    let alt1 = skua_dir.0.join("engines/game-storage/alt1");

    let first = run(Some(&farm));
    let next = run(Some(&farm));
    let other_name = run(Some(&alt1));

    assert_eq!(first, ["read 0", "flush flushed"]);
    assert_eq!(next, ["read 1", "flush flushed"]);
    assert_eq!(other_name, ["read 0", "flush flushed"]);
    // Ruffle keys a SharedObject by the root movie's host, "localhost" for skua.swf's file, then the local path and the name
    // (core/src/avm2/globals/flash/net/shared_object.rs, get_local): "localhost//AQLite_Data", a .sol file in the folder.
    assert!(farm.join("localhost/AQLite_Data.sol").is_file());
}

#[test]
fn without_a_storage_folder_the_next_run_reads_nothing_back() {
    assert_eq!(run(None), ["read 0", "flush flushed"]);
    assert_eq!(run(None), ["read 0", "flush flushed"]);
}
