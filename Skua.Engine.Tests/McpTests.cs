using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Skua.Control;

namespace Skua.Engine.Tests;

public class McpTests
{
    [Fact]
    public async Task Skua_mcp_exposes_status_as_a_tool_that_auto_starts_the_Engine()
    {
        await using EngineSandbox sandbox = new();
        await using McpClient client = await ConnectAsync(sandbox);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        CallToolResult result = await client.CallToolAsync("status", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(tools, tool => tool.Name == "status");
        Assert.NotEqual(true, result.IsError);
        StatusDto status = JsonSerializer.Deserialize<StatusDto>(((TextContentBlock)result.Content.Single()).Text, ControlJson.Options)!;
        Assert.Equal("default", status.Engine.Name);
        Assert.Equal("default", result.StructuredContent?.GetProperty("engine").GetProperty("name").GetString());
        Assert.True(EngineLock.IsHeld(sandbox.Endpoint.LockPath));
    }

    [Fact]
    public async Task A_failed_call_returns_isError_with_the_code_and_message()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox);
        await using McpClient client = await ConnectAsync(sandbox);

        CallToolResult result = await client.CallToolAsync("status", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.StartsWith("ProtocolMismatch: ", ((TextContentBlock)result.Content.Single()).Text);
    }

    /// <summary>Starts <c>skua mcp</c> with this sandbox's data folder and extra environment, and connects to it.</summary>
    internal static Task<McpClient> ConnectAsync(EngineSandbox sandbox, IDictionary<string, string>? environment = null)
    {
        Dictionary<string, string?> variables = new()
        {
            [EngineEndpoint.SkuaDirVariable] = sandbox.SkuaDir,
            [EngineEndpoint.SocketVariable] = null,
            [EngineClientOptions.EngineExecutableVariable] = EngineSandbox.EngineExecutable,
        };
        foreach ((string key, string value) in environment ?? new Dictionary<string, string>())
            variables[key] = value;

        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = EngineSandbox.CliExecutable,
            Arguments = ["mcp"],
            ShutdownTimeout = TimeSpan.FromSeconds(1),
            EnvironmentVariables = variables,
        }), cancellationToken: TestContext.Current.CancellationToken);
    }
}
