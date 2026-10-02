//! The Control Surface DTOs the shell shows, as `Skua.Control` serializes them: camelCase names, camelCase enum strings.

use serde::Deserialize;
use serde_json::Value;

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum EngineHost {
    Engine,
    App,
}

/// The reply to `hello`, frozen across protocol versions. `host` is null from an Engine older than protocol 10.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Hello {
    pub protocol: i64,
    pub build: String,
    pub engine_name: String,
    pub pid: i64,
    #[serde(default)]
    pub host: Option<EngineHost>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    pub engine: EngineInfo,
    pub game: GameStatus,
    pub script: ScriptStatus,
    pub pending_dialogs: Vec<Question>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EngineInfo {
    pub name: String,
    pub build: String,
    pub protocol: i64,
    pub uptime_sec: f64,
    pub pid: i64,
    pub host: EngineHost,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum GameState {
    NotStarted,
    LoginScreen,
    LoggingIn,
    Playing,
    Disconnected,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameStatus {
    pub game_host_up: bool,
    pub state: GameState,
    pub server: Option<String>,
    #[serde(default)]
    pub player: Option<Player>,
    #[serde(default)]
    pub player_age_sec: Option<f64>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Player {
    pub name: String,
    pub level: i64,
    pub class: Option<String>,
    pub hp: i64,
    pub max_hp: i64,
    pub mp: i64,
    pub max_mp: i64,
    pub gold: i64,
    pub map: String,
    pub cell: String,
    pub pad: String,
    pub alive: bool,
    pub in_combat: bool,
    pub xp: i64,
    pub required_xp: i64,
    pub xp_percent: Option<f64>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ScriptState {
    Idle,
    Compiling,
    Running,
    Stopping,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScriptStatus {
    pub state: ScriptState,
    pub run: Option<ScriptRun>,
    pub last_run: Option<ScriptRunResult>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScriptRun {
    pub number: i64,
    pub script: String,
    pub relogins: i64,
    pub relogging_in: bool,
    pub elapsed_sec: f64,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScriptRunResult {
    pub number: i64,
    pub script: String,
    /// One of `completed`, `stopped`, `error`, `stopTimedOut`.
    pub outcome: String,
    pub error: Option<String>,
    pub duration_sec: f64,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Question {
    pub id: i64,
    pub caption: String,
    pub text: String,
    pub choices: Vec<String>,
    pub script: Option<String>,
}

/// One log or event entry: `text` for every kind but `events`, which has `type` and `data`.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogEntry {
    pub seq: i64,
    pub ts: i64,
    pub kind: String,
    pub run: Option<i64>,
    #[serde(default)]
    pub text: Option<String>,
    #[serde(default, rename = "type")]
    pub event_type: Option<String>,
    #[serde(default)]
    pub data: Option<Value>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogPage {
    pub entries: Vec<LogEntry>,
    pub next: String,
    pub gap: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Item {
    pub id: i64,
    pub name: String,
    pub qty: i64,
    pub max_stack: i64,
    pub category: String,
    pub equipped: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Inventory {
    pub used_slots: i64,
    pub total_slots: Option<i64>,
    pub items: Vec<Item>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QuestRequirement {
    pub name: String,
    pub qty: i64,
    pub have: i64,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Quest {
    pub id: i64,
    pub name: String,
    /// One of `notAccepted`, `inProgress`, `completable`.
    pub status: String,
    pub requirements: Vec<QuestRequirement>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Quests {
    pub quests: Vec<Quest>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MapPlayer {
    pub name: String,
    pub level: i64,
    pub cell: String,
    pub hp: i64,
    pub max_hp: i64,
    pub afk: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Monster {
    pub map_id: i64,
    pub name: String,
    pub cell: String,
    pub hp: i64,
    pub max_hp: i64,
    pub alive: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Map {
    pub name: String,
    pub room_id: i64,
    pub cells: Vec<String>,
    pub players: Vec<MapPlayer>,
    pub monsters: Vec<Monster>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Server {
    pub name: String,
    pub online: bool,
    pub player_count: i64,
    pub max_players: i64,
    pub member_only: bool,
    pub language: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Servers {
    pub servers: Vec<Server>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LoginResult {
    pub server: String,
    pub already_logged_in: bool,
    pub username: String,
    pub is_test_account: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogoutResult {
    pub was_logged_in: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScriptInfo {
    pub path: String,
    pub name: Option<String>,
    pub description: Option<String>,
    pub downloaded: bool,
    pub outdated: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScriptsSearch {
    pub matched: i64,
    pub scripts: Vec<ScriptInfo>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScriptOption {
    pub key: String,
    pub category: String,
    pub display_name: String,
    pub description: Option<String>,
    /// One of `bool`, `int`, `number`, `string`, `enum`.
    #[serde(rename = "type")]
    pub kind: String,
    pub value: String,
    pub default: String,
    pub choices: Option<Vec<String>>,
    pub transient: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScriptOptions {
    pub script: String,
    pub options: Vec<ScriptOption>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScriptStartResult {
    pub run: i64,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScriptStopResult {
    pub was_running: bool,
    pub ended: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Dialogs {
    pub questions: Vec<Question>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DialogAnswer {
    pub id: i64,
    pub choice: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Location {
    pub map: String,
    pub cell: String,
    pub pad: String,
    pub already_there: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScriptsUpdate {
    /// One of `full`, `incremental`, `upToDate`.
    pub mode: String,
    pub downloaded: i64,
    pub failed: Vec<String>,
    pub added: Vec<String>,
    pub changed: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ChatSendResult {
    pub channel: String,
    pub to: Option<String>,
}

/// A `hook.ran` event's data (`HookRunDto`): one run of a Hook.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct HookRun {
    pub hook: String,
    pub event_seq: i64,
    pub started_at: i64,
    pub duration_ms: i64,
    /// None when it couldn't be started.
    pub exit_code: Option<i64>,
    pub output: String,
}

impl HookRun {
    pub fn of(entry: &LogEntry) -> HookRun {
        entry
            .data
            .clone()
            .and_then(|data| serde_json::from_value(data).ok())
            .unwrap_or_default()
    }
}
