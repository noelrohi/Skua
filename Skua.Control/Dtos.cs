namespace Skua.Control;

public sealed record HelloResult(int Protocol, string Build, string EngineName, int Pid);

/// <summary>The reply to <c>status</c>. Fields that don't apply yet are null.</summary>
public sealed record StatusDto(EngineInfoDto Engine, GameStatusDto Game);

public sealed record EngineInfoDto(string Name, string Build, int Protocol, double UptimeSec, int Pid);

/// <param name="GameHostUp">Whether the Game Host process is running.</param>
/// <param name="State">The game state; <see cref="GameState.NotStarted"/> while the Game Host is down, and null while it isn't tracked yet.</param>
/// <param name="Server">The server the player is on, or null when not logged in.</param>
public sealed record GameStatusDto(bool GameHostUp, GameState? State, string? Server);

public enum GameState
{
    NotStarted,
    LoginScreen,
    LoggingIn,
    Playing,
    Disconnected,
}

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
/// <param name="Png">The PNG image; base64 on the wire.</param>
/// <param name="Frame">An estimate of the Game Client's frame number when it was captured.</param>
public sealed record ScreenshotResult(byte[] Png, int Width, int Height, long Frame);

/// <summary>The reply to <c>scripts_update</c>.</summary>
/// <param name="Commit">The Script Source commit the Scripts are now synced to.</param>
/// <param name="Downloaded">How many Script files were downloaded.</param>
/// <param name="Failed">The paths of Scripts that failed to download; the next update retries them.</param>
public sealed record ScriptsUpdateResult(ScriptSourceDto Source, ScriptsUpdateMode Mode, string Commit, int Downloaded, IReadOnlyList<string> Failed);
