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
    /// Skua's options for playing the game, as Scripts set them on `Bot.Options`.
    #[serde(default)]
    pub options: Option<GameOptions>,
}

/// The switches of `Bot.Options` that change how Skua plays.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameOptions {
    pub lag_killer: bool,
    pub hide_players: bool,
    pub private_rooms: bool,
    pub auto_relogin: bool,
    pub safe_timings: bool,
    pub aggro_monsters: bool,
    pub aggro_all_monsters: bool,
    pub infinite_range: bool,
    pub magnetise: bool,
    pub skip_cutscenes: bool,
    pub accept_all_drops: bool,
    pub reject_all_drops: bool,
    pub rest_packets: bool,
}

impl GameOptions {
    /// The options that are on, by the names the TUI shows.
    pub fn on(&self) -> Vec<&'static str> {
        [
            ("lag killer", self.lag_killer),
            ("hide players", self.hide_players),
            ("private rooms", self.private_rooms),
            ("auto relogin", self.auto_relogin),
            ("safe timings", self.safe_timings),
            ("aggro", self.aggro_monsters),
            ("aggro all", self.aggro_all_monsters),
            ("infinite range", self.infinite_range),
            ("magnetise", self.magnetise),
            ("skip cutscenes", self.skip_cutscenes),
            ("accept drops", self.accept_all_drops),
            ("reject drops", self.reject_all_drops),
            ("rest", self.rest_packets),
        ]
        .into_iter()
        .filter_map(|(name, on)| on.then_some(name))
        .collect()
    }
}

/// What the character lets other players do, as the game's own options set it.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Social {
    pub goto: bool,
    pub whisper: bool,
    pub party: bool,
    pub friend: bool,
    pub duel: bool,
    pub guild: bool,
}

impl Social {
    /// Each setting by the name the TUI shows, with whether it is on.
    pub fn all(&self) -> [(&'static str, bool); 6] {
        [
            ("goto", self.goto),
            ("pm", self.whisper),
            ("party", self.party),
            ("friend", self.friend),
            ("duel", self.duel),
            ("guild", self.guild),
        ]
    }
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
    /// The monster the player targets, by its map ID as `map` lists it.
    pub target_id: Option<i64>,
    /// The character's social settings in the game's own options.
    #[serde(default)]
    pub social: Option<Social>,
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
    /// How long the accepted quests' unmet requirements have gone without a rise, at most `elapsed_sec`; None when there are none.
    pub quest_idle_sec: Option<f64>,
    /// The monsters the player was credited with killing during the run, and how many a minute over the last 5 minutes.
    #[serde(default)]
    pub kills: i64,
    pub kills_per_min: Option<f64>,
    /// How many times the player died during the run.
    #[serde(default)]
    pub deaths: i64,
    /// What the Script is working toward, as its CoreBots log lines say.
    pub goal: Option<ScriptGoal>,
    /// What the player held as the run started, or first in game during it; only `script_status` gives it, and `None` until then.
    #[serde(default)]
    pub held: Option<HeldItems>,
}

/// What the player held, as `script.started` carries it; the inventory is all Bags needs.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HeldItems {
    pub inventory: Vec<HeldItem>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HeldItem {
    pub id: i64,
    pub name: String,
    pub qty: i64,
}

/// Each step is what the one before it needs: the quest, the item bought for it, the material farmed for that, what it kills now.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScriptGoal {
    pub quest: Option<String>,
    pub buy: Option<GoalItem>,
    pub farm: Option<GoalItem>,
    pub now: Option<String>,
    pub resets: i64,
    pub last_reset_at: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GoalItem {
    pub item: String,
    pub want: i64,
    pub have: Option<i64>,
    pub per_hour: Option<f64>,
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

/// The game's picture: `png` is the PNG in base64, as JSON carries bytes; `frame` counts the Game Host's frames.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Screenshot {
    pub width: i64,
    pub height: i64,
    pub frame: i64,
    pub png: String,
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
    /// How long since the Engine saw `have` rise, or began watching the requirement if it hasn't.
    pub idle_sec: Option<f64>,
    /// How much the count rose per hour over the last hour watched; None until the Engine has watched it for 5 minutes.
    pub gain_per_hour: Option<f64>,
    /// How many the bank holds, which a Script takes out for the turn-in; 0 until the game has loaded the bank.
    #[serde(default)]
    pub in_bank: i64,
}

impl QuestRequirement {
    /// What the player owns toward it: the inventory's (or temporary inventory's) and the bank's.
    pub fn owned(&self) -> i64 {
        self.have + self.in_bank
    }
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
