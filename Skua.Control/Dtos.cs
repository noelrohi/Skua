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
