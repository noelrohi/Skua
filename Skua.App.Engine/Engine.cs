using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Engine.Logging;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.MacOS.GameHost;
using StreamJsonRpc;

namespace Skua.App.Engine;

/// <summary>
/// One Engine process: it holds the lock, serves the Control Surface on the socket and owns the Game Host until it shuts down.
/// </summary>
internal sealed class Engine : IEngineRpc
{
    /// <summary>Extra secrets to redact from every log, one per line; for development and tests.</summary>
    public const string RedactVariable = "SKUA_REDACT";

    private static readonly TimeSpan LockRetry = TimeSpan.FromSeconds(1);

    private readonly EngineEndpoint _endpoint;
    private readonly GameHostSupervisor _gameHost;
    private readonly EngineLogs _logs;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<JsonRpc, byte> _connections = new();
    private readonly ScriptSourceOperations _scriptSource;

    private Engine(EngineEndpoint endpoint, GameHostSupervisor gameHost, EngineLogs logs, IGetScriptsService scriptsService)
    {
        _endpoint = endpoint;
        _gameHost = gameHost;
        _logs = logs;
        _scriptSource = new ScriptSourceOperations(scriptsService, _shutdown.Token);
    }

    public static string Build { get; } =
        typeof(Engine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static async Task<int> RunAsync(EngineEndpoint endpoint, bool detach)
    {
        foreach (string dir in new[] { endpoint.EnginesDir, Path.GetDirectoryName(endpoint.SocketPath)! })
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using EngineLock? engineLock = await AcquireLockAsync(endpoint);
        if (engineLock is null)
        {
            // An auto-start that lost the race stays quiet; its client connects to the running Engine.
            if (!detach)
                Console.Error.WriteLine($"Engine '{endpoint.Name}' is already running; it holds {endpoint.LockPath}.");
            return EngineExitCodes.AlreadyRunning;
        }

        if (detach)
            Detach.RedirectStdio(endpoint.LogPath);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        using EngineLogs logs = new(started, LogFile.Open(endpoint.LogFilesDir, started));
        foreach (string secret in (Environment.GetEnvironmentVariable(RedactVariable) ?? "").Split('\n'))
            logs.AddSecret(secret);
        EngineLog.Attach(logs);
        logs.Event(EventTypes.EngineStarted, new { name = endpoint.Name, build = Build, protocol = ControlProtocol.Version, pid = Environment.ProcessId });

        GameHostLaunch launch;
        try
        {
            launch = GameHostLaunch.Resolve(AppContext.BaseDirectory);
        }
        catch (FileNotFoundException e)
        {
            EngineLog.Write(e.Message);
            return EngineExitCodes.GameHostMissing;
        }

        // Like the Windows app, the Engine never disposes Core's singletons: they stop with the process, and their Dispose paths throw.
        ServiceProvider services = EngineServices.Build(launch, logs);
        using (GameHostSupervisor gameHost = GameHostSupervisor.Start(services, logs))
        {
            Engine engine = new(endpoint, gameHost, logs, services.GetRequiredService<IGetScriptsService>());
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

    public Task<ScriptsSearchResult> ScriptsSearchAsync(string query, string? tag, CancellationToken cancellationToken) =>
        _scriptSource.SearchAsync(query, tag, cancellationToken);

    public Task<ScriptsUpdateResult> ScriptsUpdateAsync(CancellationToken cancellationToken) =>
        _scriptSource.UpdateAsync();

    public Task<LogPage> LogsAsync(LogKind kind, string? after, int? max, CancellationToken cancellationToken) =>
        Task.FromResult(_logs.Read([kind], after, max));

    public async IAsyncEnumerable<LogPage> SubscribeAsync(
        LogKind[] kinds, string? after, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = after;
        while (true)
        {
            Task appended = _logs.NextAppend;
            LogPage page = _logs.Read(kinds, cursor, EngineLogs.MaxMax);
            cursor = page.Next;
            if (page.Entries.Count > 0 || page.Gap)
                yield return page;
            else
                await appended.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Takes the lock, or returns null when another Engine holds it. A client checking the lock holds it for an instant,
    /// so a failed attempt is retried for a moment unless a running Engine already answers on the socket.
    /// </summary>
    private static async Task<EngineLock?> AcquireLockAsync(EngineEndpoint endpoint)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            if (EngineLock.TryAcquire(endpoint.LockPath) is { } acquired)
                return acquired;
            if (waited.Elapsed > LockRetry || await SocketAnswersAsync(endpoint.SocketPath))
                return null;
            await Task.Delay(50);
        }
    }

    private static async Task<bool> SocketAnswersAsync(string socketPath)
    {
        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task ServeAsync()
    {
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using PosixSignalRegistration sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

        File.Delete(_endpoint.SocketPath);
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            // A umask of 0177 makes bind create the socket as 0600, so others never reach it, even in a shared directory.
            int umask = Umask.Set(0b001_111_111);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(_endpoint.SocketPath));
            }
            finally
            {
                Umask.Set(umask);
            }
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
        _gameHost.Dispose();
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
