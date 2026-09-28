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
    [Description("Liveness and a summary of the Skua Engine and its game, with the player (name, level, class, hp/mp, gold, map/cell/pad, alive, inCombat, xp, requiredXp and xpPercent toward the next level) while playing, the running Script with its elapsedSec, and the pending Questions (pendingDialogs). Never fails; fields that don't apply are null. Starts the Engine if it isn't running, but never logs in.")]
    public Task<CallToolResult> Status(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.StatusAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "servers", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ServersResult))]
    [Description("The game servers, fresh from the game's servers API: name, online, player count and max, member-only, language. Works before login.")]
    public Task<CallToolResult> Servers(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.ServersAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "login", Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(LoginResult))]
    [Description("Log the Test Account in; the Engine reads its credentials from Keychain, so none are passed. A developer may instead have made an account active with 'skua account add --allow-agents', which this then uses. Returns once it is playing with the world loaded, with the server it plays on, the account's username and whether it is the Test Account, and a sentence naming them. Already playing on the server (or on any, when none is named) it does nothing; playing elsewhere, it relogs. Fails with LoginFailed and the game's reason (e.g. a full server), Timeout, InvalidArgument for an unknown server, Busy during another login, logout, join or jump, or ScriptRunning.")]
    public Task<CallToolResult> Login(
        [Description("A server name from the servers tool; omit it to let the Engine pick an online, non-member server with room.")] string? server = null,
        [Description("Seconds to wait for the world: 120 by default.")] int? timeoutSec = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.AgentLoginAsync(server, timeoutSec, cancellationToken), result =>
        {
            CallToolResult reply = Structured(result);
            reply.Content.Add(new TextContentBlock { Text = Output.Login(result) });
            return reply;
        }, cancellationToken);

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
    [Description("The items dropped for the player since the login and not yet picked up: id, name and qty (summed over repeat drops). A rejected drop stays listed, since rejecting happens only in the Game Client. Fails with NotLoggedIn unless playing.")]
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
    [Description("Sync the Scripts on disk with the Script Source. The first sync downloads every Script (full); later ones download only the Scripts changed since the last synced commit (incremental), or nothing (upToDate). added and changed list the downloaded Scripts that were new on disk or replaced an older copy; scripts_new lists them later. A full sync also records the Script Source's commits of the last 7 days for scripts_new. Fails with Busy while another update runs.")]
    public Task<CallToolResult> ScriptsUpdate(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.ScriptsUpdateAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "scripts_list", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ScriptsListResult))]
    [Description("Browse one folder of the Script Source's scripts.json: its subfolders (path and how many Scripts each holds) and the Scripts directly in it, with name, description, tags, downloaded and outdated. Fails with InvalidArgument when no Script is in the folder.")]
    public Task<CallToolResult> ScriptsList(
        [Description("A folder's path, e.g. \"Farm\" or \"Farm/Special\", ignoring case; omit it for the top.")] string? folder = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.ScriptsListAsync(folder, cancellationToken), cancellationToken);

    [McpServerTool(Name = "scripts_new", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ScriptsNewResult))]
    [Description("The Scripts that scripts_update added or changed on disk since a point, the latest first: path, name, change (added or changed), when and the commit. A full download isn't news, but the Script Source commits of the week before it are: it recorded them from GitHub, and updates counts the updates while commits counts those commits. historyFrom is when the record starts (null when there is none yet); nothing before it is known, so an empty list with a later historyFrom doesn't mean nothing changed. Reads the Engine's record, so it works offline.")]
    public Task<CallToolResult> ScriptsNew(
        [Description("A date or time, in the Engine's local time unless it has an offset (e.g. \"2026-09-01\" is local midnight, \"2026-09-01T00:00Z\" is UTC), or a recorded commit (its first 7 characters or more); omit it for the last 7 days.")] string? since = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.ScriptsNewAsync(since, cancellationToken), cancellationToken);

    [McpServerTool(Name = "scripts_source", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ScriptSourceResult))]
    [Description("The Script Source that scripts_search, scripts_list and scripts_update use (owner, repo, branch), whether it is the default (isDefault), and the default. Only a developer changes it, with 'skua scripts source'. Works offline.")]
    public Task<CallToolResult> ScriptsSource(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.ScriptsSourceAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "script_options", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ScriptOptionsResult))]
    [Description("Compile a Script and list its options: key (what script_start's options take), group, name, type (bool, int, number, string, enum), the stored value (or the default), the default, an enum's choices, and whether it is transient (resets every start, so can't be set). Fails with ScriptNotFound (run scripts_update), CompileFailed with diagnostics, or ScriptRunning while a Script runs.")]
    public Task<CallToolResult> ScriptOptions(
        [Description("A path in the Script Source, e.g. Farm/Leveling.cs (see scripts_search), or an absolute path to a Script file.")] string script,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.ScriptOptionsAsync(script, cancellationToken), cancellationToken);

    [McpServerTool(Name = "script_start", UseStructuredContent = true, OutputSchemaType = typeof(ScriptStartResult))]
    [Description("Store option values in the Script's options storage, then compile the Script and start it; returns once it has started, with its run number. Follow it with script_wait. Fails with ScriptRunning while a Script runs, ScriptNotFound (run scripts_update), CompileFailed with diagnostics, and InvalidArgument for an unknown option or a bad value. Log entries of the run carry its number as run.")]
    public Task<CallToolResult> ScriptStart(
        [Description("A path in the Script Source, e.g. Farm/Leveling.cs (see scripts_search), or an absolute path to a Script file.")] string script,
        [Description("Option values by key, as script_options lists them, e.g. {\"count\": \"7\"}; the other options keep their stored values, which later runs also use.")] Dictionary<string, string>? options = null,
        [Description("ask (the default): the run's Questions wait for dialog_answer until dialogTimeoutSec, then get the fallback; cancel: they get the fallback at once. The fallback is null / DialogResult.Cancelled, never the first button.")] DialogMode? dialogs = null,
        [Description("Seconds a Question waits in ask mode: 120 by default.")] int? dialogTimeoutSec = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.ScriptStartAsync(script, options, dialogs, dialogTimeoutSec, cancellationToken), cancellationToken);

    [McpServerTool(Name = "script_stop", Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ScriptStopResult))]
    [Description("Stop the running Script cooperatively and return once its thread has ended (ended true, lastRun outcome stopped), or once Core gives up after about 10 s (ended false, outcome stopTimedOut, state stays stopping until the thread ends). Does nothing when no Script runs.")]
    public Task<CallToolResult> ScriptStop(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.ScriptStopAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "script_status", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ScriptStatusDto))]
    [Description("The Script state (idle, compiling, running, stopping), the run in progress (number, script, startedAt, relogins, reloggingIn) and the last run (outcome completed, stopped, error or stopTimedOut, error text, duration). A restart by the auto-relogin is the same run.")]
    public Task<CallToolResult> ScriptStatus(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.ScriptStatusAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "script_wait", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ScriptWaitResult))]
    [Description("Wait until a Question is pending (reason question; also at once when one already is: answer it with dialog_answer, or it gets the fallback at its expiresAt), the run ends (reason ended; also at once when none runs), or the timeout passes (reason timeout), and return the Script status. Loop on it to supervise a long run.")]
    public Task<CallToolResult> ScriptWait(
        [Description("Seconds to wait: 300 by default; 0 only looks.")] int? timeoutSec = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.ScriptWaitAsync(timeoutSec, cancellationToken), cancellationToken);

    [McpServerTool(Name = "dialogs", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DialogsResult))]
    [Description("The pending Questions: Script Dialogs that wait for an answer, such as a Script confirming an AC purchase. Each has id, caption, text, choices, raisedAt, expiresAt (when it gets the fallback: null / DialogResult.Cancelled, never the first button), the thread waiting on it and the Script. Answer with dialog_answer. Notices never wait; they arrive as notice.shown events in logs.")]
    public Task<CallToolResult> Dialogs(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.DialogsAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "dialog_answer", UseStructuredContent = true, OutputSchemaType = typeof(DialogAnswerResult))]
    [Description("Answer a pending Question with one of its choices; the first answer wins and the Script goes on with it. Fails with DialogNotPending when it was already answered, timed out or never existed, and with InvalidArgument for a choice it doesn't offer, which leaves it pending.")]
    public Task<CallToolResult> DialogAnswer(
        [Description("The Question's id, from dialogs, status or the question.raised event.")] int id,
        [Description("One of the Question's choices, ignoring case, e.g. \"Yes\".")] string choice,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.DialogAnswerAsync(id, choice, cancellationToken), cancellationToken);

    [McpServerTool(Name = "eval", UseStructuredContent = true, OutputSchemaType = typeof(EvalResult))]
    [Description("Compile a C# snippet against IScriptInterface Bot, as a Script body, and run it on its own thread, also while a Script runs. An expression (Bot.Player.Level) returns its value; statements return what they return. Returns the value as JSON (best effort), the Script log lines it wrote, and what it threw as error. Fails with CompileFailed and diagnostics, or Timeout after timeoutSec (the snippet keeps running). Reach anything the typed tools don't cover this way.")]
    public Task<CallToolResult> Eval(
        [Description("An expression such as Bot.Player.Level, or statements such as Bot.Map.Join(\"yulgar\"); return Bot.Map.Name;")] string code,
        [Description("Seconds the snippet may run once compiled: 30 by default.")] int? timeoutSec = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(connection => connection.EvalAsync(code, timeoutSec, cancellationToken), cancellationToken);

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
        CallAsync(call, Structured, cancellationToken);

    /// <summary>The DTO as structured content, and as its JSON text for clients that read only text.</summary>
    private static CallToolResult Structured<T>(T result)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(result, ControlJson.Options);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
            StructuredContent = structured,
        };
    }

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
