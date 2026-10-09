using System.Text;
using System.Text.Json;
using Skua.Control;
using Skua.Engine;

namespace Skua.App.Cli;

/// <summary>Human-readable renderings of the Control Surface DTOs; <c>--json</c> prints the DTOs themselves.</summary>
internal static class Output
{
    public static JsonSerializerOptions JsonOptions { get; } = new(ControlJson.Options) { WriteIndented = true };

    public static string Status(StatusDto status)
    {
        EngineInfoDto engine = status.Engine;
        return $"Engine  {engine.Name} ({(engine.Host == EngineHost.App ? "in the Skua app, " : "")}pid {engine.Pid}, up {engine.UptimeSec:0} s, "
            + $"build {engine.Build}, protocol {engine.Protocol})\n{(OtherBuild(engine, status.Script) is { } note ? note + "\n" : "")}{GameAndScript(status)}";
    }

    /// <summary>
    /// The line that says an Engine is from another build than this skua's, and what replaces it; null for one from this build. The notice when
    /// a command keeps such an Engine shows only once, so this keeps it in view.
    /// </summary>
    private static string? OtherBuild(EngineInfoDto engine, ScriptStatusDto script)
    {
        if (engine.Build == ControlProtocol.Build)
            return null;
        string replaces = engine.Host == EngineHost.App ? "quitting the app replaces it"
            : script.Run is not null ? "a skua command that drives the game replaces it once its Script ends"
            : "the next skua command that drives the game replaces it";
        return $"Build   {engine.Build}, another build than this skua's {ControlProtocol.Build}; {replaces}";
    }

    /// <summary>
    /// One line per Engine for watching a party: Engine Name, class, HP, map·cell, target, Script and run time, kills a minute and deaths,
    /// in columns. An Engine that isn't playing shows its game state, and one that isn't running why.
    /// </summary>
    public static string EngineListBrief(IReadOnlyList<EngineListEntry> engines)
    {
        if (engines.Count == 0)
            return "No Engines are running.";
        List<(string[] Cells, bool Playing)> rows = [.. engines.Select(BriefRow)];
        // The names line up, and so do the columns of the Engines that are playing; the last cell of a row is never padded.
        int Width(int column, bool playingOnly) => rows.Where(row => (!playingOnly || row.Playing) && row.Cells.Length > column + 1)
            .Select(row => row.Cells[column].Length).DefaultIfEmpty(0).Max();
        int[] widths = [.. Enumerable.Range(0, rows.Max(row => row.Cells.Length)).Select(column => Width(column, playingOnly: column > 0))];
        return string.Join('\n', rows.Select(row => string.Join("  ", row.Cells.Select((cell, column) =>
            column < row.Cells.Length - 1 && (column == 0 || row.Playing) ? cell.PadRight(widths[column]) : cell))));
    }

    private static (string[] Cells, bool Playing) BriefRow(EngineListEntry entry)
    {
        if (entry.Status is not { } status)
            return ([entry.Engine.Name, entry.Engine.State switch
            {
                EngineState.Running when entry.Engine.Compatible == false => $"protocol {entry.Engine.Protocol}, not {ControlProtocol.Version}",
                EngineState.Running => "not answering",
                EngineState.StartingOrHung => "starting or hung",
                _ => "stopped",
            }], false);

        List<string> cells = [status.Engine.Name];
        ScriptRunDto? run = status.Script.Run;
        string script = run is null ? "no Script" : $"{run.Script} {Duration(run.ElapsedSec)}";
        if (status.Game.Player is { } player)
            cells.AddRange([player.Class ?? "no class", $"HP {player.Hp}/{player.MaxHp}", $"{player.Map}·{player.Cell}",
                player.TargetId is { } id ? $"{player.Target ?? "monster"} #{id}" : "no target", script]);
        else
            cells.AddRange([Name(status.Game.State), script]);
        if (run is not null)
            cells.AddRange([$"{(run.KillsPerMin is { } perMin ? $"{perMin:0.0}" : "-")} kills/min", Count(run.Deaths, "death")]);
        if (status.PendingDialogs.Count > 0)
            cells.Add(Count(status.PendingDialogs.Count, "Question"));
        if (status.Engine.Build != ControlProtocol.Build)
            cells.Add("another build");
        return ([.. cells], status.Game.Player is not null);
    }

    /// <summary>One block per Engine: its name and host, then its game and Script indented; one without a status says why on one line.</summary>
    public static string EngineList(IReadOnlyList<EngineListEntry> engines)
    {
        if (engines.Count == 0)
            return "No Engines are running.";
        return string.Join("\n\n", engines.Select(entry => entry.Status is not { Engine: var engine } status
            ? Engine(entry.Engine)
            : $"{engine.Name}  {(engine.Host == EngineHost.App ? "in the Skua app" : "windowless")}, pid {engine.Pid}, up {engine.UptimeSec:0} s, build {engine.Build}\n"
                + string.Join('\n', $"{(OtherBuild(engine, status.Script) is { } note ? note + "\n" : "")}{GameAndScript(status)}".Split('\n').Select(line => "  " + line))));
    }

    /// <summary>The lines of a status below its Engine: the game, the Script, the player and any pending Questions.</summary>
    private static string GameAndScript(StatusDto status)
    {
        GameStatusDto game = status.Game;
        string gameLine = game.GameHostUp
            ? $"Game Host up, {Name(game.State)}{(game.Server is { } server ? $" on {server}" : "")}"
            : "Game Host down";
        string text = $"""
            Game    {gameLine}
            Script  {ScriptLine(status.Script)}
            """;
        if (game.Player is { } player)
            text += $"\nPlayer  {Player(player)}{(game.PlayerAgeSec is { } age ? $" (stale, read {age:0} s ago)" : "")}";
        else if (game.State == GameState.Playing)
            text += "\nPlayer  unknown: the game didn't answer in time";
        else
            text += "\nPlayer  none (not playing)";
        if (game.Player?.Social is { } social)
            text += $"\nSocial  {Social(social)}";
        if (game.Options is { } options)
            text += $"\nOptions {GameOptions(options)}";
        return status.PendingDialogs.Count > 0
            ? $"{text}\nDialogs {status.PendingDialogs.Count} Question{(status.PendingDialogs.Count == 1 ? "" : "s")} pending; see 'skua dialogs'"
            : text;
    }

    /// <summary>The game's social options, those on and then those off: <c>goto, whisper, party, guild on; friend, duel off</c>.</summary>
    private static string Social(SocialDto social)
    {
        (string Name, bool On)[] all =
            [("goto", social.Goto), ("whisper", social.Whisper), ("party", social.Party), ("friend", social.Friend), ("duel", social.Duel), ("guild", social.Guild)];
        string on = string.Join(", ", all.Where(o => o.On).Select(o => o.Name));
        string off = string.Join(", ", all.Where(o => !o.On).Select(o => o.Name));
        return (on, off) switch
        {
            ("", _) => "all off",
            (_, "") => "all on",
            _ => $"{on} on; {off} off",
        };
    }

    /// <summary>Skua's options that are on, or <c>none on</c>.</summary>
    private static string GameOptions(GameOptionsDto options)
    {
        (string Name, bool On)[] all =
        [
            ("lag killer", options.LagKiller), ("hide players", options.HidePlayers), ("private rooms", options.PrivateRooms),
            ("auto relogin", options.AutoRelogin), ("safe timings", options.SafeTimings), ("aggro", options.AggroMonsters),
            ("aggro all", options.AggroAllMonsters), ("infinite range", options.InfiniteRange), ("magnetise", options.Magnetise),
            ("skip cutscenes", options.SkipCutscenes), ("accept drops", options.AcceptAllDrops), ("reject drops", options.RejectAllDrops),
            ("rest", options.RestPackets),
        ];
        string on = string.Join(", ", all.Where(o => o.On).Select(o => o.Name));
        return on == "" ? "none on" : on;
    }

    public static string Dialogs(DialogsResult result)
    {
        if (result.Questions.Count == 0)
            return "No Questions are pending.";
        StringBuilder text = new();
        foreach (QuestionDto question in result.Questions)
        {
            double left = Math.Max(0, (question.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
            if (text.Length > 0)
                text.AppendLine();
            text.AppendLine($"Question {question.Id} '{question.Caption}' ({string.Join(" / ", question.Choices)}), from {question.Script ?? "outside a run"} on {question.Thread}, {left:0} s left")
                .Append($"  {question.Text}");
        }
        return text.ToString();
    }

    public static string DialogAnswer(DialogAnswerResult result) => $"Answered Question {result.Id}: {result.Choice}.";

    public static string ChatSend(ChatSendResult result) => $"Sent to {result.To ?? result.Channel}: {result.Text}";

    private static string Player(PlayerDto player)
    {
        string state = !player.Alive ? ", dead" : player.InCombat ? ", in combat" : "";
        string xp = player.XpPercent is { } percent ? $", XP {player.Xp}/{player.RequiredXp} ({percent:0.0}%)" : "";
        return $"{player.Name}, level {player.Level}{(player.Class is { } playerClass ? $" {playerClass}" : "")}{xp}, HP {player.Hp}/{player.MaxHp}, MP {player.Mp}/{player.MaxMp}, " +
            $"{player.Gold} gold, on {player.Map} in {player.Cell} ({player.Pad}){state}";
    }

    public static string Location(LocationResult result) =>
        $"{(result.AlreadyThere ? "Already" : "Now")} on {result.Map} in {result.Cell} ({result.Pad}).";

    public static string Inventory(InventoryResult result)
    {
        StringBuilder text = new(result.TotalSlots is { } total
            ? $"{Name(result.Kind)}: {result.UsedSlots}/{total} slots used"
            : $"{Name(result.Kind)}: {result.UsedSlots} items");
        foreach (ItemDto item in result.Items)
        {
            string equipped = item.Equipped ? "  equipped" : "";
            string enhancement = item.EnhancementLevel > 0 ? $"  enhancement {item.EnhancementLevel}" : "";
            text.AppendLine().Append($"  {item.Id,8}  {item.Name}  {item.Qty}/{item.MaxStack}  {item.Category}{equipped}{enhancement}");
        }
        return text.ToString();
    }

    public static string ScriptStatus(ScriptStatusDto status)
    {
        string text = ScriptLine(status);
        return status.Run is not null && status.LastRun is { } last ? $"{text}\nLast    {RunResult(last)}" : text;
    }

    public static string ScriptStart(ScriptStartResult result) =>
        $"Started run {result.Run} ({result.Status.Run?.Script ?? result.Status.LastRun?.Script}).\n{ScriptStatus(result.Status)}";

    public static string ScriptStop(ScriptStopResult result) => result switch
    {
        { WasRunning: false } => "No Script was running.",
        { Ended: false } => $"The Script didn't stop in time and its thread still runs; the Engine stays stopping until it ends. {RunResult(result.Status.LastRun!)}",
        _ => $"Stopped run {result.Status.LastRun?.Number} ({result.Status.LastRun?.Script}).",
    };

    public static string ScriptWait(ScriptWaitResult result) => result.Reason switch
    {
        ScriptWaitReason.Timeout => $"Still running after the timeout: {ScriptLine(result.Status)}",
        ScriptWaitReason.Question => $"A Question is pending; see 'skua dialogs'. {ScriptLine(result.Status)}",
        _ => ScriptLine(result.Status),
    };

    public static string ScriptOptions(ScriptOptionsResult result)
    {
        if (result.Options.Count == 0)
            return $"{result.Script} has no options.";
        StringBuilder text = new($"{result.Script} keeps its options in '{result.Storage}':");
        int width = result.Options.Max(o => o.Key.Length);
        foreach (ScriptOptionDto option in result.Options)
        {
            string shown = option.Value.Length == 0 ? "(empty)" : option.Value;
            string value = option.Value == option.Default ? shown : $"{shown} (default {option.Default})";
            string choices = option.Choices is { } list ? $" [{string.Join(", ", list)}]" : "";
            string transient = option.Transient ? " (transient)" : "";
            text.AppendLine().Append($"  {option.Key.PadRight(width)}  {option.Type,-6}  {value}{choices}{transient}");
            if (option.Description is { } description)
                text.AppendLine().Append($"  {"".PadRight(width)}  {description}");
        }
        return text.ToString();
    }

    public static string Quests(QuestsResult result)
    {
        if (result.Quests.Count == 0)
            return result.Filter == QuestFilter.Active ? "No quests are active." : "No quests are loaded.";

        StringBuilder text = new();
        foreach (QuestDto quest in result.Quests)
        {
            text.AppendLine($"{quest.Id} {quest.Name}: {Name(quest.Status)}{(quest.MemberOnly ? ", member-only" : "")}{Repeat(quest)}");
            if (quest.LastRejection is { } rejection)
                text.AppendLine(rejection.Reason is { } reason ? $"    last turn-in refused: {reason}" : "    last turn-in refused, with no reason");
            foreach (QuestRequirementDto requirement in quest.Requirements)
                text.AppendLine($"    needs {requirement.Name} {requirement.Have}/{requirement.Qty}{(requirement.Temp ? " (temp)" : "")}");
            foreach (QuestRewardDto reward in quest.Rewards)
                text.AppendLine($"    rewards {reward.Name} x{reward.Qty}");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>A repeating quest's period and whether it is done in it, e.g. <c>, weekly, done this week</c>.</summary>
    private static string Repeat(QuestDto quest)
    {
        if (quest.Repeat is not { } repeat)
            return "";
        string period = repeat switch
        {
            QuestRepeat.Weekly => "this week",
            QuestRepeat.Monthly => "this month",
            _ => "today",
        };
        string done = quest.RepeatDone switch
        {
            true => $"done {period}",
            false => $"not done {period}",
            null => $"unknown whether done {period}",
        };
        return $", {Name(repeat)}, {done}";
    }

    public static string QuestComplete(QuestCompleteResult result)
    {
        string quest = result.Name is null ? $"quest {result.Id}" : $"quest {result.Id} {result.Name}";
        if (result.Completed)
            return $"Turned in {quest}.";
        return result.Reason is { } reason
            ? $"The game server refused to turn in {quest}: {reason}"
            : $"The game server refused to turn in {quest} and gave no reason.";
    }

    public static string Map(MapDto map)
    {
        IEnumerable<string> players = map.Players.Select(p => $"{p.Name} (level {p.Level}, {p.Cell}{(p.Afk ? ", AFK" : "")})");
        IEnumerable<string> monsters = map.Monsters.Select(m => $"{m.Name} #{m.MapId} ({m.Cell}, {(m.Alive ? $"HP {m.Hp}/{m.MaxHp}" : "dead")})");
        return $"""
            Map       {map.Name} (room {map.RoomId})
            Cells     {string.Join(", ", map.Cells)}
            Players   {(map.Players.Count > 0 ? string.Join(", ", players) : "none")}
            Monsters  {(map.Monsters.Count > 0 ? string.Join(", ", monsters) : "none")}
            """;
    }

    public static string Drops(DropsResult result) =>
        result.Drops.Count == 0 ? "No drops." : string.Join("\n", result.Drops.Select(d => $"{d.Id,8}  {d.Name} x{d.Qty}"));
    /// <summary>The log lines, then the value as JSON (or the exception).</summary>
    public static string Eval(EvalResult result)
    {
        StringBuilder text = new();
        foreach (string line in result.Logs)
            text.AppendLine($"log: {line}");
        text.Append(result.Error is { } error ? $"threw {error}" : result.Value is { } value ? value.GetRawText() : "null");
        return text.ToString();
    }

    private static string ScriptLine(ScriptStatusDto status) => status switch
    {
        { Run: { } run } => $"{Name(status.State)} {run.Script}, run {run.Number}, {Duration(run.ElapsedSec)}"
            + $"{(run.ReloggingIn ? ", waiting for the auto-relogin" : "")}{(run.Relogins > 0 ? $", {run.Relogins} relogins" : "")}",
        { LastRun: { } last } => $"{Name(status.State)}; {RunResult(last)}",
        _ => Name(status.State),
    };

    /// <summary>A run's elapsed time: <c>42 s</c>, <c>12:05</c> or <c>1:02:05</c>.</summary>
    public static string Duration(double seconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Floor(seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time:mm\\:ss}" : time.TotalMinutes >= 1 ? $"{time:m\\:ss}" : $"{time.TotalSeconds:0} s";
    }

    private static string RunResult(ScriptRunResultDto run) =>
        $"Run {run.Number} ({run.Script}) {Name(run.Outcome)} after {run.DurationSec:0.#} s{(run.Error is { } error ? $": {error}" : "")}.";

    public static string Servers(ServersResult result)
    {
        int width = result.Servers.Max(s => s.Name.Length);
        StringBuilder text = new();
        foreach (ServerDto server in result.Servers)
        {
            string state = !server.Online ? "offline" : server.MaxPlayers > 0 && server.PlayerCount >= server.MaxPlayers ? "full" : "";
            string players = $"{server.PlayerCount}/{server.MaxPlayers}";
            text.AppendLine($"{server.Name.PadRight(width)}  {players,9}  {state,-7}  {(server.MemberOnly ? "member" : ""),-6}  {server.Language}".TrimEnd());
        }
        return text.ToString().TrimEnd();
    }

    public static string Login(LoginResult result) =>
        $"{(result.AlreadyLoggedIn ? "Already playing" : "Logged in")} as {result.Username}{(result.IsTestAccount ? " (the Test Account)" : "")} on {result.Server}.";

    public static string AccountAdded(AccountDto account) =>
        $"Added account {account.Name} ({account.Username}) to Keychain as '{account.Service}'"
        + (account.Active ? "; 'skua login' now uses it" : "; agents' logins use it")
        + (account.AllowAgents && account.Service != AccountSetting.DefaultService ? ", and so may agents' while it is active." : ".");

    public static string Account(AccountDto account)
    {
        string[] flags = [.. account.Active ? ["active"] : Array.Empty<string>(), .. account.AllowAgents ? ["agents allowed"] : Array.Empty<string>()];
        return $"{AccountName(account)}{(flags.Length > 0 ? $" ({string.Join(", ", flags)})" : "")}: {account.Username}, Keychain service '{account.Service}'";
    }

    public static string AccountUsed(AccountDto account) => $"Now using {AccountName(account, lower: true)} ({account.Username}); 'skua login' uses it.";

    public static string AccountRemoved(AccountDto account) =>
        $"Removed {AccountName(account, lower: true)} ({account.Username}) from Keychain{(account.Active ? "; 'skua login' uses the Test Account again" : "")}.";

    private static string AccountName(AccountDto account, bool lower = false) =>
        account.Name is { } name ? $"{(lower ? "account" : "Account")} {name}" : $"{(lower ? "the" : "The")} account under '{account.Service}'";

    public static string Logout(LogoutResult result) => result.WasLoggedIn ? "Logged out." : "Not logged in.";

    public static string ScriptsSearch(ScriptsSearchResult result)
    {
        if (result.Matched == 0)
            return $"No Scripts in {Source(result.Source)} match.";

        StringBuilder text = new(result.Matched > result.Scripts.Count
            ? $"{result.Matched} Scripts in {Source(result.Source)} match; showing the first {result.Scripts.Count}, so narrow the search."
            : $"{result.Matched} Scripts in {Source(result.Source)} match.");
        int width = result.Scripts.Max(s => s.Path.Length);
        foreach (ScriptDto script in result.Scripts)
        {
            string state = script.Outdated ? "outdated" : script.Downloaded ? "downloaded" : "missing";
            string tags = script.Tags.Count > 0 ? $" [{string.Join(", ", script.Tags)}]" : "";
            text.AppendLine().Append($"  {script.Path.PadRight(width)}  {state,-10}  {script.Name}{tags}".TrimEnd());
        }
        return text.ToString();
    }

    public static string ScriptsUpdate(ScriptsUpdateResult result)
    {
        string commit = result.Commit.Length > 7 ? result.Commit[..7] : result.Commit;
        string text = result.Mode switch
        {
            ScriptsUpdateMode.Full => $"Downloaded {result.Downloaded} Scripts from {Source(result.Source)} at {commit} (full download).",
            ScriptsUpdateMode.Incremental => $"Downloaded {result.Downloaded} changed Scripts from {Source(result.Source)} at {commit}.",
            _ => $"The Scripts are up to date with {Source(result.Source)} at {commit}.",
        };
        if (result.Mode == ScriptsUpdateMode.Incremental && result.Added.Count + result.Changed.Count > 0)
            text += $"\n{ChangeCounts(result)}; see 'skua scripts new'.";
        if (result.Failed.Count > 0)
            text += $"\n{result.Failed.Count} failed to download; run 'skua scripts update' again: {string.Join(", ", result.Failed)}";
        return text;
    }

    public static string ScriptCheck(ScriptCheckResult result) =>
        string.Join('\n', [$"{result.Script} compiles, with {Count(result.Includes.Count, "include")}.", .. result.Warnings]);

    /// <summary>What the update before <c>skua script start</c> did, in one line; null when it downloaded nothing and nothing failed.</summary>
    public static string? StartUpdate(ScriptsUpdateResult result) => result switch
    {
        { Mode: ScriptsUpdateMode.UpToDate } or { Downloaded: 0, Failed.Count: 0 } => null,
        { Mode: ScriptsUpdateMode.Full } => ScriptsUpdate(result).ReplaceLineEndings(" "),
        _ => $"Updated the Scripts from {Source(result.Source)}: {ChangeCounts(result)}; see 'skua scripts new'."
            + (result.Failed.Count > 0 ? $" {result.Failed.Count} failed to download: {string.Join(", ", result.Failed)}." : ""),
    };

    /// <summary>e.g. <c>3 new, 12 changed</c>.</summary>
    public static string ChangeCounts(ScriptsUpdateResult result) => $"{result.Added.Count} new, {result.Changed.Count} changed";

    /// <summary>The folder as a tree one level deep: its subfolders with their Script counts, then its Scripts with their descriptions.</summary>
    public static string ScriptsList(ScriptsListResult result)
    {
        string folder = result.Folder.Length == 0 ? "The top" : $"{result.Folder}/";
        StringBuilder text = new($"{folder} in {Source(result.Source)}: {Count(result.Scripts.Count, "Script")}, {Count(result.Folders.Count, "folder")}");
        List<string> lines =
        [
            .. result.Folders.Select(f => $"{Leaf(f.Path)}/  {Count(f.Scripts, "Script")}"),
            .. result.Scripts.Select(s => s.Description is { } description ? $"{Leaf(s.Path)}  {Clip(description, 100)}" : Leaf(s.Path)),
        ];
        for (int i = 0; i < lines.Count; i++)
            text.AppendLine().Append(i == lines.Count - 1 ? "└── " : "├── ").Append(lines[i]);
        return text.ToString();
    }

    public static string ScriptsNew(ScriptsNewResult result)
    {
        string since = $"since {Time(result.Since)}";
        // A window reaching back before the record starts can't claim that nothing changed.
        string? unknown = result.HistoryFrom is not { } from ? $"No history of {Source(result.Source)} yet; 'skua scripts update' starts it."
            : from > result.Since ? $"No history of {Source(result.Source)} yet from before {Time(from)}" : null;
        if (result.Scripts.Count == 0)
            return unknown is null ? $"No Scripts were added or changed by updates from {Source(result.Source)} {since}."
                : result.HistoryFrom is null ? unknown : $"{unknown}; no Scripts were added or changed after.";

        int added = result.Scripts.Count(s => s.Change == ScriptChange.Added);
        string[] by = [.. result.Updates > 0 ? [Count(result.Updates, "update")] : Array.Empty<string>(),
            .. result.Commits > 0 ? [$"{Count(result.Commits, "commit")} from its history"] : Array.Empty<string>()];
        StringBuilder text = new($"{added} new, {result.Scripts.Count - added} changed from {Source(result.Source)} {since}, by {string.Join(" and ", by)}:");
        int width = result.Scripts.Max(s => s.Path.Length);
        foreach (NewScriptDto script in result.Scripts)
        {
            string change = script.Change == ScriptChange.Added ? "new" : "changed";
            string commit = script.Commit.Length > 7 ? script.Commit[..7] : script.Commit;
            text.AppendLine().Append($"  {script.Path.PadRight(width)}  {change,-7}  {Time(script.At)}  {commit}  {script.Name}".TrimEnd());
        }
        if (unknown is not null)
            text.AppendLine().Append($"{unknown}.");
        return text.ToString();
    }

    private static string Time(DateTimeOffset time) => $"{time.ToLocalTime():yyyy-MM-dd HH:mm}";

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string Clip(string text, int max)
    {
        string line = text.ReplaceLineEndings(" ");
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    /// <summary>Only the path, so a script can use it.</summary>
    public static string Screenshot(ScreenshotFile file) => file.Path;

    public static string Engine(EngineStateDto engine) => engine.State switch
    {
        EngineState.Running when engine.Compatible == false =>
            $"Engine '{engine.Name}' is running{InApp(engine)} (pid {engine.Pid}, build {engine.Build}) on protocol {engine.Protocol}, not {ControlProtocol.Version}; "
            + (engine.Host == EngineHost.App ? "quit the app." : "run 'skua engine stop'."),
        EngineState.Running when engine.Build != ControlProtocol.Build =>
            $"Engine '{engine.Name}' is running{InApp(engine)} (pid {engine.Pid}, build {engine.Build}, another build than this skua's {ControlProtocol.Build}); "
            + (engine.Host == EngineHost.App ? "quitting the app replaces it." : "a skua command that drives the game replaces it once it's idle."),
        EngineState.Running => $"Engine '{engine.Name}' is running{InApp(engine)} (pid {engine.Pid}, build {engine.Build}).",
        EngineState.StartingOrHung => $"Engine '{engine.Name}' is starting or hung; its socket {engine.Socket} doesn't answer.",
        _ => $"Engine '{engine.Name}' is stopped.",
    };

    private static string InApp(EngineStateDto engine) => engine.Host == EngineHost.App ? " in the Skua app" : "";

    public static string ScriptSource(ScriptSourceResult result) => result.IsDefault
        ? $"{Source(result.Source)} (the default)"
        : $"{Source(result.Source)} (set in Skua.settings.json; the default is {Source(result.Default)}, which 'skua scripts source --default' restores)";

    public static string ScriptSourceChanged(ScriptSourceResult result) =>
        $"Now fetching Scripts from {Source(result.Source)}{(result.IsDefault ? " (the default)" : "")}. Unless the Scripts were last synced from it, "
        + "the next 'skua scripts update' downloads every Script.";

    private static string Source(ScriptSourceDto source) => ScriptSourceSetting.Format(source);


    /// <summary>
    /// A header with the run's Script and length, then each source with what it paid (+) and what its turn-ins took (−), and the net per item,
    /// each with its rate an hour.
    /// </summary>
    public static string Gains(GainsResult result)
    {
        StringBuilder text = new($"Run {result.Run}{(result.Script is { } script ? $", {script}" : "")}: {Duration(result.DurationSec)} in {result.File}\n");
        foreach (GainSource source in result.Sources)
        {
            text.Append('\n').AppendLine(source.Kind == GainSourceKind.Quest
                ? $"{source.Name} (quest {source.QuestId}): {Count(source.TurnIns!.Value, "turn-in")}{(source.TurnInsPerHour is { } rate ? $", {rate:0}/h" : "")}"
                : source.Name);
            foreach (ItemCount item in source.Gained)
                text.AppendLine(GainRow(item.ItemId, item.Name, item.Qty, item.PerHour));
            foreach (ItemCount item in source.Spent)
                text.AppendLine(GainRow(item.ItemId, item.Name, -item.Qty, -item.PerHour));
        }
        text.Append("\nNet");
        foreach (ItemGains item in result.Items.Where(i => i.Net != 0))
            text.Append('\n').Append(GainRow(item.ItemId, item.Name, item.Net, item.NetPerHour));
        return text.ToString();
    }

    private static string GainRow(int itemId, string? name, long qty, double? perHour)
    {
        string rate = perHour is { } value ? $"{value:+0;-0;0}/h" : "";
        return $"  {qty,8:+0;-0;0}  {rate,10}  {name ?? $"item {itemId}"}";
    }

    public const string GapNotice = "gap: entries after the cursor are no longer held (evicted, or the Engine restarted).";

    public static string Logs(LogPage page)
    {
        StringBuilder text = new();
        if (page.Gap)
            text.AppendLine($"-- {GapNotice}");
        foreach (LogEntryDto entry in page.Entries)
            text.AppendLine(Entry(entry));
        return text.Append($"-- next {page.Next}").ToString();
    }

    /// <summary>
    /// One line: seq, UTC time, kind and run, then the text, the event type and its data, or a game message as <c>[channel] from→to: text</c>.
    /// </summary>
    public static string Entry(LogEntryDto entry)
    {
        string time = DateTimeOffset.FromUnixTimeMilliseconds(entry.Ts).UtcDateTime.ToString("HH:mm:ss.fff");
        string run = entry.Run is { } number ? $" run {number}" : "";
        string body = entry.Kind == LogKind.Game && entry.Data is { } message ? GameMessage(message, entry.Text)
            : entry.Text ?? $"{entry.Type} {JsonSerializer.Serialize(entry.Data, ControlJson.Options)}";
        return $"{entry.Seq} {time} {Name(entry.Kind)}{run}{(entry.Truncated ? " (truncated)" : "")} {body}";
    }

    private static string GameMessage(JsonElement data, string? text)
    {
        string? Field(string name) => data.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
        string sender = Field("from") is { } from ? $" {from}{(Field("to") is { } to ? $"→{to}" : "")}:" : "";
        return $"[{Field("channel")}]{sender} {text}";
    }

    internal static string Name<TEnum>(TEnum value) where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
