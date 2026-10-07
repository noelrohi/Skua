using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.Control;

/// <summary>What an entry records. <see cref="All"/> only selects in requests; no entry carries it.</summary>
public enum LogKind
{
    All,

    /// <summary>Verbatim <c>ScriptLog</c> lines.</summary>
    Script,

    /// <summary>Trace output, the Engine's own diagnostics and the Game Host's logs.</summary>
    Debug,

    /// <summary>Bridge call failures, AS3 <c>trace()</c> and uncaught AS3 errors.</summary>
    Flash,

    /// <summary>Typed entries; see <see cref="EventTypes"/>.</summary>
    Events,

    /// <summary>
    /// What the game's chat shows: the message as <see cref="LogEntryDto.Text"/>, and <c>{channel, from, to?}</c> as its data. The channel is the
    /// game's (<c>zone</c>, <c>party</c>, <c>guild</c>, <c>whisper</c>, <c>server</c>, <c>warning</c>, …); from is null for the server's own
    /// messages, and only a whisper has a <c>to</c>.
    /// </summary>
    Game,
}

/// <summary>
/// One log or event entry. It has either <see cref="Text"/> or <see cref="Type"/> and <see cref="Data"/>; a <see cref="LogKind.Game"/> entry has
/// <see cref="Text"/> and <see cref="Data"/>.
/// </summary>
/// <param name="Seq">The Engine-wide sequence number; it orders entries of every kind.</param>
/// <param name="Ts">When the entry was recorded, in UTC milliseconds since the Unix epoch.</param>
/// <param name="Kind">The log the entry belongs to; never <see cref="LogKind.All"/>.</param>
/// <param name="Run">The Script run number, or null outside a run.</param>
/// <param name="Text">The line, for every kind but <see cref="LogKind.Events"/>; a game message's text.</param>
/// <param name="Type">The event type, one of <see cref="EventTypes"/>.</param>
/// <param name="Data">The event's fields, or a game message's channel and sender.</param>
/// <param name="Truncated">Whether a field was cut to its size cap.</param>
public sealed record LogEntryDto(
    long Seq,
    long Ts,
    LogKind Kind,
    int? Run,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Truncated = false);

/// <summary>A page of entries in seq order.</summary>
/// <param name="Entries">The entries, oldest first.</param>
/// <param name="Next">The opaque cursor to pass as <c>after</c> for the entries that follow.</param>
/// <param name="Gap">Whether entries after the given cursor are no longer held: they were evicted, or the Engine restarted.</param>
public sealed record LogPage(IReadOnlyList<LogEntryDto> Entries, string Next, bool Gap);

/// <summary>The type names of <see cref="LogKind.Events"/> entries. They are stable contract strings.</summary>
public static class EventTypes
{
    /// <summary>The Engine started: <c>{name, build, protocol, pid}</c>.</summary>
    public const string EngineStarted = "engine.started";

    /// <summary>
    /// The Engine is stopping: <c>{reason, account, server}</c>. The reason is <c>command</c> (<c>skua engine stop</c>, the <c>shutdown</c> op),
    /// <c>replaced</c> (a <c>skua</c> from another build replacing it while idle), <c>signal</c> (SIGTERM or SIGINT) or <c>quit</c> (its host, such
    /// as the Mac App, quitting). Account and server are what was playing, or null. The game closes without a <c>game.disconnected</c>.
    /// </summary>
    public const string EngineStopping = "engine.stopping";

    /// <summary>The Game Host started: <c>{pid, executable, swf}</c>.</summary>
    public const string GameHostStarted = "gamehost.started";

    /// <summary>The Game Host exited: <c>{code}</c>.</summary>
    public const string GameHostExited = "gamehost.exited";

    /// <summary>A Bridge call failed (<c>{function, args, error}</c>) or the Bridge stream broke (<c>{error}</c>).</summary>
    public const string BridgeError = "bridge.error";

    /// <summary>The Game Client has loaded and shows the login screen: <c>{}</c>.</summary>
    public const string GameLoaded = "game.loaded";

    /// <summary>The game state changed: <c>{from, to}</c>, each a <see cref="GameState"/>; <c>status</c> reports the same state.</summary>
    public const string GameState = "game.state";

    /// <summary>
    /// The account was disconnected: <c>{reason, detail?, account, server}</c>. The reason is the first that applies of <c>gameHostExited</c>,
    /// <c>connectionLost</c> (with the game's connection message as detail), <c>kicked</c>, <c>logout</c> (deliberate: the <c>logout</c> op or the
    /// in-game button) and <c>unknown</c> (logged out with nothing to say why, as the game's idle kick does). Account is the account's name as
    /// <c>login --account</c> takes it, null when the Engine didn't log it in; server is the one it played on. A failed login, a relogin's own
    /// logout and an Engine stopping (see <see cref="EngineStopping"/>) aren't disconnects.
    /// </summary>
    public const string GameDisconnected = "game.disconnected";

    /// <summary>Core's auto-relogin: <c>{phase: "triggered", wasKicked, delayMs}</c>, then <c>{phase: "finished", ok}</c>.</summary>
    public const string GameRelogin = "game.relogin";

    /// <summary>The player joined a map: <c>{map, roomId, cell}</c>; cell is null when the game didn't say.</summary>
    public const string MapJoined = "map.joined";

    /// <summary>The player died: <c>{map, cell}</c>.</summary>
    public const string PlayerDeath = "player.death";

    /// <summary>The game marked the player AFK: <c>{}</c>.</summary>
    public const string PlayerAfk = "player.afk";

    /// <summary>
    /// The inventory is full: <c>{used, slots, drop}</c>, drop null as it fills, or the <c>{id, name}</c> of a drop it has no slot for (one that
    /// isn't in the inventory already). It is checked when an item drops, is added, picked up or bought, and when the player joins a map; while it stays
    /// full, each item dropping is recorded once.
    /// A check that finds a free slot, or a login, re-arms it.
    /// </summary>
    public const string InventoryFull = "inventory.full";

    /// <summary>
    /// The quests a run farms have gone without progress for the stall time (10 minutes): <c>{run, script, idleSec, killsPerMin, quests}</c>, each quest
    /// <c>{id, name, requirements}</c> with its unmet requirements as <c>{itemId, name, have, inBank, qty}</c>. It is recorded once per stall; progress, or
    /// another run, re-arms it. See <see cref="ScriptRunDto.QuestIdleSec"/>.
    /// </summary>
    public const string QuestStalled = "quest.stalled";

    /// <summary>
    /// The game server turned a quest in: <c>{id, name}</c>. Recorded for every turn-in, a Script's, <c>quest_complete</c>'s or the game's own.
    /// </summary>
    public const string QuestCompleted = "quest.completed";

    /// <summary>
    /// The game server refused to turn a quest in: <c>{id, name, reason}</c>, the reason its message (what the game shows after
    /// "Quest Complete Failed:") or null when it gave none. The game's refusal carries no quest ID, so the quest is the one last sent for turn-in.
    /// A Script log line says the same, and <c>quests</c> shows it on the quest until it is turned in.
    /// </summary>
    public const string QuestRejected = "quest.rejected";

    /// <summary>
    /// A run started: <c>{run, script, restart, inventory, temp, bank}</c>. A restart by Core's auto-relogin is the same run, with <c>restart</c> true.
    /// <c>inventory</c>, <c>temp</c> and <c>bank</c> are what the player holds, each a list of <c>{id, name, qty}</c>, and null while not playing;
    /// <c>bank</c> is null until the game has loaded the bank.
    /// </summary>
    public const string ScriptStarted = "script.started";

    /// <summary>
    /// A run ended: <c>{run, script, outcome, durationSec, relogins, error?}</c>, the outcome one of <see cref="ScriptOutcome"/>.
    /// </summary>
    public const string ScriptStopped = "script.stopped";

    /// <summary>A Script threw: <c>{run, script, error, stack}</c>, the stack cut to 4 KB. <c>script.stopped</c> follows.</summary>
    public const string ScriptError = "script.error";

    /// <summary>
    /// A Script Report, which a Script or an <c>eval</c> records with <c>Bot.Report(name, data)</c>: <c>{run, script, name, data}</c>, the data the
    /// object serialized as JSON. Data over 64 KB is its JSON text cut to 64 KB, as a string; data that can't be serialized is <c>{error}</c>.
    /// The run and script are null outside a run. A <c>[report] &lt;name&gt;</c> Script log line accompanies it.
    /// </summary>
    public const string ScriptReport = "script.report";

    /// <summary>
    /// A Script showed a Notice, which never waits: <c>{caption, text, thread, script}</c>, the text cut to 64 KB and the script null outside a run.
    /// </summary>
    public const string NoticeShown = "notice.shown";

    /// <summary>
    /// A Script raised a Question: <c>{id, caption, text, choices, raisedAt, expiresAt, thread, script}</c>, as <c>dialogs</c> lists it.
    /// In <see cref="DialogMode.Cancel"/> mode it expires as it is raised, and <c>question.answered</c> follows at once.
    /// </summary>
    public const string QuestionRaised = "question.raised";

    /// <summary>
    /// A Question was answered: <c>{id, choice, answeredBy}</c>, answeredBy one of <see cref="AnsweredBy"/>. The choice is null for the fallback,
    /// which the Script sees as null or <c>DialogResult.Cancelled</c>.
    /// </summary>
    public const string QuestionAnswered = "question.answered";

    /// <summary>
    /// The Hook Runner ran a Hook for one of this Engine's events: <c>{hook, eventSeq, startedAt, durationMs, exitCode, output}</c>, as
    /// <see cref="HookRunDto"/>. No Hook runs for it.
    /// </summary>
    public const string HookRan = "hook.ran";
}
