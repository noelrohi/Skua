//! What skua-tui does to Engines: each [`Job`] is one Control Surface op on one Engine, over a connection of its own, so a 2-minute login never
//! holds up the poller or another Engine. [`spawn`] runs each job on its own thread; [`Runner::run`] runs one where it is called.

use std::collections::BTreeMap;
use std::os::unix::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::mpsc::Sender;
use std::thread;
use std::time::{Duration, Instant};

use crate::discovery;
use crate::dto::{
    ChatSendResult, DialogAnswer, Hello, Location, LoginResult, LogoutResult, Question, ScriptOptions,
    ScriptStartResult, ScriptStopResult, ScriptsSearch, ScriptsUpdate, Server,
};
use crate::engine::{Engine, Error};

/// Overrides the `skua-engine` that starting an Engine launches, as for `skua`.
pub const ENGINE_EXECUTABLE_VARIABLE: &str = "SKUA_ENGINE";

/// `EngineExitCodes.AlreadyRunning`: another Engine holds the name's lock.
const ALREADY_RUNNING: i32 = 3;

/// macOS's `sun_path` holds 104 bytes, including the terminating NUL.
const MAX_SOCKET_PATH_BYTES: usize = 103;

#[derive(Debug, Clone, PartialEq)]
pub enum Op {
    /// Launches a windowless `skua-engine` under this name, as `skua` auto-starts one; never one that answers or is starting.
    StartEngine,
    /// `shutdown_if_idle`: refused while a Script runs or a command holds the Engine.
    StopEngine,
    Servers,
    Login {
        server: String,
    },
    Logout,
    ScriptsSearch {
        query: String,
    },
    ScriptOptions {
        script: String,
    },
    ScriptStart {
        script: String,
        options: BTreeMap<String, String>,
    },
    ScriptStop,
    Dialogs,
    DialogAnswer {
        id: i64,
        choice: String,
    },
    Join {
        map: String,
        cell: Option<String>,
        pad: Option<String>,
    },
    ScriptsUpdate,
    /// Zone chat, or with `to` a whisper.
    ChatSend {
        text: String,
        to: Option<String>,
    },
    /// Launches `skua hooks` for the data folder, unless a Hook Runner holds its lock; its job's `engine` is empty.
    StartHookRunner,
    /// Takes the game's picture with `skua screenshot` and opens it in Preview.
    OpenPicture,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Job {
    pub engine: String,
    pub op: Op,
}

#[derive(Debug, Clone)]
pub enum Reply {
    Started(Hello),
    Stopping,
    Servers(Vec<Server>),
    LoggedIn(LoginResult),
    LoggedOut(LogoutResult),
    Scripts(ScriptsSearch),
    Options(ScriptOptions),
    ScriptStarted(ScriptStartResult),
    ScriptStopped(ScriptStopResult),
    Dialogs(Vec<Question>),
    Answered(DialogAnswer),
    Joined(Location),
    Updated(ScriptsUpdate),
    ChatSent(ChatSendResult),
    HookRunnerStarted,
    PictureOpened(PathBuf),
}

#[derive(Debug, Clone)]
pub struct Outcome {
    pub job: Job,
    pub result: Result<Reply, Error>,
}

#[derive(Debug, Clone)]
pub struct Runner {
    pub skua_dir: PathBuf,
    /// The `skua-engine` to launch; None finds it as [`engine_executable`] does.
    pub engine_executable: Option<PathBuf>,
    /// How long a starting Engine has to answer, as `skua` waits; and a starting Hook Runner to take its lock.
    pub start_timeout: Duration,
    /// The `skua` that runs the Hook Runner; None is the one on `PATH`.
    pub skua_executable: Option<PathBuf>,
}

impl Runner {
    pub fn new(skua_dir: PathBuf) -> Runner {
        Runner {
            skua_dir,
            engine_executable: None,
            start_timeout: Duration::from_secs(30),
            skua_executable: None,
        }
    }

    pub fn run(&self, job: Job) -> Outcome {
        let result = match &job.op {
            Op::StartEngine => self.start_engine(&job.engine).map(Reply::Started),
            Op::StartHookRunner => self.start_hook_runner().map(|()| Reply::HookRunnerStarted),
            Op::OpenPicture => self.open_picture(&job.engine).map(Reply::PictureOpened),
            op => self.call(&job.engine, op),
        };
        Outcome { job, result }
    }

    fn call(&self, name: &str, op: &Op) -> Result<Reply, Error> {
        let mut engine = Engine::connect(&discovery::socket_path(&self.skua_dir, name), Duration::from_secs(10))?;
        // Longer than each op's own wait in the Engine, so its answer, even a timeout, arrives.
        engine.set_timeout(Duration::from_secs(match op {
            Op::Login { .. } => 150,
            Op::Join { .. } => 90,
            Op::ScriptOptions { .. } | Op::ScriptStart { .. } => 120,
            Op::ScriptsUpdate => 600,
            _ => 30,
        }))?;
        Ok(match op {
            Op::StartEngine | Op::StartHookRunner | Op::OpenPicture => unreachable!("not a call"),
            Op::StopEngine => engine.shutdown_if_idle().map(|()| Reply::Stopping)?,
            Op::Servers => Reply::Servers(engine.servers()?.servers),
            // An Engine's own account is the one named after it, which the Skua Manager also launches its app with.
            Op::Login { server } => Reply::LoggedIn(engine.login(Some(server), Some(name))?),
            Op::Logout => Reply::LoggedOut(engine.logout()?),
            Op::ScriptsSearch { query } => Reply::Scripts(engine.scripts_search(query)?),
            Op::ScriptOptions { script } => Reply::Options(engine.script_options(script)?),
            Op::ScriptStart { script, options } => Reply::ScriptStarted(engine.script_start(script, options)?),
            Op::ScriptStop => Reply::ScriptStopped(engine.script_stop()?),
            Op::Dialogs => Reply::Dialogs(engine.dialogs()?.questions),
            Op::DialogAnswer { id, choice } => Reply::Answered(engine.dialog_answer(*id, choice)?),
            Op::Join { map, cell, pad } => Reply::Joined(engine.join(map, cell.as_deref(), pad.as_deref())?),
            Op::ScriptsUpdate => Reply::Updated(engine.scripts_update()?),
            Op::ChatSend { text, to } => Reply::ChatSent(engine.chat_send(text, to.as_deref())?),
        })
    }

    /// Starts `skua-engine --name <name> --detach` as `EngineClient.ConnectOrStartAsync` does, and waits for it to answer `hello`. An Engine
    /// that already answers, of any protocol, is left alone; so is one starting (its lock held), which is only waited for.
    fn start_engine(&self, name: &str) -> Result<Hello, Error> {
        let socket = discovery::socket_path(&self.skua_dir, name);
        if let Some(hello) = answering(&socket)? {
            return Err(Error::Refused(format!(
                "Engine '{}' already runs (pid {}); nothing was started",
                hello.engine_name, hello.pid
            )));
        }
        if socket.as_os_str().len() > MAX_SOCKET_PATH_BYTES {
            return Err(Error::Refused(format!(
                "the socket path {} is longer than macOS allows",
                socket.display()
            )));
        }
        let engines = self.skua_dir.join("engines");
        let lock = engines.join(format!("{name}.lock"));
        let log = engines.join(format!("{name}.log"));
        let mut started = if discovery::lock_held(&lock) {
            None
        } else {
            let executable = match &self.engine_executable {
                Some(path) => path.clone(),
                None => engine_executable().map_err(Error::Refused)?,
            };
            // As `skua`: the Engine must never hold its starter's stdio, so the shell points it at /dev/null before the Engine starts.
            let child = Command::new("/bin/sh")
                .args(["-c", "exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1"])
                .arg(&executable)
                .args(["--name", name, "--detach"])
                .env(discovery::SKUA_DIR_VARIABLE, &self.skua_dir)
                .env("SKUA_ENGINE_SOCKET", &socket)
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .spawn()
                .map_err(|e| Error::Refused(format!("couldn't start {}: {e}", executable.display())))?;
            Some(child)
        };
        let waited = Instant::now();
        let result = loop {
            thread::sleep(Duration::from_millis(50));
            match Engine::connect(&socket, Duration::from_secs(10)) {
                Ok(engine) => break Ok(engine.hello),
                Err(Error::Offline) => {}
                Err(e) => break Err(e),
            }
            if let Some(status) = started.as_mut().and_then(|c| c.try_wait().ok().flatten())
                && status.code() != Some(ALREADY_RUNNING)
            {
                break Err(Error::Refused(format!(
                    "the Engine exited during start with {status}; see {}",
                    log.display()
                )));
            }
            if waited.elapsed() > self.start_timeout {
                break Err(Error::Refused(format!(
                    "Engine '{name}' is starting or hung: it doesn't answer on {}. See {}",
                    socket.display(),
                    log.display()
                )));
            }
        };
        // The Engine outlives skua-tui; this only reaps it if it exits first.
        if let Some(mut child) = started {
            thread::spawn(move || _ = child.wait());
        }
        result
    }

    /// Launches `skua hooks` in its own process group, so it outlives skua-tui, with its output in `<SkuaDIR>/hooks.log`; and waits for it to
    /// take its lock. skua-tui never runs a Hook itself.
    /// `skua screenshot` of the Engine into a file in the temporary folder, then `open`, which shows a PNG in Preview.
    fn open_picture(&self, name: &str) -> Result<PathBuf, Error> {
        let skua = match &self.skua_executable {
            Some(path) => path.clone(),
            None => skua_on_path().map_err(Error::Refused)?,
        };
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |d| d.as_secs());
        let file = std::env::temp_dir().join(format!("skua-{name}-{stamp}.png"));
        let shot = Command::new(&skua)
            .args(["screenshot", "--engine", name, "-o"])
            .arg(&file)
            .env(discovery::SKUA_DIR_VARIABLE, &self.skua_dir)
            .env_remove("SKUA_ENGINE_SOCKET")
            .stdin(Stdio::null())
            .output()
            .map_err(|e| Error::Refused(format!("couldn't run {}: {e}", skua.display())))?;
        if !shot.status.success() {
            return Err(Error::Refused(first_line_of(&shot.stderr, "skua screenshot failed")));
        }
        Command::new("open")
            .arg(&file)
            .status()
            .map_err(|e| Error::Refused(format!("couldn't open {}: {e}", file.display())))?;
        Ok(file)
    }

    fn start_hook_runner(&self) -> Result<(), Error> {
        let lock = discovery::hook_runner_lock(&self.skua_dir);
        if discovery::lock_held(&lock) {
            return Err(Error::Refused("a Hook Runner already runs; nothing was started".into()));
        }
        let skua = match &self.skua_executable {
            Some(path) => path.clone(),
            None => skua_on_path().map_err(Error::Refused)?,
        };
        let log = self.skua_dir.join("hooks.log");
        let mut child = Command::new("/bin/sh")
            .args(["-c", "exec \"$0\" hooks </dev/null >>\"$1\" 2>&1"])
            .arg(&skua)
            .arg(&log)
            .env(discovery::SKUA_DIR_VARIABLE, &self.skua_dir)
            .env_remove("SKUA_ENGINE_SOCKET")
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .process_group(0)
            .spawn()
            .map_err(|e| Error::Refused(format!("couldn't start {}: {e}", skua.display())))?;
        let waited = Instant::now();
        let result = loop {
            if discovery::lock_held(&lock) {
                break Ok(());
            }
            if let Ok(Some(status)) = child.try_wait() {
                break Err(Error::Refused(format!(
                    "skua hooks exited during start with {status}; see {}",
                    log.display()
                )));
            }
            if waited.elapsed() > self.start_timeout {
                break Err(Error::Refused(format!(
                    "skua hooks didn't take {} in time; see {}",
                    lock.display(),
                    log.display()
                )));
            }
            thread::sleep(Duration::from_millis(50));
        };
        thread::spawn(move || _ = child.wait());
        result
    }
}

/// The `hello` of an Engine answering on `socket`, of any protocol; None when nothing answers.
fn answering(socket: &Path) -> Result<Option<Hello>, Error> {
    match Engine::connect(socket, Duration::from_secs(10)) {
        Ok(engine) => Ok(Some(engine.hello)),
        Err(Error::Offline) => Ok(None),
        Err(e) => Err(e),
    }
}

/// The `skua-engine` that `skua` auto-starts: `SKUA_ENGINE`, else the one next to the `skua` on `PATH` (which `install-macos.sh` links).
pub fn engine_executable() -> Result<PathBuf, String> {
    if let Some(path) = std::env::var_os(ENGINE_EXECUTABLE_VARIABLE).filter(|p| !p.is_empty()) {
        return Ok(std::path::absolute(&path).unwrap_or_else(|_| path.into()));
    }
    let skua = skua_on_path().map_err(|e| {
        format!("{e} to find skua-engine by; set {ENGINE_EXECUTABLE_VARIABLE} to the skua-engine executable")
    })?;
    let engine = std::fs::canonicalize(&skua)
        .map_err(|e| format!("{}: {e}", skua.display()))?
        .with_file_name("skua-engine");
    if engine.is_file() {
        Ok(engine)
    } else {
        Err(format!(
            "{} doesn't exist; set {ENGINE_EXECUTABLE_VARIABLE} to the skua-engine executable",
            engine.display()
        ))
    }
}

/// The `skua` on `PATH`, which `install-macos.sh` links.
fn skua_on_path() -> Result<PathBuf, String> {
    std::env::var_os("PATH")
        .into_iter()
        .flat_map(|paths| std::env::split_paths(&paths).collect::<Vec<_>>())
        .map(|dir| dir.join("skua"))
        .find(|path| path.is_file())
        .ok_or_else(|| "no skua on PATH".to_owned())
}

pub fn spawn(runner: &Runner, job: Job, outcomes: Sender<Outcome>) {
    let runner = runner.clone();
    thread::spawn(move || _ = outcomes.send(runner.run(job)));
}

/// The first line of a process's output, or `fallback` when it said nothing.
fn first_line_of(output: &[u8], fallback: &str) -> String {
    String::from_utf8_lossy(output)
        .lines()
        .find(|l| !l.trim().is_empty())
        .unwrap_or(fallback)
        .trim()
        .to_owned()
}
