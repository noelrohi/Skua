using StreamJsonRpc;

namespace Skua.Control;

/// <summary>
/// The Control Surface contract: every method the Engine serves over JSON-RPC.
/// </summary>
/// <remarks>
/// Each method other than <c>hello</c>, <c>shutdown</c> and <c>shutdown_if_idle</c> is one snake_case MCP tool and one <c>skua</c> subcommand with the same arguments and DTOs,
/// except the CLI-only <c>subscribe</c> and <c>scripts_source_set</c>: agents follow logs by paging, and only a developer changes the Script Source.
/// Failures are JSON-RPC errors whose code maps to an <see cref="ErrorCode"/> through <see cref="ErrorCodes"/>.
/// </remarks>
[JsonRpcContract]
public partial interface IEngineRpc
{
    /// <summary>The first call on every connection. Frozen across protocol versions.</summary>
    [JsonRpcMethod("hello")]
    Task<HelloResult> HelloAsync(int protocol, CancellationToken cancellationToken = default);

    /// <summary>Liveness and a summary of the Engine and its game. Never fails.</summary>
    [JsonRpcMethod("status")]
    Task<StatusDto> StatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts a cooperative shutdown and returns before it finishes. Frozen across protocol versions.</summary>
    [JsonRpcMethod("shutdown")]
    Task ShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <c>shutdown</c>, but refuses with <see cref="ErrorCode.ScriptRunning"/> while a Script runs, or <see cref="ErrorCode.Busy"/>
    /// while a command holds the Engine, and keeps any from starting once it has accepted. Frozen across protocol versions.
    /// </summary>
    [JsonRpcMethod("shutdown_if_idle")]
    Task ShutdownIfIdleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the Script Source's <c>scripts.json</c>: every word of <paramref name="query"/> must appear in a Script's name,
    /// description, tags or path, ignoring case. An empty query matches every Script.
    /// </summary>
    /// <param name="tag">When set, only Scripts with this tag (ignoring case) match.</param>
    [JsonRpcMethod("scripts_search")]
    Task<ScriptsSearchResult> ScriptsSearchAsync(string query, string? tag = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Syncs the Scripts on disk with the Script Source: a full download the first time, then only the Scripts changed since the last sync.
    /// A full download also records the Script Source's commits of the last 7 days for <c>scripts_new</c>, with at most 21 GitHub API requests;
    /// when GitHub refuses them, the download still succeeds. A second update while one runs fails with <see cref="ErrorCode.Busy"/>.
    /// </summary>
    [JsonRpcMethod("scripts_update")]
    Task<ScriptsUpdateResult> ScriptsUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One folder of the Script Source's <c>scripts.json</c>: its subfolders, with how many Scripts each holds, and the Scripts directly in it.
    /// Fails with <see cref="ErrorCode.InvalidArgument"/> when no Script is in the folder.
    /// </summary>
    /// <param name="folder">A folder's path, e.g. <c>Farm</c> or <c>Farm/Special</c>, ignoring case; null or empty for the top.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [JsonRpcMethod("scripts_list")]
    Task<ScriptsListResult> ScriptsListAsync(string? folder = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The Scripts that <c>scripts_update</c> added or changed on disk since a point, from the Engine's record of its updates; a full download
    /// isn't news, but the Script Source commits it recorded are. Works offline.
    /// </summary>
    /// <param name="since">A date or time, in the Engine's local time unless it has an offset, or a recorded commit (its first 7 characters or more); by default the last 7 days.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>Fails with <see cref="ErrorCode.InvalidArgument"/> for a <paramref name="since"/> that is neither a date nor a recorded commit.</remarks>
    [JsonRpcMethod("scripts_new")]
    Task<ScriptsNewResult> ScriptsNewAsync(string? since = null, CancellationToken cancellationToken = default);

    /// <summary>The Script Source the Engine fetches Scripts from, whether it is the default, and the default. Works offline.</summary>
    [JsonRpcMethod("scripts_source")]
    Task<ScriptSourceResult> ScriptsSourceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the Script Source in the settings file, or resets it to the default, keeping the rest of the file; the Engine uses it from the next
    /// call, and the next <c>scripts_update</c> from a Script Source other than the last one synced is a full download. CLI-only. Refused with
    /// <see cref="ErrorCode.ScriptRunning"/> while a Script runs and <see cref="ErrorCode.Busy"/> during an update.
    /// </summary>
    /// <param name="source"><c>owner/repo@branch</c>, e.g. <c>noelrohi/Scripts@Skua</c>, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>Fails with <see cref="ErrorCode.InvalidArgument"/> for a <paramref name="source"/> that isn't <c>owner/repo@branch</c>.</remarks>
    [JsonRpcMethod("scripts_source_set")]
    Task<ScriptSourceResult> ScriptsSourceSetAsync(string? source, CancellationToken cancellationToken = default);

    /// <summary>
    /// A page of entries of one kind (or all, merged by seq) after the cursor, or from the oldest entry held when there is none.
    /// </summary>
    /// <param name="kind">One kind, or <see cref="LogKind.All"/>.</param>
    /// <param name="after">The <see cref="LogPage.Next"/> of an earlier page, or null for the oldest entry held.</param>
    /// <param name="max">Entries per page: 200 by default, capped at 1000. A reply also stays within 1 MB.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [JsonRpcMethod("logs")]
    Task<LogPage> LogsAsync(LogKind kind = LogKind.All, string? after = null, int? max = null, CancellationToken cancellationToken = default);

    /// <summary>Replays the entries of the given kinds after the cursor, then follows new ones until cancelled. CLI-only.</summary>
    [JsonRpcMethod("subscribe")]
    IAsyncEnumerable<LogPage> SubscribeAsync(LogKind[] kinds, string? after = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a frame of the Game Client and captures it as a PNG, at the stage's native size or scaled down to <paramref name="maxWidth"/>.
    /// Callers while a capture of the same size is in flight share it. Fails with <see cref="ErrorCode.GameHostDown"/> when there is no Game Host,
    /// and with <see cref="ErrorCode.Timeout"/> after 10 s.
    /// </summary>
    /// <param name="maxWidth">When set, a wider frame is scaled down to this width, keeping its aspect ratio; at least 1.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [JsonRpcMethod("screenshot")]
    Task<ScreenshotResult> ScreenshotAsync(int? maxWidth = null, CancellationToken cancellationToken = default);

    /// <summary>The game servers, fresh from the game's servers API. Works before login.</summary>
    [JsonRpcMethod("servers")]
    Task<ServersResult> ServersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs the active account in (see <c>skua account</c>), reading its credentials from Keychain, and returns once it is playing with the world
    /// and its inventory loaded. An agent's login uses the Test Account instead, unless the active account was added with <c>--allow-agents</c>.
    /// Already playing on the requested server (or on any, when none is named), it does nothing; playing elsewhere, it relogs.
    /// </summary>
    /// <param name="server">A server name from <c>servers</c>; without one, the Engine picks an online, non-member server with room.</param>
    /// <param name="timeoutSec">How long to wait for the world: 120 s by default.</param>
    /// <param name="asAgent">Whether an agent asks, as MCP's <c>login</c> does, rather than a developer at the CLI.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// Fails with <see cref="ErrorCode.LoginFailed"/> and the game's reason (a full or offline server, a rejected account),
    /// <see cref="ErrorCode.Timeout"/>, <see cref="ErrorCode.InvalidArgument"/> for an unknown server, <see cref="ErrorCode.GameHostDown"/>
    /// before the Game Client has loaded, and <see cref="ErrorCode.Busy"/> while another login, logout, join or jump runs.
    /// </remarks>
    [JsonRpcMethod("login")]
    Task<LoginResult> LoginAsync(string? server = null, int? timeoutSec = null, bool asAgent = false, CancellationToken cancellationToken = default);

    /// <summary>Logs out to the login screen; a deliberate logout, so the game isn't reported disconnected. Does nothing when not logged in.</summary>
    [JsonRpcMethod("logout")]
    Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the player to a map, then to the cell and pad when they are given, and returns where the player ended up.
    /// Already there, it does nothing; already on the map, it only jumps.
    /// </summary>
    /// <param name="map">A map name, optionally with a room number, e.g. <c>battleon</c> or <c>battleon-1234</c>.</param>
    /// <param name="cell">The cell to move to on the map; by default the one the game places the player in.</param>
    /// <param name="pad">The pad to stand on in the cell: <c>Spawn</c> by default.</param>
    /// <param name="timeoutSec">How long to wait for the map and the cell: 60 s by default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// Fails with <see cref="ErrorCode.NotLoggedIn"/> unless playing, <see cref="ErrorCode.Timeout"/> when the map never loads (the game refuses
    /// maps the player may not enter), <see cref="ErrorCode.InvalidArgument"/> for a malformed map or a cell the map lacks,
    /// <see cref="ErrorCode.ScriptRunning"/>, and <see cref="ErrorCode.Busy"/> while another login, logout, join or jump runs.
    /// </remarks>
    [JsonRpcMethod("join")]
    Task<LocationResult> JoinAsync(string map, string? cell = null, string? pad = null, int? timeoutSec = null, CancellationToken cancellationToken = default);

    /// <summary>Moves the player to a cell on the current map and returns where the player ended up; already there, it does nothing.</summary>
    /// <param name="cell">A cell of the current map, as <c>map</c> lists them.</param>
    /// <param name="pad">The pad to stand on in the cell: <c>Spawn</c> by default.</param>
    /// <param name="timeoutSec">How long to wait for the cell: 30 s by default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>Fails as <c>join</c> does.</remarks>
    [JsonRpcMethod("jump")]
    Task<LocationResult> JumpAsync(string cell, string? pad = null, int? timeoutSec = null, CancellationToken cancellationToken = default);

    /// <summary>The items in one of the player's item stores, with its used and total slots. Fails with <see cref="ErrorCode.NotLoggedIn"/> unless playing.</summary>
    [JsonRpcMethod("inventory")]
    Task<InventoryResult> InventoryAsync(InventoryKind kind = InventoryKind.Inventory, CancellationToken cancellationToken = default);

    /// <summary>The loaded or active quests, with their requirements and rewards. Fails with <see cref="ErrorCode.NotLoggedIn"/> unless playing.</summary>
    [JsonRpcMethod("quests")]
    Task<QuestsResult> QuestsAsync(QuestFilter filter = QuestFilter.Loaded, CancellationToken cancellationToken = default);

    /// <summary>The current map: its cells, players and monsters. Fails with <see cref="ErrorCode.NotLoggedIn"/> unless playing.</summary>
    [JsonRpcMethod("map")]
    Task<MapDto> MapAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The items dropped for the player since the login and not yet picked up; a rejected drop stays listed, since rejecting happens only in
    /// the Game Client. Fails with <see cref="ErrorCode.NotLoggedIn"/> unless playing.
    /// </summary>
    [JsonRpcMethod("drops")]
    Task<DropsResult> DropsAsync(CancellationToken cancellationToken = default);
    /// Compiles a Script and lists its options: key, group, name, type, the stored value (or the default), the default and an enum's choices.
    /// Refused with <see cref="ErrorCode.ScriptRunning"/> while a Script runs.
    /// </summary>
    /// <param name="script">A path in the Script Source, e.g. <c>Farm/Leveling.cs</c>, or an absolute path.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>Fails with <see cref="ErrorCode.ScriptNotFound"/> when the file is missing and <see cref="ErrorCode.CompileFailed"/> with diagnostics.</remarks>
    [JsonRpcMethod("script_options")]
    Task<ScriptOptionsResult> ScriptOptionsAsync(string script, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the given option values in the Script's options storage, then compiles the Script and starts it on the Script Thread.
    /// Returns once it has started; <c>script_wait</c> follows it.
    /// </summary>
    /// <param name="script">A path in the Script Source, e.g. <c>Farm/Leveling.cs</c>, or an absolute path.</param>
    /// <param name="options">Values by option key, as <c>script_options</c> lists them; the others keep their stored values.</param>
    /// <param name="dialogs">How the run's Questions are answered: <see cref="DialogMode.Ask"/> by default.</param>
    /// <param name="dialogTimeoutSec">How long a Question waits in <see cref="DialogMode.Ask"/> mode: 120 s by default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// Fails with <see cref="ErrorCode.ScriptRunning"/> while a Script runs, <see cref="ErrorCode.ScriptNotFound"/> when the file is missing,
    /// <see cref="ErrorCode.CompileFailed"/> with diagnostics, and <see cref="ErrorCode.InvalidArgument"/> for an unknown option or a bad value.
    /// </remarks>
    [JsonRpcMethod("script_start")]
    Task<ScriptStartResult> ScriptStartAsync(
        string script, IReadOnlyDictionary<string, string>? options = null, DialogMode? dialogs = null, int? dialogTimeoutSec = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the running Script cooperatively and returns once its thread has ended, or once Core gives up on it (about 10 s).
    /// Does nothing when no Script runs.
    /// </summary>
    [JsonRpcMethod("script_stop")]
    Task<ScriptStopResult> ScriptStopAsync(CancellationToken cancellationToken = default);

    /// <summary>The Script state, the run in progress and the last run's outcome.</summary>
    [JsonRpcMethod("script_status")]
    Task<ScriptStatusDto> ScriptStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until a Question is pending, no run is in progress, or the timeout passes. Returns at once when a Question is already pending
    /// or when idle.
    /// </summary>
    /// <param name="timeoutSec">How long to wait: 300 s by default; 0 only looks.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [JsonRpcMethod("script_wait")]
    Task<ScriptWaitResult> ScriptWaitAsync(int? timeoutSec = null, CancellationToken cancellationToken = default);

    /// <summary>The pending Questions, oldest first; <c>status</c> lists them too.</summary>
    [JsonRpcMethod("dialogs")]
    Task<DialogsResult> DialogsAsync(CancellationToken cancellationToken = default);

    /// <summary>Answers a pending Question; the first answer wins.</summary>
    /// <param name="id">The Question's id, as <c>dialogs</c> lists it.</param>
    /// <param name="choice">One of its choices, ignoring case.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// Fails with <see cref="ErrorCode.DialogNotPending"/> when no Question with that id is pending (already answered, timed out or unknown),
    /// and with <see cref="ErrorCode.InvalidArgument"/> for a choice it doesn't offer, which leaves it pending.
    /// </remarks>
    [JsonRpcMethod("dialog_answer")]
    Task<DialogAnswerResult> DialogAnswerAsync(int id, string choice, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compiles a C# snippet against <c>IScriptInterface Bot</c>, as a Script would, and runs it on a thread of its own, also while a Script runs.
    /// An expression returns its value; statements return what they <c>return</c>.
    /// </summary>
    /// <param name="code">An expression such as <c>Bot.Player.Level</c>, or statements such as <c>Bot.Log("hi"); return Bot.Map.Name;</c>.</param>
    /// <param name="timeoutSec">How long the snippet may run once compiled: 30 s by default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// Fails with <see cref="ErrorCode.CompileFailed"/> with diagnostics, and with <see cref="ErrorCode.Timeout"/> when it runs too long,
    /// though it keeps running on its thread. An exception it throws is returned as <see cref="EvalResult.Error"/>.
    /// </remarks>
    [JsonRpcMethod("eval")]
    Task<EvalResult> EvalAsync(string code, int? timeoutSec = null, CancellationToken cancellationToken = default);
}
