using System.CommandLine;
using System.Runtime.Versioning;
using Skua.App.Cli;
using Skua.App.Cli.Mcp;
using Skua.Control;
using Skua.Engine;

[assembly: UnsupportedOSPlatform("windows")]

Option<bool> json = new("--json")
{
    Description = "Print the result (or the error) as JSON.",
    Recursive = true,
};

Option<string?> engineName = new("--engine")
{
    Description = "The Engine Name of the Engine to talk to (or auto-start). It wins over SKUA_ENGINE_SOCKET; without it, the Engine at SKUA_ENGINE_SOCKET is named after the socket's file, else default.",
    HelpName = "name",
    Recursive = true,
};
engineName.Validators.Add(result =>
{
    if (result.GetValueOrDefault<string?>() is { } name && !EngineName.IsValid(name))
        result.AddError($"Engine Name '{name}' is invalid; it must match [a-z0-9-]{{1,16}}.");
});

Command status = new("status", "Show a running Engine, its game and its Script; it starts none, and fails when the Engine isn't running.");
status.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), options => EngineCommands.StatusOfRunningAsync(options, ct), Output.Status));

Argument<string> scriptsQuery = new("query")
{
    Description = "Words that must all appear in a Script's name, description, tags or path; none matches every Script.",
    Arity = ArgumentArity.ZeroOrOne,
    DefaultValueFactory = _ => "",
};
Option<string?> scriptsTag = new("--tag") { Description = "Only Scripts with this tag." };
Command scriptsSearch = new("search", "Search the Script Source for Scripts, with whether each is downloaded or outdated.") { scriptsQuery, scriptsTag };
scriptsSearch.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json),
    _ => ScriptsCommands.SearchAsync(parse.GetValue(scriptsQuery)!, parse.GetValue(scriptsTag), ct), Output.ScriptsSearch));

Option<bool> scriptsVerify = new("--verify")
{
    Description = "Also hash every local Script against the Script Source's scripts.json and download each one that differs or is missing, "
        + "whatever was synced last; a Script edited on disk is replaced.",
};
Command scriptsUpdate = new("update", "Sync the Scripts with the Script Source: a full download the first time, then only changed Scripts; refused while any Engine runs a Script.")
{
    scriptsVerify,
};
scriptsUpdate.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), _ => ScriptsCommands.UpdateAsync(parse.GetValue(scriptsVerify), ct), Output.ScriptsUpdate));

Argument<string?> scriptsFolder = new("folder")
{
    Description = "A folder of the Script Source, e.g. Farm or Farm/Special, ignoring case; the top by default.",
    Arity = ArgumentArity.ZeroOrOne,
};
Command scriptsList = new("list", "Browse a folder of the Script Source: its subfolders, and its Scripts with their descriptions.") { scriptsFolder };
scriptsList.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), _ => ScriptsCommands.ListAsync(parse.GetValue(scriptsFolder), ct), Output.ScriptsList));

Option<string?> scriptsSince = new("--since") { Description = "A date or time in local time (e.g. 2026-09-01 for local midnight, or 2026-09-01T00:00Z for UTC), or a recorded commit: the last 7 days by default." };
Command scriptsNew = new("new", "List the Scripts that recent Scripts updates, or the Script Source's commits before the first download, added or changed, and when.") { scriptsSince };
scriptsNew.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), _ => ScriptsCommands.NewAsync(parse.GetValue(scriptsSince)), Output.ScriptsNew));

Argument<string?> scriptsSourceValue = new("source")
{
    Description = "owner/repo@branch to fetch Scripts from, e.g. noelrohi/Scripts@Skua; without one, show the Script Source.",
    Arity = ArgumentArity.ZeroOrOne,
};
Option<bool> scriptsSourceDefault = new("--default") { Description = "Reset the Script Source to the default." };
Command scriptsSource = new("source", "Show or set the Script Source; a new one takes effect at once, but not while any Engine runs a Script.")
{
    scriptsSourceValue, scriptsSourceDefault,
};
scriptsSource.Validators.Add(result =>
{
    if (result.GetValue(scriptsSourceValue) is not null && result.GetValue(scriptsSourceDefault))
        result.AddError("Give a Script Source or --default, not both.");
});
scriptsSource.SetAction((parse, ct) =>
{
    string? source = parse.GetValue(scriptsSourceValue);
    bool change = source is not null || parse.GetValue(scriptsSourceDefault);
    return Cli.RunAsync(parse.GetValue(json), _ => change ? ScriptsCommands.SetSourceAsync(source, ct) : Task.FromResult(ScriptsCommands.Source()),
        change ? Output.ScriptSourceChanged : Output.ScriptSource);
});

Argument<string> scriptsCheckPath = new("path")
{
    Description = "The Script's file, absolute or from here, e.g. in a Scripts checkout; else its path in the data folder's Scripts, e.g. Farm/Leveling.cs.",
};
Command scriptsCheck = new("check",
    "Compile a Script and its includes as a start would, without running it or starting an Engine; exits non-zero with each error at its file and line.")
{
    scriptsCheckPath,
};
scriptsCheck.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json),
    _ => Task.FromResult(ScriptCheck.Run(parse.GetValue(scriptsCheckPath)!, EngineEndpoint.DefaultSkuaDir())), Output.ScriptCheck));

Command scripts = new("scripts", "Find, browse, sync and check Scripts from the Script Source; these start no Engine.")
{
    scriptsSearch, scriptsList, scriptsUpdate, scriptsNew, scriptsSource, scriptsCheck,
};

Argument<LogKind[]> logKinds = new("kind")
{
    Description = "script, debug, flash, events, game, or all (merged by seq); several kinds with -f.",
    HelpName = "kind",
    Arity = ArgumentArity.ZeroOrMore,
};
Option<string?> logsAfter = new("--after") { Description = "Start after this cursor: the 'next' of an earlier reply." };
Option<int?> logsMax = new("--max") { Description = "Entries per page: 200 by default, at most 1000." };
Option<int?> logsTail = new("--tail") { Description = "Instead of --max: the newest N entries (after --after's cursor, if given), still oldest first; at most 1000. With -f, it replays them, then follows." };
Option<bool> follow = new("--follow", "-f") { Description = "Replay from the cursor, then print new entries as they arrive, until interrupted." };
Command logs = new("logs", "Page through the Engine's logs and events, or follow them with -f; -f --tail N follows on from the newest N, like tail -f -n N.") { logKinds, logsAfter, logsMax, logsTail, follow };
logs.Validators.Add(result =>
{
    if (!result.GetValue(follow) && result.GetValue(logKinds)!.Length > 1)
        result.AddError("A page takes one kind; follow several with -f.");
    if (result.GetValue(follow) && result.GetValue(logsMax) is not null)
        result.AddError("--max applies to one page, not to -f.");
    if (result.GetValue(logsTail) is not null && result.GetValue(logsMax) is not null)
        result.AddError("Give --max or --tail, not both.");
});
logs.SetAction((parse, ct) =>
{
    LogKind[] kinds = parse.GetValue(logKinds) is { Length: > 0 } given ? given : [LogKind.All];
    return parse.GetValue(follow)
        ? Cli.FollowLogsAsync(parse.GetValue(json), kinds, parse.GetValue(logsAfter), parse.GetValue(logsTail), ct)
        : Cli.RunAsync(parse.GetValue(json), async options =>
        {
            using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
            return await connection.LogsAsync(kinds[0], parse.GetValue(logsAfter), parse.GetValue(logsMax), parse.GetValue(logsTail), ct);
        }, Output.Logs);
});

Option<int?> gainsRun = new("--run") { Description = "The run, by its number in the log: the last run by default." };
Option<string?> gainsFile = new("--file") { Description = "A session log to read: by default the Engine's newest, in <SKUA_DIR>/engines/logs/<engine>/." };
Command logsGains = new("gains",
    "Attribute every item gain of a run to the quest turn-in that paid it, or to monster drops, with what turn-ins took, totals and rates an hour.")
{
    gainsRun, gainsFile,
};
logsGains.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), _ => LogGains.ReadAsync(parse.GetValue(gainsFile), parse.GetValue(gainsRun), ct), Output.Gains));
logs.Subcommands.Add(logsGains);

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
Option<string?> loginAccount = new("--account")
{
    Description = "Log this account in, by name (a Skua Manager account's, or test), without making it active.",
};
Command login = new("login", "Log the active account (see 'skua account') in with its credentials from Keychain, and wait until it is playing.")
{
    loginServer, loginTimeout, loginAccount,
};
login.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.LoginAsync(parse.GetValue(loginServer), parse.GetValue(loginTimeout), parse.GetValue(loginAccount), ct);
}, Output.Login));

Option<string?> accountName = new("--name")
{
    Description = "The account's name, as [a-z0-9-]{1,16}: by default its username in lower case. test is the Test Account's.",
};
Option<bool> accountTest = new("--test") { Description = "Store (or replace) the Test Account, which agents and the live tests use, instead of a personal account." };
Option<bool> accountAllowAgents = new("--allow-agents")
{
    Description = "Let agents' logins (MCP's) use this account while it is active; otherwise they use the Test Account.",
};
Argument<string?> accountUsername = new("username") { Description = "The account's username; without one, it is asked for.", Arity = ArgumentArity.ZeroOrOne };
Option<bool> accountReplace = new("--replace") { Description = "Replace the account already stored under the name, e.g. to change its password." };
Command accountAdd = new("add", "Store a personal account in Keychain, asking for its password without echoing it, and make 'skua login' use it.")
{
    accountUsername, accountName, accountReplace, accountTest, accountAllowAgents,
};
accountAdd.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), _ => AccountCommands.AddAsync(
    parse.GetValue(accountName), parse.GetValue(accountUsername), parse.GetValue(accountReplace), parse.GetValue(accountTest),
    parse.GetValue(accountAllowAgents), ct), Output.AccountAdded));

Argument<string?> accountShowName = new("name") { Description = "An account's name; by default the one 'skua login' uses.", Arity = ArgumentArity.ZeroOrOne };
Command accountShow = new("show", "Show an account's username, Keychain service and whether agents may use it, never its password.") { accountShowName };
accountShow.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), _ => AccountCommands.ShowAsync(parse.GetValue(accountShowName), ct), Output.Account));

Argument<string> accountUseName = new("name") { Description = "An account's name: test for the Test Account, or one given to 'skua account add --name'." };
Command accountUse = new("use", "Make 'skua login' use another account in Keychain; the next login switches to it.") { accountUseName };
accountUse.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), _ => AccountCommands.UseAsync(parse.GetValue(accountUseName)!, ct), Output.AccountUsed));

Argument<string?> accountRemoveName = new("name") { Description = "An account's name; by default the one 'skua login' uses.", Arity = ArgumentArity.ZeroOrOne };
Command accountRemove = new("remove", "Delete an account from Keychain; if 'skua login' used it, it uses the Test Account again.") { accountRemoveName };
accountRemoveName.Description = "An account's name; by default the one 'skua login' uses, unless that is the Test Account, which must be named.";
accountRemove.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), _ => AccountCommands.RemoveAsync(parse.GetValue(accountRemoveName), ct), Output.AccountRemoved));

Command account = new("account", "Keep the accounts 'skua login' uses in Keychain; the password never leaves the prompt and Keychain.")
{
    accountAdd, accountShow, accountUse, accountRemove,
};

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

Argument<int> completeId = new("id") { Description = "The quest's ID, as 'skua quests' lists it." };
Option<int?> completeReward = new("--reward") { Description = "For a quest whose reward the player picks, the item ID to take." };
Option<int?> completeTimeout = new("--timeout") { Description = "Seconds to wait for the game server's answer: 10 by default." };
Command questComplete = new("complete", "Turn a quest in and print the game server's answer; exits with 1 when it refuses.") { completeId, completeReward, completeTimeout };
questComplete.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.QuestCompleteAsync(parse.GetValue(completeId), parse.GetValue(completeReward), parse.GetValue(completeTimeout), ct);
}, Output.QuestComplete, result => result.Completed ? ExitCodes.Success : ExitCodes.Failure));
quests.Subcommands.Add(questComplete);

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
Option<bool> startFollow = new("--follow", "-f")
{
    Description = "Print the run's log lines and events until it ends, under a live status line on a terminal, and ask its Questions there.",
};
Option<bool> startNoUpdate = new("--no-update") { Description = "Start the Scripts on disk as they are, without bringing them up to date with the Script Source first." };
Command scriptStart = new("start", "Update the Scripts from the Script Source, store the given options, compile the Script and start it.")
{
    scriptPath, startOptions, startDialogs, startDialogTimeout, startFollow, startNoUpdate,
};
scriptStart.Validators.Add(result =>
{
    if (result.GetValue(startOptions)!.FirstOrDefault(o => o.IndexOf('=') < 1) is { } bad)
        result.AddError($"--option takes key=value, not '{bad}'.");
});
scriptStart.SetAction((parse, ct) =>
{
    Dictionary<string, string> values = parse.GetValue(startOptions)!.Select(o => o.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    string path = parse.GetValue(scriptPath)!;
    async Task<ScriptStartResult> Start(EngineConnection connection)
    {
        // A Script outside the Script Source has nothing to update.
        if (!parse.GetValue(startNoUpdate) && !Path.IsPathRooted(path))
            await Cli.UpdateBeforeStartAsync(connection, parse.GetValue(json), ct);
        return await connection.ScriptStartAsync(path, values, parse.GetValue(startDialogs), parse.GetValue(startDialogTimeout), ct);
    }
    return parse.GetValue(startFollow)
        ? ScriptFollow.RunAsync(parse.GetValue(json), Start, ct)
        : Cli.RunAsync(parse.GetValue(json), async options =>
        {
            using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
            return await Start(connection);
        }, Output.ScriptStart);
});

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

Option<int> watchInterval = new("--interval")
{
    Description = "Seconds between lines without a terminal or with --json: 2 by default.",
    DefaultValueFactory = _ => 2,
};
Command watch = new("watch", "Show the game's progress and the running Script's log under a live status line; Ctrl-C leaves the Script running.") { watchInterval };
watchInterval.Validators.Add(result =>
{
    if (result.GetValueOrDefault<int>() < 1)
        result.AddError("--interval takes at least 1 second.");
});
watch.SetAction((parse, ct) => Watch.RunAsync(parse.GetValue(json), parse.GetValue(watchInterval), ct));

Command dialogs = new("dialogs", "List the pending Questions of Scripts, or answer one.");
dialogs.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.DialogsAsync(ct);
}, Output.Dialogs));

Argument<int> answerId = new("id") { Description = "The Question's id, as 'skua dialogs' lists it." };
Argument<string> answerChoice = new("choice") { Description = "One of the Question's choices, ignoring case, e.g. Yes." };
Command dialogAnswer = new("answer", "Answer a pending Question; the first answer wins.") { answerId, answerChoice };
dialogAnswer.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.DialogAnswerAsync(parse.GetValue(answerId), parse.GetValue(answerChoice)!, ct);
}, Output.DialogAnswer));
dialogs.Subcommands.Add(dialogAnswer);

Argument<string> chatText = new("text") { Description = "The message, on one line and without '%'." };
Command chatSend = new("send", "Send zone chat as the player.") { chatText };
chatSend.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.ChatSendAsync(parse.GetValue(chatText)!, cancellationToken: ct);
}, Output.ChatSend));

Argument<string> whisperName = new("name") { Description = "The player to whisper." };
Command chatWhisper = new("whisper", "Whisper a player.") { whisperName, chatText };
chatWhisper.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), async options =>
{
    using EngineConnection connection = await EngineClient.ConnectAsync(options, ct);
    return await connection.ChatSendAsync(parse.GetValue(chatText)!, parse.GetValue(whisperName), ct);
}, Output.ChatSend));

Command chat = new("chat", "Send game chat; 'skua logs game' reads it.") { chatSend, chatWhisper };

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

Option<bool> engineListBrief = new("--brief") { Description = "One line per Engine: class, HP, map·cell, target, Script, run time, kills/min and deaths." };
Command engineList = new("list", "List every Engine in the data folder, in the Skua app or windowless, with its status; starts none.") { engineListBrief };
engineList.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json), _ => EngineCommands.ListAsync(ct),
    parse.GetValue(engineListBrief) ? Output.EngineListBrief : Output.EngineList));

Command engine = new("engine", "Control the Engine's lifetime.") { engineStart, engineStop, engineStatus, engineList };

Command hooks = new("hooks",
    "Run the Hook Runner: follow every Engine's events (or --engine's) and run <SKUA_DIR>/hooks/<event type> for each, with the event's JSON on stdin; one per data folder.");
hooks.SetAction((parse, ct) => HookRunner.RunAsync(parse.GetValue(json), parse.GetValue(engineName), ct));

Command mcp = new("mcp", "Serve the Control Surface as an MCP server over stdio.");
mcp.SetAction((_, ct) => McpServer.RunAsync(ct));

RootCommand root = new("Drive a Skua Engine.")
{
    json, engineName, status, account, servers, login, logout, join, jump, inventory, quests, map, drops, scripts, script, watch, dialogs, chat, eval, logs, screenshot, engine, hooks, mcp,
};
Skill.AddTo(root);
ParseResult parsed = root.Parse(args);
Cli.EngineName = parsed.GetValue(engineName);
return await parsed.InvokeAsync();
