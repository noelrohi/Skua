using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using Skua.Control;

namespace Skua.App.Cli.Mcp;

/// <summary>
/// <c>skua mcp</c>: the Control Surface as a stdio MCP server. Each tool is one Engine call; the Engine auto-starts on the first one.
/// </summary>
internal static class McpServer
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services
            // Unlike the CLI, it never replaces a stale Engine: it outlives an update, and would replace the newer Engine with its own.
            .AddSingleton(() => new EngineClientOptions { Endpoint = Cli.Endpoint() })
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
