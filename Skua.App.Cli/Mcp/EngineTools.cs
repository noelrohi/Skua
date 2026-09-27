using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Skua.Control;

namespace Skua.App.Cli.Mcp;

/// <summary>The MCP tools: one snake_case tool per Control Surface method, with the same arguments and DTOs.</summary>
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

    /// <summary>Calls the Engine and returns the DTO as JSON text plus structured content, or the error code and message with isError.</summary>
    private async Task<CallToolResult> CallAsync<T>(Func<EngineConnection, Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            using EngineConnection connection = await EngineClient.ConnectAsync(options(), cancellationToken);
            T result = await call(connection);
            JsonElement structured = JsonSerializer.SerializeToElement(result, ControlJson.Options);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = structured.GetRawText() }],
                StructuredContent = structured,
            };
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
