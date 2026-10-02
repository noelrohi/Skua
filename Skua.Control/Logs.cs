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
    /// The Test Account was disconnected: <c>{reason, detail?}</c>. The reason is the first that applies of <c>gameHostExited</c>, <c>connectionLost</c>
    /// (with the game's connection message as detail), <c>kicked</c> and <c>logout</c> (deliberate: the <c>logout</c> op, a Script or the
    /// in-game button). A failed login and a relogin's own logout aren't disconnects.
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
    /// A run started: <c>{run, script, restart}</c>. A restart by Core's auto-relogin is the same run, with <c>restart</c> true.
    /// </summary>
    public const string ScriptStarted = "script.started";

    /// <summary>
    /// A run ended: <c>{run, script, outcome, durationSec, relogins, error?}</c>, the outcome one of <see cref="ScriptOutcome"/>.
    /// </summary>
    public const string ScriptStopped = "script.stopped";

    /// <summary>A Script threw: <c>{run, script, error, stack}</c>, the stack cut to 4 KB. <c>script.stopped</c> follows.</summary>
    public const string ScriptError = "script.error";

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
}
