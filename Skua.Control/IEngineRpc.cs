using StreamJsonRpc;

namespace Skua.Control;

/// <summary>
/// The Control Surface contract: every method the Engine serves over JSON-RPC.
/// </summary>
/// <remarks>
/// Each method other than <c>hello</c> and <c>shutdown</c> is one snake_case MCP tool and one <c>skua</c> subcommand with the same arguments and DTOs.
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
    /// Searches the Script Source's <c>scripts.json</c>: every word of <paramref name="query"/> must appear in a Script's name,
    /// description, tags or path, ignoring case. An empty query matches every Script.
    /// </summary>
    /// <param name="tag">When set, only Scripts with this tag (ignoring case) match.</param>
    [JsonRpcMethod("scripts_search")]
    Task<ScriptsSearchResult> ScriptsSearchAsync(string query, string? tag = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Syncs the Scripts on disk with the Script Source: a full download the first time, then only the Scripts changed since the last sync.
    /// A second update while one runs fails with <see cref="ErrorCode.Busy"/>.
    /// </summary>
    [JsonRpcMethod("scripts_update")]
    Task<ScriptsUpdateResult> ScriptsUpdateAsync(CancellationToken cancellationToken = default);

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
    /// Logs the Test Account in, reading its credentials from Keychain, and returns once it is playing with the world loaded.
    /// Already playing on the requested server (or on any, when none is named), it does nothing; playing elsewhere, it relogs.
    /// </summary>
    /// <param name="server">A server name from <c>servers</c>; without one, the Engine picks an online, non-member server with room.</param>
    /// <param name="timeoutSec">How long to wait for the world: 120 s by default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// Fails with <see cref="ErrorCode.LoginFailed"/> and the game's reason (a full or offline server, a rejected account),
    /// <see cref="ErrorCode.Timeout"/>, <see cref="ErrorCode.InvalidArgument"/> for an unknown server, <see cref="ErrorCode.GameHostDown"/>
    /// before the Game Client has loaded, and <see cref="ErrorCode.Busy"/> while another login, logout, join or jump runs.
    /// </remarks>
    [JsonRpcMethod("login")]
    Task<LoginResult> LoginAsync(string? server = null, int? timeoutSec = null, CancellationToken cancellationToken = default);

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

    /// <summary>The items dropped for the player and not yet picked up or rejected. Fails with <see cref="ErrorCode.NotLoggedIn"/> unless playing.</summary>
    [JsonRpcMethod("drops")]
    Task<DropsResult> DropsAsync(CancellationToken cancellationToken = default);
}
