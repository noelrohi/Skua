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

Argument<string> scriptsQuery = new("query")
{
    Description = "Words that must all appear in a Script's name, description, tags or path; none matches every Script.",
    Arity = ArgumentArity.ZeroOrOne,
    DefaultValueFactory = _ => "",
};
Option<string?> scriptsTag = new("--tag") { Description = "Only Scripts with this tag." };
Command scriptsSearch = new("search", "Search the Script Source for Scripts, with whether each is downloaded or outdated.") { scriptsQuery, scriptsTag };
scriptsSearch.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.ScriptsSearchAsync(parse.GetValue(scriptsQuery)!, parse.GetValue(scriptsTag), ct);
}, Output.ScriptsSearch));

Command scriptsUpdate = new("update", "Sync the Scripts with the Script Source: a full download the first time, then only changed Scripts.");
scriptsUpdate.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.ScriptsUpdateAsync(ct);
}, Output.ScriptsUpdate));

Command scripts = new("scripts", "Find and sync Scripts from the Script Source.") { scriptsSearch, scriptsUpdate };

Argument<LogKind[]> logKinds = new("kind")
{
    Description = "script, debug, flash, events, or all (merged by seq); several kinds with -f.",
    HelpName = "kind",
    Arity = ArgumentArity.ZeroOrMore,
};
Option<string?> logsAfter = new("--after") { Description = "Start after this cursor: the 'next' of an earlier reply." };
Option<int?> logsMax = new("--max") { Description = "Entries per page: 200 by default, at most 1000." };
Option<bool> follow = new("--follow", "-f") { Description = "Replay from the cursor, then print new entries as they arrive, until interrupted." };
Command logs = new("logs", "Page through the Engine's logs and events, or follow them with -f.") { logKinds, logsAfter, logsMax, follow };
logs.Validators.Add(result =>
{
    if (!result.GetValue(follow) && result.GetValue(logKinds)!.Length > 1)
        result.AddError("A page takes one kind; follow several with -f.");
    if (result.GetValue(follow) && result.GetValue(logsMax) is not null)
        result.AddError("--max applies to one page, not to -f.");
});
logs.SetAction((parse, ct) =>
{
    LogKind[] kinds = parse.GetValue(logKinds) is { Length: > 0 } given ? given : [LogKind.All];
    return parse.GetValue(follow)
        ? Cli.FollowLogsAsync(parse.GetValue(json), kinds, parse.GetValue(logsAfter), ct)
        : Cli.RunAsync(parse.GetValue(json), async options =>
        {
            using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
            return await connection.LogsAsync(kinds[0], parse.GetValue(logsAfter), parse.GetValue(logsMax), ct);
        }, Output.Logs);
});

Command engineStart = new("start", "Start the Engine if it isn't running.");
engineStart.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), options => EngineCommands.StartAsync(options, ct), Output.Engine));

Command engineStop = new("stop", "Stop the Engine: stop its Script, close the Game Host and remove the socket.");
engineStop.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), options => EngineCommands.StopAsync(options, ct), Output.Engine));

Command engineStatus = new("status", "Show whether the Engine is running, without starting it.");
engineStatus.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), options => EngineCommands.StatusAsync(options, ct), Output.Engine));

Command engine = new("engine", "Control the Engine's lifetime.") { engineStart, engineStop, engineStatus };

Command mcp = new("mcp", "Serve the Control Surface as an MCP server over stdio.");
mcp.SetAction((_, ct) => McpServer.RunAsync(ct));

RootCommand root = new("Drive a Skua Engine.") { json, status, scripts, logs, engine, mcp };
return await root.Parse(args).InvokeAsync();
