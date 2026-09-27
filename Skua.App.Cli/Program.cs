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

Command servers = new("servers", "List the game servers: players, member-only and language. Works before login.");
servers.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.ServersAsync(ct);
}, Output.Servers));

Argument<string?> loginServer = new("server")
{
    Description = "A server from 'skua servers'; without one, the Engine picks an online, non-member server with room.",
    Arity = ArgumentArity.ZeroOrOne,
};
Option<int?> loginTimeout = new("--timeout") { Description = "Seconds to wait for the world: 120 by default." };
Command login = new("login", "Log the Test Account in with its credentials from Keychain, and wait until it is playing.") { loginServer, loginTimeout };
login.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.LoginAsync(parse.GetValue(loginServer), parse.GetValue(loginTimeout), ct);
}, Output.Login));

Command logout = new("logout", "Log out to the login screen.");
logout.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.LogoutAsync(ct);
}, Output.Logout));

Argument<string> joinMap = new("map") { Description = "A map name, optionally with a room number, e.g. battleon or battleon-1234." };
Argument<string?> joinCell = new("cell") { Description = "The cell to move to; by default the one the game places the player in.", Arity = ArgumentArity.ZeroOrOne };
Argument<string?> joinPad = new("pad") { Description = "The pad to stand on in the cell: Spawn by default.", Arity = ArgumentArity.ZeroOrOne };
Option<int?> joinTimeout = new("--timeout") { Description = "Seconds to wait for the map and the cell: 60 by default." };
Command join = new("join", "Move the player to a map, and to a cell and pad on it; print where the player ended up.") { joinMap, joinCell, joinPad, joinTimeout };
join.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.JoinAsync(parse.GetValue(joinMap)!, parse.GetValue(joinCell), parse.GetValue(joinPad), parse.GetValue(joinTimeout), ct);
}, Output.Location));

Argument<string> jumpCell = new("cell") { Description = "A cell of the current map, as 'skua map' lists them." };
Argument<string?> jumpPad = new("pad") { Description = "The pad to stand on in the cell: Spawn by default.", Arity = ArgumentArity.ZeroOrOne };
Option<int?> jumpTimeout = new("--timeout") { Description = "Seconds to wait for the cell: 30 by default." };
Command jump = new("jump", "Move the player to a cell on the current map; print where the player ended up.") { jumpCell, jumpPad, jumpTimeout };
jump.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.JumpAsync(parse.GetValue(jumpCell)!, parse.GetValue(jumpPad), parse.GetValue(jumpTimeout), ct);
}, Output.Location));

Argument<InventoryKind> inventoryKind = new("kind")
{
    Description = "inventory, bank, temp or house: inventory by default.",
    Arity = ArgumentArity.ZeroOrOne,
    DefaultValueFactory = _ => InventoryKind.Inventory,
};
Command inventory = new("inventory", "List the items in one of the player's item stores, with its slots.") { inventoryKind };
inventory.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.InventoryAsync(parse.GetValue(inventoryKind), ct);
}, Output.Inventory));

Argument<QuestFilter> questsFilter = new("filter")
{
    Description = "loaded (every quest the game has loaded) or active (only accepted ones): loaded by default.",
    Arity = ArgumentArity.ZeroOrOne,
    DefaultValueFactory = _ => QuestFilter.Loaded,
};
Command quests = new("quests", "List the loaded or active quests, with their requirements and rewards.") { questsFilter };
quests.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.QuestsAsync(parse.GetValue(questsFilter), ct);
}, Output.Quests));

Command map = new("map", "Show the current map: its cells, players and monsters.");
map.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.MapAsync(ct);
}, Output.Map));

Command drops = new("drops", "List the items dropped for the player since the login and not yet picked up.");
drops.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.DropsAsync(ct);
}, Output.Drops));

Option<int?> screenshotMaxWidth = new("--max-width") { Description = "Scale a wider frame down to this width, keeping its aspect ratio." };
Option<string?> screenshotOut = new("--out", "-o") { Description = "The PNG file to write; by default a new skua-screenshot-<time>.png in the current directory." };
Command screenshot = new("screenshot", "Capture the game as a PNG file and print its path.") { screenshotMaxWidth, screenshotOut };
screenshot.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    ScreenshotResult shot = await connection.ScreenshotAsync(parse.GetValue(screenshotMaxWidth), ct);
    return await ScreenshotFile.WriteAsync(shot, parse.GetValue(screenshotOut), ct);
}, Output.Screenshot));

Argument<string> scriptPath = new("script") { Description = "A path in the Script Source, e.g. Farm/Leveling.cs, or an absolute path." };

Command scriptOptions = new("options", "Compile a Script and list its options: key, type, value, default and choices.") { scriptPath };
scriptOptions.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.ScriptOptionsAsync(parse.GetValue(scriptPath)!, ct);
}, Output.ScriptOptions));

Option<string[]> startOptions = new("--option")
{
    Description = "Store an option value before the start, as <key>=<value> with a key from 'skua script options'; repeat for more.",
    DefaultValueFactory = _ => [],
};
Option<DialogMode?> startDialogs = new("--dialogs") { Description = "ask (the default): Questions wait for an answer; cancel: they get the fallback at once." };
Option<int?> startDialogTimeout = new("--dialog-timeout") { Description = "Seconds a Question waits in ask mode: 120 by default." };
Command scriptStart = new("start", "Store the given options, compile the Script and start it.") { scriptPath, startOptions, startDialogs, startDialogTimeout };
scriptStart.Validators.Add(result =>
{
    if (result.GetValue(startOptions)!.FirstOrDefault(o => o.IndexOf('=') < 1) is { } bad)
        result.AddError($"--option takes key=value, not '{bad}'.");
});
scriptStart.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    Dictionary<string, string> values = parse.GetValue(startOptions)!.Select(o => o.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.ScriptStartAsync(parse.GetValue(scriptPath)!, values, parse.GetValue(startDialogs), parse.GetValue(startDialogTimeout), ct);
}, Output.ScriptStart));

Command scriptStop = new("stop", "Stop the running Script and wait for its thread to end (about 10 s at most).");
scriptStop.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.ScriptStopAsync(ct);
}, Output.ScriptStop));

Command scriptStatus = new("status", "Show the Script state, the run in progress and the last run's outcome.");
scriptStatus.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.ScriptStatusAsync(ct);
}, Output.ScriptStatus));

Option<int?> waitTimeout = new("--timeout") { Description = "Seconds to wait: 300 by default; 0 only looks." };
Command scriptWait = new("wait", "Wait until the run ends, a Question is pending, or the timeout passes.") { waitTimeout };
scriptWait.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.ScriptWaitAsync(parse.GetValue(waitTimeout), ct);
}, Output.ScriptWait));

Command script = new("script", "Run Scripts: options, start, stop, status and wait.") { scriptOptions, scriptStart, scriptStop, scriptStatus, scriptWait };

Argument<string> evalCode = new("code") { Description = "A C# expression or statements against IScriptInterface Bot, e.g. Bot.Player.Level; - reads it from stdin." };
Option<int?> evalTimeout = new("--timeout") { Description = "Seconds the snippet may run once compiled: 30 by default." };
Command eval = new("eval", "Compile and run a C# snippet against the Script API, and print its log lines and value.") { evalCode, evalTimeout };
eval.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    string code = parse.GetValue(evalCode)!;
    if (code == "-")
        code = await Console.In.ReadToEndAsync(ct);
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.EvalAsync(code, parse.GetValue(evalTimeout), ct);
}, Output.Eval));

Command engineStart = new("start", "Start the Engine if it isn't running.");
engineStart.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), options => EngineCommands.StartAsync(options, ct), Output.Engine));

Command engineStop = new("stop", "Stop the Engine: stop its Script, close the Game Host and remove the socket.");
engineStop.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), options => EngineCommands.StopAsync(options, ct), Output.Engine));

Command engineStatus = new("status", "Show whether the Engine is running, without starting it.");
engineStatus.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), options => EngineCommands.StatusAsync(options, ct), Output.Engine));

Command engine = new("engine", "Control the Engine's lifetime.") { engineStart, engineStop, engineStatus };

Command mcp = new("mcp", "Serve the Control Surface as an MCP server over stdio.");
mcp.SetAction((_, ct) => McpServer.RunAsync(ct));

RootCommand root = new("Drive a Skua Engine.")
{
    json, status, servers, login, logout, join, jump, inventory, quests, map, drops, scripts, script, eval, logs, screenshot, engine, mcp,
};
return await root.Parse(args).InvokeAsync();
