using Skua.Control;

namespace Skua.Engine;

/// <summary>
/// An Engine running in this process: it holds the lock, owns the Game Host and serves the Control Surface on its socket until it stops,
/// whether through <see cref="StopAsync"/> or a <c>shutdown</c> request.
/// </summary>
public sealed class HostedEngine : IAsyncDisposable
{
    private readonly Action _stop;

    internal HostedEngine(EngineEndpoint endpoint, IServiceProvider services, Task<int> completion, Action stop)
    {
        Endpoint = endpoint;
        Services = services;
        Completion = completion;
        _stop = stop;
    }

    public EngineEndpoint Endpoint { get; }

    /// <summary>The Engine's container, which <c>Ioc.Default</c> also serves.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Completes with the exit code once the Engine has stopped and let go of its socket and lock.</summary>
    public Task<int> Completion { get; }

    /// <summary>
    /// Starts an Engine for <paramref name="endpoint"/>. It has bound its socket when this returns, so Control Surfaces can connect.
    /// </summary>
    /// <exception cref="EngineStartException">Another Engine holds the name, or the Game Host is missing.</exception>
    public static Task<HostedEngine> StartAsync(EngineEndpoint endpoint, EngineHostOptions options) => Engine.StartAsync(endpoint, options);

    /// <summary>Stops the Engine as a <c>shutdown</c> request does, and waits until it has.</summary>
    public async Task<int> StopAsync()
    {
        _stop();
        return await Completion;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
