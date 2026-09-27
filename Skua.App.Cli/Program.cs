using System.CommandLine;
using System.Runtime.Versioning;
using Skua.App.Cli;
using Skua.App.Cli.Mcp;
using Skua.Control;

[assembly: UnsupportedOSPlatform("windows")]

Option<bool> json = new("--json")
{
    Description = "Print the result (or the error) as JSON.",
    Recursive = true,
};

Command status = new("status", "Show the Engine, its game and the running Script; auto-starts the Engine.");
status.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.StatusAsync(ct);
}, Output.Status));

Command engineStart = new("start", "Start the Engine if it isn't running.");
engineStart.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), options => EngineCommands.StartAsync(options, ct), Output.Engine));

Command engineStop = new("stop", "Stop the Engine: stop its Script, close the Game Host and remove the socket.");
engineStop.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), options => EngineCommands.StopAsync(options, ct), Output.Engine));

Command engineStatus = new("status", "Show whether the Engine is running, without starting it.");
engineStatus.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), options => EngineCommands.StatusAsync(options, ct), Output.Engine));

Command engine = new("engine", "Control the Engine's lifetime.") { engineStart, engineStop, engineStatus };

Command mcp = new("mcp", "Serve the Control Surface as an MCP server over stdio.");
mcp.SetAction((_, ct) => McpServer.RunAsync(ct));

RootCommand root = new("Drive a Skua Engine.") { json, status, engine, mcp };
return await root.Parse(args).InvokeAsync();
