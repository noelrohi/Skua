namespace Skua.Control;

/// <summary>The reply to <c>hello</c>.</summary>
/// <remarks><c>Host</c> is null from an Engine older than protocol 10: <c>skua-engine</c>, or an early Mac App that <c>shutdown</c> still stopped.</remarks>
public sealed record HelloResult(int Protocol, string Build, string EngineName, int Pid, EngineHost? Host = null);

/// <summary>Who hosts an Engine (ADR 0006): only <c>skua-engine</c>'s may be stopped over the Control Surface.</summary>
public enum EngineHost
{
    /// <summary><c>skua-engine</c>, which the CLI and MCP auto-start and <c>skua engine stop</c> stops.</summary>
    Engine,

    /// <summary>The Mac App, which owns it: only quitting the app stops it.</summary>
    App,
}

/// <summary>The reply to <c>status</c>. Fields that don't apply yet are null.</summary>
/// <param name="PendingDialogs">The pending Questions, oldest first, as <c>dialogs</c> lists them.</param>
public sealed record StatusDto(EngineInfoDto Engine, GameStatusDto Game, ScriptStatusDto Script, IReadOnlyList<QuestionDto> PendingDialogs);

public sealed record EngineInfoDto(string Name, string Build, int Protocol, double UptimeSec, int Pid, EngineHost Host);

/// <param name="GameHostUp">Whether the Game Host process is running.</param>
/// <param name="State">The game state, as the <c>game.state</c> events report it.</param>
/// <param name="Server">The server the player is on, or null when not playing.</param>
/// <param name="Player">
/// A summary of the player, or null when not playing; while playing, null when the game didn't answer in time (or the read failed) and there is no earlier
/// reading of this login.
/// </param>
/// <param name="PlayerAgeSec">
/// How many seconds ago <paramref name="Player"/> was read, when the game didn't answer in time (or the read failed) and it is the last reading; null when it is fresh.
/// </param>
public sealed record GameStatusDto(bool GameHostUp, GameState State, string? Server, PlayerDto? Player = null, double? PlayerAgeSec = null);

/// <summary>The player, as <c>status</c> summarises it.</summary>
/// <param name="Class">The equipped class, or null when none is.</param>
/// <param name="InCombat">Whether the player is fighting.</param>
/// <param name="Xp">The XP earned toward the next level.</param>
/// <param name="RequiredXp">The XP the next level needs; 0 when there is no next level.</param>
/// <param name="XpPercent"><see cref="Xp"/> as a percentage of <see cref="RequiredXp"/>, to one decimal; null when there is no next level.</param>
public sealed record PlayerDto(
    string Name, int Level, string? Class, int Hp, int MaxHp, int Mp, int MaxMp, int Gold, string Map, string Cell, string Pad, bool Alive, bool InCombat,
    int Xp, int RequiredXp, double? XpPercent)
{
    /// <summary><paramref name="xp"/> as a percentage of <paramref name="requiredXp"/>, to one decimal; null when there is no next level.</summary>
    public static double? Percent(int xp, int requiredXp) =>
        requiredXp > 0 ? Math.Round(100.0 * xp / requiredXp, 1, MidpointRounding.AwayFromZero) : null;
}

public enum GameState
{
    /// <summary>There is no Game Host, or the Game Client hasn't loaded yet.</summary>
    NotStarted,

    /// <summary>The Game Client has loaded and nobody is logged in; either nobody was yet, or the last login ended deliberately.</summary>
    LoginScreen,

    /// <summary>A login or relogin is in flight, or the player is logged in but the world hasn't loaded yet.</summary>
    LoggingIn,

    /// <summary>Logged in with the world loaded, alive or dead.</summary>
    Playing,

    /// <summary>The Test Account was disconnected without a deliberate logout; it stays so until a login, a relogin or a logout.</summary>
    Disconnected,
}

/// <summary>A game server.</summary>
/// <param name="PlayerCount">How many players are on it now.</param>
/// <param name="MemberOnly">Whether only members may play on it.</param>
/// <param name="Language">Its language code, e.g. <c>en</c> or <c>pt</c>.</param>
public sealed record ServerDto(string Name, bool Online, int PlayerCount, int MaxPlayers, bool MemberOnly, string Language);

/// <summary>The reply to <c>servers</c>.</summary>
public sealed record ServersResult(IReadOnlyList<ServerDto> Servers);

/// <summary>The reply to <c>login</c>.</summary>
/// <param name="Server">The server the account is playing on.</param>
/// <param name="AlreadyLoggedIn">Whether it was already playing there, so nothing was done.</param>
/// <param name="Username">The username of the account that logged in, from Keychain.</param>
/// <param name="IsTestAccount">
/// Whether that account is the Test Account: the active one by default, and the one an agent's login falls back to unless the active account
/// allows agents.
/// </param>
public sealed record LoginResult(string Server, bool AlreadyLoggedIn, string Username, bool IsTestAccount);

/// <summary>The reply to <c>logout</c>.</summary>
/// <param name="WasLoggedIn">Whether the Test Account was logged in; a logout at the login screen does nothing.</param>
public sealed record LogoutResult(bool WasLoggedIn);

/// <summary>The data of a JSON-RPC error raised by the Engine.</summary>
/// <param name="Diagnostics">The compiler's errors, one per line, for <see cref="ErrorCode.CompileFailed"/>; else null.</param>
public sealed record ErrorDataDto(ErrorCode Code, IReadOnlyList<string>? Diagnostics = null);

/// <summary>The repository the Engine fetches Scripts from: <c>owner/repo@branch</c>.</summary>
public sealed record ScriptSourceDto(string Owner, string Repo, string Branch);

/// <summary>The reply to <c>scripts_source</c> and <c>scripts_source_set</c>.</summary>
/// <param name="Source">The Script Source the Engine fetches Scripts from.</param>
/// <param name="IsDefault">Whether the setting is unset, so <see cref="Source"/> is <see cref="Default"/>.</param>
/// <param name="Default">The Script Source the Engine uses when the setting is unset.</param>
public sealed record ScriptSourceResult(ScriptSourceDto Source, bool IsDefault, ScriptSourceDto Default);

/// <summary>A Script in the Script Source.</summary>
/// <param name="Path">The Script's path in the Script Source, which identifies it, e.g. <c>Farm/Leveling.cs</c>.</param>
/// <param name="Name">The Script's name from <c>scripts.json</c>, or null when it has none.</param>
/// <param name="Downloaded">Whether the Script's file is on disk.</param>
/// <param name="Outdated">Whether the file on disk differs from the Script Source's current version.</param>
public sealed record ScriptDto(string Path, string? Name, string? Description, IReadOnlyList<string> Tags, bool Downloaded, bool Outdated);

/// <summary>The reply to <c>scripts_search</c>.</summary>
/// <param name="Matched">How many Scripts matched; <see cref="Scripts"/> holds at most <see cref="MaxScripts"/> of them.</param>
public sealed record ScriptsSearchResult(ScriptSourceDto Source, int Matched, IReadOnlyList<ScriptDto> Scripts)
{
    public const int MaxScripts = 100;
}

public enum ScriptsUpdateMode
{
    /// <summary>The first sync from this Script Source: every missing or outdated Script was downloaded.</summary>
    Full,

    /// <summary>Only the Scripts changed since the last synced commit were downloaded.</summary>
    Incremental,

    /// <summary>The Script Source hasn't changed since the last sync.</summary>
    UpToDate,
}

/// <summary>The reply to <c>screenshot</c>.</summary>
/// <param name="Frame">An estimate of the Game Client's frame number when it was captured.</param>
/// <param name="Png">The PNG image; base64 on the wire.</param>
public sealed record ScreenshotResult(int Width, int Height, long Frame, byte[] Png);

/// <summary>The reply to <c>scripts_update</c>.</summary>
/// <param name="Commit">The Script Source commit the Scripts are now synced to.</param>
/// <param name="Downloaded">How many Script files were downloaded.</param>
/// <param name="Failed">The paths of Scripts that failed to download; the next update retries them.</param>
/// <param name="Added">The paths of the downloaded Scripts that weren't on disk before.</param>
/// <param name="Changed">The paths of the downloaded Scripts that replaced an older copy on disk.</param>
public sealed record ScriptsUpdateResult(
    ScriptSourceDto Source, ScriptsUpdateMode Mode, string Commit, int Downloaded, IReadOnlyList<string> Failed, IReadOnlyList<string> Added, IReadOnlyList<string> Changed);

/// <summary>A folder of the Script Source.</summary>
/// <param name="Path">Its path in the Script Source, e.g. <c>Farm/Special</c>.</param>
/// <param name="Scripts">How many Scripts it holds, with its subfolders'.</param>
public sealed record ScriptFolderDto(string Path, int Scripts);

/// <summary>The reply to <c>scripts_list</c>: one folder of the Script Source.</summary>
/// <param name="Folder">The folder's path as the Script Source spells it; empty for the top.</param>
/// <param name="Folders">Its subfolders, by path.</param>
/// <param name="Scripts">The Scripts directly in it, by path.</param>
public sealed record ScriptsListResult(ScriptSourceDto Source, string Folder, IReadOnlyList<ScriptFolderDto> Folders, IReadOnlyList<ScriptDto> Scripts);

public enum ScriptChange
{
    /// <summary>The update downloaded a Script that wasn't on disk, or the Script Source commit added it.</summary>
    Added,

    /// <summary>The update replaced a Script on disk with a newer version, or the Script Source commit changed it.</summary>
    Changed,
}

/// <summary>A Script that a Script Source update, or a commit from the Script Source's history, added or changed.</summary>
/// <param name="Name">Its name from <c>scripts.json</c> at the time, or null when it had none.</param>
/// <param name="Change">Added if any update or commit in the window added it, else changed.</param>
/// <param name="At">When the last update in the window that touched it ran, or when the last such commit was made.</param>
/// <param name="Commit">The Script Source commit that update synced to, or that commit.</param>
public sealed record NewScriptDto(string Path, string? Name, ScriptChange Change, DateTimeOffset At, string Commit);

/// <summary>The reply to <c>scripts_new</c>.</summary>
/// <param name="Since">Where the window starts: updates after this time count.</param>
/// <param name="Updates">How many updates in the window added or changed Scripts.</param>
/// <param name="Scripts">The Scripts they added or changed, the latest first.</param>
/// <param name="Commits">
/// How many commits in the window, from the Script Source's history that the first full download read from GitHub, added or changed Scripts.
/// </param>
/// <param name="HistoryFrom">
/// When the Engine's record of the Script Source starts, or null when it has none yet. A window that starts earlier can't show what changed
/// before it; it is the first full download's time when that couldn't read the Script Source's history.
/// </param>
public sealed record ScriptsNewResult(
    ScriptSourceDto Source, DateTimeOffset Since, int Updates, IReadOnlyList<NewScriptDto> Scripts, int Commits, DateTimeOffset? HistoryFrom);

/// <summary>The reply to <c>join</c> and <c>jump</c>: where the player ended up.</summary>
/// <param name="Map">The map's name, without a room number.</param>
/// <param name="AlreadyThere">Whether the player was already there, so nothing was done.</param>
public sealed record LocationResult(string Map, string Cell, string Pad, bool AlreadyThere);

/// <summary>Which of the player's item stores <c>inventory</c> lists.</summary>
public enum InventoryKind
{
    Inventory,

    /// <summary>The bank, which the Engine loads from the game server the first time it is listed after each login.</summary>
    Bank,

    /// <summary>The temporary inventory, which holds quest items and has no slot limit.</summary>
    Temp,

    House,
}

/// <summary>An item in one of the player's item stores.</summary>
/// <param name="Id">The item's ID, the same for every copy of the item.</param>
/// <param name="Category">The game's category, e.g. <c>Sword</c>, <c>Class</c> or <c>Quest Item</c>.</param>
/// <param name="EnhancementLevel">The enhancement level, or null for a temporary item, which has none.</param>
public sealed record ItemDto(int Id, string Name, int Qty, int MaxStack, string Category, bool Equipped, int? EnhancementLevel);

/// <summary>The reply to <c>inventory</c>.</summary>
/// <param name="TotalSlots">How many slots the store has, or null for the temporary inventory, which has no limit.</param>
public sealed record InventoryResult(InventoryKind Kind, int UsedSlots, int? TotalSlots, IReadOnlyList<ItemDto> Items);

/// <summary>Which quests <c>quests</c> lists.</summary>
public enum QuestFilter
{
    /// <summary>Every quest the Game Client has loaded, accepted or not.</summary>
    Loaded,

    /// <summary>Only the accepted quests.</summary>
    Active,
}

public enum QuestStatus
{
    NotAccepted,
    InProgress,

    /// <summary>Accepted, with every requirement met.</summary>
    Completable,
}

/// <summary>An item a quest needs to be turned in.</summary>
/// <param name="Qty">How many the quest needs.</param>
/// <param name="Have">How many the player has, in the inventory or, for a temporary item, the temporary inventory.</param>
/// <param name="IdleSec">
/// How many seconds since the Engine saw <paramref name="Have"/> rise, or since it began watching the requirement if it hasn't; a fall (a turn-in, or
/// temporary items lost to a relogin) is no rise. It watches the loaded quests while playing, and starts afresh when another account logs in.
/// </param>
/// <param name="GainPerHour">How much <paramref name="Have"/> rose per hour over the last hour watched; null until it has watched for 5 minutes.</param>
public sealed record QuestRequirementDto(int ItemId, string Name, int Qty, int Have, bool Temp, double? IdleSec = null, double? GainPerHour = null);

/// <summary>An item a quest can reward.</summary>
public sealed record QuestRewardDto(int ItemId, string Name, int Qty);

public sealed record QuestDto(
    int Id, string Name, QuestStatus Status, bool MemberOnly, int Gold, int Xp, IReadOnlyList<QuestRequirementDto> Requirements, IReadOnlyList<QuestRewardDto> Rewards);

/// <summary>The reply to <c>quests</c>.</summary>
public sealed record QuestsResult(QuestFilter Filter, IReadOnlyList<QuestDto> Quests);

/// <summary>Another player, or the player, on the map.</summary>
/// <param name="Name">The player's name, in lower case as the game keys it.</param>
public sealed record MapPlayerDto(string Name, int Level, string Cell, string Pad, int Hp, int MaxHp, bool Afk);

/// <summary>A monster on the map.</summary>
/// <param name="Id">The kind of monster, the same on every map.</param>
/// <param name="MapId">This monster on this map, which targets it.</param>
public sealed record MonsterDto(int Id, int MapId, string Name, string Cell, int Hp, int MaxHp, bool Alive);

/// <summary>The reply to <c>map</c>.</summary>
/// <param name="RoomId">The game's ID for this instance of the map.</param>
/// <param name="Cells">The map's cells, which <c>jump</c> takes.</param>
public sealed record MapDto(string Name, int RoomId, IReadOnlyList<string> Cells, IReadOnlyList<MapPlayerDto> Players, IReadOnlyList<MonsterDto> Monsters);

/// <summary>An item dropped for the player since the login and not yet picked up.</summary>
/// <param name="Qty">How many dropped, over all the drops of the item.</param>
public sealed record DropDto(int Id, string Name, int Qty);

/// <summary>The reply to <c>drops</c>.</summary>
public sealed record DropsResult(IReadOnlyList<DropDto> Drops);

/// <summary>The reply to <c>chat_send</c>: what was sent.</summary>
/// <param name="Channel"><c>zone</c> or <c>whisper</c>.</param>
/// <param name="To">The whispered player, or null for zone chat.</param>
public sealed record ChatSendResult(string Channel, string? To, string Text);

/// <summary>One run of a Hook, as the Hook Runner reports it to <c>hook_ran</c> and the <c>hook.ran</c> event records it.</summary>
/// <param name="Hook">The Hook's file name: the event type it ran for.</param>
/// <param name="EventSeq">The seq of the event it ran for.</param>
/// <param name="StartedAt">When it started, in UTC milliseconds since the Unix epoch.</param>
/// <param name="DurationMs">How long it ran.</param>
/// <param name="ExitCode">Its exit code; null when it couldn't be started.</param>
/// <param name="Output">The tail of its stdout and stderr, interleaved; or why it couldn't be started.</param>
public sealed record HookRunDto(string Hook, long EventSeq, long StartedAt, long DurationMs, int? ExitCode, string Output);
