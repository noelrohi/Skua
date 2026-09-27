using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Skua.Control;

namespace Skua.App.Cli.Mcp;

/// <summary>
/// The MCP tools: one snake_case tool per Control Surface method, with the same arguments and DTOs, except that <c>screenshot</c> returns its PNG
/// as an image block.
/// </summary>
[McpServerToolType]
internal sealed class EngineTools(Func<EngineClientOptions> options)
{
    [McpServerTool(Name = "status", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(StatusDto))]
    [Description("Liveness and a summary of the Skua Engine and its game, with the player (name, level, class, hp/mp, gold, map/cell/pad, alive, inCombat) while playing. Never fails; fields that don't apply are null. Starts the Engine if it isn't running, but never logs in.")]
    public Task<CallToolResult> Status(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.StatusAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "servers", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ServersResult))]
    [Description("The game servers, fresh from the game's servers API: name, online, player count and max, member-only, language. Works before login.")]
    public Task<CallToolResult> Servers(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.ServersAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "login", Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(LoginResult))]
    [Description("Log the Test Account in; the Engine reads its credentials from Keychain, so none are passed. Returns once it is playing with the world loaded, with the server it plays on. Already playing on the server (or on any, when none is named) it does nothing; playing elsewhere, it relogs. Fails with LoginFailed and the game's reason (e.g. a full server), Timeout, InvalidArgument for an unknown server, Busy during another login, logout, join or jump, or ScriptRunning.")]
    public Task<CallToolResult> Login(
        [Description("A server name from the servers tool; omit it to let the Engine pick an online, non-member server with room.")] string? server = null,
        [Description("Seconds to wait for the world: 120 by default.")] int? timeoutSec = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.LoginAsync(server, timeoutSec, cancellationToken), cancellationToken);

    [McpServerTool(Name = "logout", Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(LogoutResult))]
    [Description("Log out to the login screen. A deliberate logout: game.disconnected reports reason logout and the state becomes loginScreen. Does nothing when not logged in.")]
    public Task<CallToolResult> Logout(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.LogoutAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "join", Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(LocationResult))]
    [Description("Move the player to a map, then to the cell and pad when given; returns the final map, cell and pad, and alreadyThere when nothing was done. Already on the map, it only jumps. Fails with NotLoggedIn unless playing, Timeout when the map never loads (the game ignores a transfer to a map the player may not enter, e.g. member-only), InvalidArgument for a malformed map or a cell the map lacks, ScriptRunning, or Busy during another login, logout, join or jump.")]
    public Task<CallToolResult> Join(
        [Description("A map name, optionally with a room number, e.g. \"battleon\" or \"battleon-1234\".")] string map,
        [Description("The cell to move to on the map; omit it for the one the game places the player in.")] string? cell = null,
        [Description("The pad to stand on in the cell: Spawn by default.")] string? pad = null,
        [Description("Seconds to wait for the map and the cell: 60 by default.")] int? timeoutSec = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.JoinAsync(map, cell, pad, timeoutSec, cancellationToken), cancellationToken);

    [McpServerTool(Name = "jump", Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(LocationResult))]
    [Description("Move the player to a cell on the current map (the map tool lists them); returns the final map, cell and pad, and alreadyThere when nothing was done. Fails as join does.")]
    public Task<CallToolResult> Jump(
        [Description("A cell of the current map, e.g. \"Enter\" or \"r2\"; case doesn't matter.")] string cell,
        [Description("The pad to stand on in the cell: Spawn by default.")] string? pad = null,
        [Description("Seconds to wait for the cell: 30 by default.")] int? timeoutSec = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.JumpAsync(cell, pad, timeoutSec, cancellationToken), cancellationToken);

    [McpServerTool(Name = "inventory", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(InventoryResult))]
    [Description("The items in one of the player's item stores (id, name, qty, maxStack, category, equipped, enhancementLevel) with its used and total slots; the temp store has no slot limit (totalSlots null). The bank is fetched from the game server the first time it is listed after each login. Fails with NotLoggedIn unless playing.")]
    public Task<CallToolResult> Inventory(
        [Description("inventory, bank, temp or house.")] InventoryKind kind = InventoryKind.Inventory,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.InventoryAsync(kind, cancellationToken), cancellationToken);

    [McpServerTool(Name = "quests", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(QuestsResult))]
    [Description("The quests the game has loaded, or only the accepted ones: status (notAccepted, inProgress, completable), member-only, gold, xp, requirements (item, qty needed and how many the player has) and rewards. Fails with NotLoggedIn unless playing.")]
    public Task<CallToolResult> Quests(
        [Description("loaded (every quest the game has loaded) or active (only accepted ones).")] QuestFilter filter = QuestFilter.Loaded,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.QuestsAsync(filter, cancellationToken), cancellationToken);

    [McpServerTool(Name = "map", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MapDto))]
    [Description("The current map: name, roomId, its cells (which jump takes), the players on it and its monsters (mapId targets one). Fails with NotLoggedIn unless playing.")]
    public Task<CallToolResult> Map(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.MapAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "drops", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DropsResult))]
    [Description("The items dropped for the player and not yet picked up or rejected: id, name and qty. Fails with NotLoggedIn unless playing.")]
    public Task<CallToolResult> Drops(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.DropsAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "scripts_search", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ScriptsSearchResult))]
    [Description("Search the Script Source's scripts.json for Scripts. Every word of the query must appear in a Script's name, description, tags or path, ignoring case; an empty query matches every Script. Returns at most 100 Scripts, plus how many matched, each with whether it is downloaded and whether the Script Source has a newer version (outdated). Identify a Script by its path.")]
    public Task<CallToolResult> ScriptsSearch(
        [Description("Words to search for, e.g. \"leveling\" or \"farm gold\"; empty matches every Script.")] string query = "",
        [Description("Only Scripts with this tag, ignoring case.")] string? tag = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.ScriptsSearchAsync(query, tag, cancellationToken), cancellationToken);

    [McpServerTool(Name = "scripts_update", UseStructuredContent = true, OutputSchemaType = typeof(ScriptsUpdateResult))]
    [Description("Sync the Scripts on disk with the Script Source. The first sync downloads every Script (full); later ones download only the Scripts changed since the last synced commit (incremental), or nothing (upToDate). Fails with Busy while another update runs.")]
    public Task<CallToolResult> ScriptsUpdate(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.ScriptsUpdateAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "logs", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(LogPage))]
    [Description("A page of the Engine's log entries in seq order, with the cursor for the next page and whether entries after the given cursor are gone (evicted, or the Engine restarted). Each entry has seq, ts (UTC ms), kind and run, then text or type + data.")]
    public Task<CallToolResult> Logs(
        [Description("script, debug, flash, events, or all (merged by seq).")] LogKind kind = LogKind.All,
        [Description("Return entries after this cursor: the 'next' of an earlier reply. Omit it to start from the oldest entry held.")] string? after = null,
        [Description("Entries per page: 200 by default, at most 1000. A reply also stays within 1 MB.")] int? max = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.LogsAsync(kind, after, max, cancellationToken), cancellationToken);

    [McpServerTool(Name = "screenshot", ReadOnly = true)]
    [Description("See the game: render a frame of the Game Client and return it as a PNG image, at the stage's native size (958x550) unless maxWidth scales it down. Calls made while a capture of the same size is in flight share it. Fails with GameHostDown when there is no Game Host, and with Timeout after 10 s.")]
    public Task<CallToolResult> Screenshot(
        [Description("Scale a wider frame down to this width, keeping its aspect ratio; omit it for the native size.")] int? maxWidth = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.ScreenshotAsync(maxWidth, cancellationToken), shot => new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(shot.Png, "image/png"),
                new TextContentBlock { Text = $"{shot.Width}x{shot.Height} PNG of frame {shot.Frame}." },
            ],
        }, cancellationToken);

    /// <summary>Calls the Engine and returns the DTO as JSON text plus structured content, or the error code and message with isError.</summary>
    private Task<CallToolResult> CallAsync<T>(Func<EngineConnection, Task<T>> call, CancellationToken cancellationToken) =>
        CallAsync(call, result =>
        {
            JsonElement structured = JsonSerializer.SerializeToElement(result, ControlJson.Options);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = structured.GetRawText() }],
                StructuredContent = structured,
            };
        }, cancellationToken);

    /// <summary>Calls the Engine and turns its reply into a result, or returns the error code and message with isError.</summary>
    private async Task<CallToolResult> CallAsync<T>(Func<EngineConnection, Task<T>> call, Func<T, CallToolResult> toResult, CancellationToken cancellationToken)
    {
        try
        {
            using EngineConnection connection = await EngineClient.ConnectAsync(options(), cancellationToken);
            return toResult(await call(connection));
        }
        catch (ControlException e)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"{e.Code}: {e.Message}" }],
            };
        }
    }
}
