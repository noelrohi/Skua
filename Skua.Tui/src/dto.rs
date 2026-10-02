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
