using Skua.Control;

namespace Skua.App.Cli;

/// <summary>What <c>skua engine start|stop|status</c> report.</summary>
/// <param name="State"><c>running</c>, <c>stopped</c> or <c>startingOrHung</c>.</param>
/// <param name="Compatible">Whether the running Engine speaks this CLI's protocol version; null when none is running.</param>
public sealed record EngineStateDto(string Name, string State, int? Pid, string? Build, int? Protocol, bool? Compatible, string Socket);

/// <summary><c>skua engine start|stop|status</c>: the Engine's lifetime, which only the CLI controls.</summary>
internal static class EngineCommands
{
    /// <summary>Long enough for the Engine to stop a Script cooperatively and close the Game Host.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    public static async Task<EngineStateDto> StartAsync(EngineClientOptions options, CancellationToken cancellationToken)
    {
        using EngineConnection connection = await EngineClient.ConnectAsync(options, cancellationToken);
        return Running(options.Endpoint, connection.Hello);
    }

    public static async Task<EngineStateDto> StopAsync(EngineClientOptions options, CancellationToken cancellationToken)
    {
        await EngineClient.StopAsync(options.Endpoint, StopTimeout, cancellationToken);
        return NotRunning(options.Endpoint, "stopped");
    }

    public static async Task<EngineStateDto> StatusAsync(EngineClientOptions options, CancellationToken cancellationToken)
    {
        using EngineConnection? connection = await EngineClient.TryConnectAsync(options.Endpoint, cancellationToken);
        if (connection is not null)
            return Running(options.Endpoint, connection.Hello);
        return NotRunning(options.Endpoint, EngineLock.IsHeld(options.Endpoint.LockPath) ? "startingOrHung" : "stopped");
    }

    private static EngineStateDto Running(EngineEndpoint endpoint, HelloResult hello) =>
        new(endpoint.Name, "running", hello.Pid, hello.Build, hello.Protocol, hello.Protocol == ControlProtocol.Version, endpoint.SocketPath);

    private static EngineStateDto NotRunning(EngineEndpoint endpoint, string state) =>
        new(endpoint.Name, state, null, null, null, null, endpoint.SocketPath);
}
