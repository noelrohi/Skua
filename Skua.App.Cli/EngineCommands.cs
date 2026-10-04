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

/// <summary>One Engine of <c>skua engine list</c>.</summary>
/// <param name="Status">Its <c>status</c>; null when it isn't running, speaks another protocol, or didn't answer in time.</param>
public sealed record EngineListEntry(EngineStateDto Engine, StatusDto? Status);

/// <summary>
/// <c>skua engine start|stop|status|list</c>: the lifetime of <c>skua-engine</c>, which only the CLI controls; the Mac App's Engine stops with the app.
/// </summary>
internal static class EngineCommands
{
    /// <summary>Long enough for the Engine to stop a Script cooperatively and close the Game Host.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long one Engine of the list may take over its <c>status</c>, so a busy one doesn't hold up the rest.</summary>
    private static readonly TimeSpan ListStatusTimeout = TimeSpan.FromSeconds(2);

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
        return connection is not null ? Running(options.Endpoint, connection) : NotRunning(options.Endpoint);
    }

    /// <summary>Every Engine in the data folder, by name, without starting any; one whose socket doesn't answer is listed as not running.</summary>
    public static async Task<IReadOnlyList<EngineListEntry>> ListAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll(EngineEndpoint.InDataFolder(EngineEndpoint.DefaultSkuaDir()).Select(endpoint => DescribeAsync(endpoint, cancellationToken)));

    private static async Task<EngineListEntry> DescribeAsync(EngineEndpoint endpoint, CancellationToken cancellationToken)
    {
        using EngineConnection? connection = await EngineClient.TryConnectAsync(endpoint, cancellationToken);
        if (connection is null)
            return new(NotRunning(endpoint), null);
        StatusDto? status = null;
        if (connection.IsCompatible)
        {
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ListStatusTimeout);
                status = await connection.StatusAsync(timeout.Token);
            }
            catch (Exception e) when (e is ControlException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }
        return new(Running(endpoint, connection), status);
    }

    private static EngineStateDto Running(EngineEndpoint endpoint, EngineConnection connection) =>
        new(connection.Hello.EngineName, EngineState.Running, connection.Hello.Pid, connection.Hello.Build, connection.Hello.Protocol, connection.IsCompatible, endpoint.SocketPath,
            connection.Hello.Host);

    /// <summary>An Engine that doesn't answer: starting or hung while its lock is held, else stopped.</summary>
    private static EngineStateDto NotRunning(EngineEndpoint endpoint, EngineState? state = null) =>
        new(endpoint.Name, state ?? (EngineLock.IsHeld(endpoint.LockPath) ? EngineState.StartingOrHung : EngineState.Stopped), null, null, null, null,
            endpoint.SocketPath, null);
}
