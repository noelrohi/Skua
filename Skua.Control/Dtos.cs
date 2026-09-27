namespace Skua.Control;

public sealed record HelloResult(int Protocol, string Build, string EngineName, int Pid);

/// <summary>The reply to <c>status</c>. Fields that don't apply yet are null.</summary>
public sealed record StatusDto(EngineInfoDto Engine, GameStatusDto Game);

public sealed record EngineInfoDto(string Name, string Build, int Protocol, double UptimeSec, int Pid);

/// <param name="GameHostUp">Whether the Game Host process is running.</param>
/// <param name="State">The game state, as the <c>game.state</c> events report it.</param>
/// <param name="Server">The server the player is on, or null when not playing.</param>
public sealed record GameStatusDto(bool GameHostUp, GameState State, string? Server);

public enum GameState
{
    /// <summary>There is no Game Host, or the Game Client hasn't loaded yet.</summary>
    NotStarted,

    /// <summary>The Game Client has loaded and nobody is logged in; either there was no session yet, or it ended deliberately.</summary>
    LoginScreen,

    /// <summary>A login or relogin is in flight, or the player is logged in but the world hasn't loaded yet.</summary>
    LoggingIn,

    /// <summary>Logged in with the world loaded, alive or dead.</summary>
    Playing,

    /// <summary>The session was lost without a deliberate logout; it stays so until a login, a relogin or a logout.</summary>
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
/// <param name="Server">The server the Test Account is playing on.</param>
/// <param name="AlreadyLoggedIn">Whether it was already playing there, so nothing was done.</param>
public sealed record LoginResult(string Server, bool AlreadyLoggedIn);

/// <summary>The reply to <c>logout</c>.</summary>
/// <param name="WasLoggedIn">Whether there was a session to end; a logout at the login screen does nothing.</param>
public sealed record LogoutResult(bool WasLoggedIn);

/// <summary>The data of a JSON-RPC error raised by the Engine.</summary>
public sealed record ErrorDataDto(ErrorCode Code);

/// <summary>The repository the Engine fetches Scripts from: <c>owner/repo@branch</c>.</summary>
public sealed record ScriptSourceDto(string Owner, string Repo, string Branch);

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
public sealed record ScriptsUpdateResult(ScriptSourceDto Source, ScriptsUpdateMode Mode, string Commit, int Downloaded, IReadOnlyList<string> Failed);
