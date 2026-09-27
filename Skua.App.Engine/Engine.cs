using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Skua.Control;
using StreamJsonRpc;

namespace Skua.App.Engine;

/// <summary>
/// One Engine process: it holds the lock, serves the Control Surface on the socket and owns the Game Host until it shuts down.
/// </summary>
internal sealed class Engine : IEngineRpc
{
    private readonly EngineEndpoint _endpoint;
    private readonly GameHostSupervisor _gameHost;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<JsonRpc, byte> _connections = new();

    private Engine(EngineEndpoint endpoint, GameHostSupervisor gameHost)
    {
        _endpoint = endpoint;
        _gameHost = gameHost;
    }

    public static string Build { get; } =
        typeof(Engine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static async Task<int> RunAsync(EngineEndpoint endpoint, bool detach)
    {
        string engineDir = Path.GetDirectoryName(endpoint.SocketPath)!;
        if (!Directory.Exists(engineDir))
            Directory.CreateDirectory(engineDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using EngineLock? engineLock = EngineLock.TryAcquire(endpoint.LockPath);
        if (engineLock is null)
        {
            // An auto-start that lost the race stays quiet; its client connects to the running Engine.
            if (!detach)
                Console.Error.WriteLine($"Engine '{endpoint.Name}' is already running; it holds {endpoint.LockPath}.");
            return EngineExitCodes.AlreadyRunning;
        }

        if (detach)
            Detach.RedirectStdio(endpoint.LogPath);
        Trace.Listeners.Add(new ConsoleTraceListener(useErrorStream: true));

        GameHostSupervisor gameHost;
        try
        {
            gameHost = GameHostSupervisor.FromEnvironment();
        }
        catch (FileNotFoundException e)
        {
            EngineLog.Write(e.Message);
            return EngineExitCodes.GameHostMissing;
        }

        using (gameHost)
        {
            using ServiceProvider services = EngineServices.Build();
            Engine engine = new(endpoint, gameHost);
            await engine.ServeAsync();
        }

        return EngineExitCodes.Success;
    }

    public Task<HelloResult> HelloAsync(int protocol, CancellationToken cancellationToken) =>
        Task.FromResult(new HelloResult(ControlProtocol.Version, Build, _endpoint.Name, Environment.ProcessId));

    public Task<StatusDto> StatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new StatusDto(
            new EngineInfoDto(_endpoint.Name, Build, ControlProtocol.Version, Math.Round(_uptime.Elapsed.TotalSeconds, 1), Environment.ProcessId),
            _gameHost.Status()));

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        EngineLog.Write("Shutdown requested over the Control Surface.");
        _shutdown.Cancel();
        return Task.CompletedTask;
    }

    private async Task ServeAsync()
    {
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using PosixSignalRegistration sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

        File.Delete(_endpoint.SocketPath);
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(_endpoint.SocketPath));
            File.SetUnixFileMode(_endpoint.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen();
            EngineLog.Write($"Engine '{_endpoint.Name}' (build {Build}, protocol {ControlProtocol.Version}) listening on {_endpoint.SocketPath}.");

            await AcceptAsync(listener);
        }
        finally
        {
            listener.Dispose();
            Stop();
        }
    }

    private async Task AcceptAsync(Socket listener)
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            JsonRpc rpc = ControlJson.CreateRpc(socket);
            rpc.AddLocalRpcTarget<IEngineRpc>(this, null);
            rpc.Disconnected += (_, _) => _connections.TryRemove(rpc, out _);
            _connections[rpc] = 0;
            rpc.StartListening();
        }
    }

    /// <summary>The shutdown order: the Game Host closes first (without logging out), then the connections, then the socket; the lock goes last.</summary>
    private void Stop()
    {
        EngineLog.Write("Shutting down.");
        _gameHost.Stop();
        foreach (JsonRpc rpc in _connections.Keys)
            rpc.Dispose();
        File.Delete(_endpoint.SocketPath);
        EngineLog.Write("Stopped.");
    }

    private void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        EngineLog.Write($"Shutdown requested by {context.Signal}.");
        _shutdown.Cancel();
    }
}
