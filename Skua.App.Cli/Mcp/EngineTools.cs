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
