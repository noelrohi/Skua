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
    [Description("Liveness and a summary of the Skua Engine and its game. Never fails; fields that don't apply are null. Starts the Engine if it isn't running, but never logs in.")]
    public Task<CallToolResult> Status(CancellationToken cancellationToken) =>
        CallAsync(connection => connection.StatusAsync(cancellationToken), cancellationToken);

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
