using Skua.Control;

namespace Skua.App.Cli;

public enum EngineState
{
    Running,
    Stopped,
    StartingOrHung,
}

/// <summary>What <c>skua engine start|stop|status</c> report.</summary>
/// <param name="Compatible">Whether the running Engine speaks this CLI's protocol version; null when none is running.</param>
/// <param name="Host">Who hosts the running Engine; null when none is running, or for an Engine older than protocol 10.</param>
public sealed record EngineStateDto(string Name, EngineState State, int? Pid, string? Build, int? Protocol, bool? Compatible, string Socket, EngineHost? Host);

/// <summary><c>skua engine start|stop|status</c>: the lifetime of <c>skua-engine</c>, which only the CLI controls; the Mac App's Engine stops with the app.</summary>
internal static class EngineCommands
{
    /// <summary>Long enough for the Engine to stop a Script cooperatively and close the Game Host.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    public static async Task<EngineStateDto> StartAsync(EngineClientOptions options, CancellationToken cancellationToken)
    {
        using EngineConnection connection = await EngineClient.ConnectAsync(options, cancellationToken);
        return Running(options.Endpoint, connection);
    }

    public static async Task<EngineStateDto> StopAsync(EngineClientOptions options, CancellationToken cancellationToken)
    {
        await EngineClient.StopAsync(options.Endpoint, StopTimeout, cancellationToken);
        return NotRunning(options.Endpoint, EngineState.Stopped);
    }

    public static async Task<EngineStateDto> StatusAsync(EngineClientOptions options, CancellationToken cancellationToken)
    {
        using EngineConnection? connection = await EngineClient.TryConnectAsync(options.Endpoint, cancellationToken);
        if (connection is not null)
            return Running(options.Endpoint, connection);
        return NotRunning(options.Endpoint, EngineLock.IsHeld(options.Endpoint.LockPath) ? EngineState.StartingOrHung : EngineState.Stopped);
    }

    private static EngineStateDto Running(EngineEndpoint endpoint, EngineConnection connection) =>
        new(endpoint.Name, EngineState.Running, connection.Hello.Pid, connection.Hello.Build, connection.Hello.Protocol, connection.IsCompatible, endpoint.SocketPath,
            connection.Hello.Host);

    private static EngineStateDto NotRunning(EngineEndpoint endpoint, EngineState state) =>
        new(endpoint.Name, state, null, null, null, null, endpoint.SocketPath, null);
}
