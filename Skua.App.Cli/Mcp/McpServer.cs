using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using Skua.Control;
using Skua.Engine;

namespace Skua.App.Cli.Mcp;

/// <summary>
/// <c>skua mcp</c>: the Control Surface as a stdio MCP server. Each tool is one Engine call, and the Engine auto-starts on the first one that
/// drives it; the tools whose CLI commands start no Engine start none (ADR 0007).
/// </summary>
internal static class McpServer
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services
            // Unlike the CLI, it never replaces a stale Engine: it outlives an update, and would replace the newer Engine with its own.
            .AddSingleton(() => new EngineClientOptions { Endpoint = Cli.Endpoint() })
            // As an Engine's own, an update in flight ends with the server, not with the call.
            .AddSingleton(_ => new DataFolderScripts(cancellationToken))
            .AddMcpServer(options => options.ServerInfo = new Implementation
            {
                Name = "skua",
                Version = typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            })
            .WithStdioServerTransport()
            .WithTools<EngineTools>(ControlJson.Options);

        await builder.Build().RunAsync(cancellationToken);
        return ExitCodes.Success;
    }
}
