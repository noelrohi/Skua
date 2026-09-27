using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Engine.Game;
using Skua.App.Engine.Logging;
using Skua.App.Engine.Scripts;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.MacOS.GameHost;
using Skua.MacOS.Services;
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
    private readonly ScreenshotOperations _screenshots;

    private readonly GameOperations _game;
    private readonly MoveOperations _moves;
    private readonly GameQueries _queries;
    private readonly ScriptOperations _scripts;
    private readonly EvalOperations _eval;
    private readonly DialogOperations _dialogs;

    private Engine(EngineEndpoint endpoint, GameHostSupervisor gameHost, EngineLogs logs, IServiceProvider services)
    {
        _endpoint = endpoint;
        _gameHost = gameHost;
        _logs = logs;
        IScriptManager manager = services.GetRequiredService<IScriptManager>();
        ScriptDialogBroker broker = services.GetRequiredService<ScriptDialogBroker>();
        _dialogs = new DialogOperations(broker, logs);
        ScriptRuns runs = new(logs, manager, services.GetRequiredService<IScriptOption>(), broker);
        ActionSlot slot = new();
        SemaphoreSlim compiling = new(1, 1);
        _scriptSource = new ScriptSourceOperations(services.GetRequiredService<IGetScriptsService>(), runs, slot, _shutdown.Token);
        _screenshots = new ScreenshotOperations(services.GetRequiredService<BridgeFlashUtil>(), services.GetRequiredService<IScriptOption>());
        GameActionSlot gameSlot = new(gameHost.Tracker, runs, slot);
        _game = new GameOperations(
            services.GetRequiredService<IScriptServers>(), services.GetRequiredService<IFlashUtil>(), services.GetRequiredService<ISettingsService>(), logs,
            gameHost.Tracker, gameSlot);
        _moves = new MoveOperations(
            services.GetRequiredService<IScriptMap>(), services.GetRequiredService<IScriptPlayer>(), services.GetRequiredService<IScriptWait>(), gameHost.Tracker, gameSlot);
        _queries = new GameQueries(services.GetRequiredService<IScriptInterface>(), services.GetRequiredService<IFlashUtil>(), gameHost.Tracker, gameSlot);
        _scripts = new ScriptOperations(manager, runs, broker, slot, compiling);
        _eval = new EvalOperations(manager, services.GetRequiredService<IScriptInterface>(), logs, compiling);
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
        (LogFile? file, string? fileError) = OpenLogFile(endpoint, started);
        using EngineLogs logs = new(started, file);
        foreach (string secret in (Environment.GetEnvironmentVariable(RedactVariable) ?? "").Split('\n'))
            logs.AddSecret(secret);
        EngineLog.Attach(logs);
        logs.Event(EventTypes.EngineStarted, new { name = endpoint.Name, build = Build, protocol = ControlProtocol.Version, pid = Environment.ProcessId });
        if (fileError is not null)
            EngineLog.Write($"Not writing a log file: {fileError}");

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
        using (GameHostSupervisor gameHost = GameHostSupervisor.Start(services, logs, endpoint.Name))
        {
            Engine engine = new(endpoint, gameHost, logs, services);
            await engine.ServeAsync();
        }

        return EngineExitCodes.Success;
    }

    /// <summary>Opens this start's JSONL file; without one the Engine still runs, with its logs in memory only.</summary>
    private static (LogFile? File, string? Error) OpenLogFile(EngineEndpoint endpoint, DateTimeOffset started)
    {
        try
        {
            return (LogFile.Open(endpoint.LogFilesDir, started), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, e.Message);
        }
    }

    public Task<HelloResult> HelloAsync(int protocol, CancellationToken cancellationToken) =>
        Task.FromResult(new HelloResult(ControlProtocol.Version, Build, _endpoint.Name, Environment.ProcessId));

    public async Task<StatusDto> StatusAsync(CancellationToken cancellationToken)
    {
        EngineInfoDto engine = new(_endpoint.Name, Build, ControlProtocol.Version, Math.Round(_uptime.Elapsed.TotalSeconds, 1), Environment.ProcessId);
        return new StatusDto(engine, _gameHost.Status() with { Player = await _queries.PlayerAsync() }, _scripts.Status(), _dialogs.Pending());
    }

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

    public Task<ScreenshotResult> ScreenshotAsync(int? maxWidth, CancellationToken cancellationToken) =>
        _screenshots.TakeAsync(maxWidth, cancellationToken);

    public Task<ServersResult> ServersAsync(CancellationToken cancellationToken) => _game.ServersAsync();

    public Task<LoginResult> LoginAsync(string? server, int? timeoutSec, CancellationToken cancellationToken) =>
        _game.LoginAsync(server, timeoutSec, cancellationToken);

    public Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken) =>
        _game.LogoutAsync(cancellationToken);

    public Task<LocationResult> JoinAsync(string map, string? cell, string? pad, int? timeoutSec, CancellationToken cancellationToken) =>
        _moves.JoinAsync(map, cell, pad, timeoutSec, cancellationToken);

    public Task<LocationResult> JumpAsync(string cell, string? pad, int? timeoutSec, CancellationToken cancellationToken) =>
        _moves.JumpAsync(cell, pad, timeoutSec, cancellationToken);

    public Task<InventoryResult> InventoryAsync(InventoryKind kind, CancellationToken cancellationToken) =>
        _queries.InventoryAsync(kind, cancellationToken);

    public Task<QuestsResult> QuestsAsync(QuestFilter filter, CancellationToken cancellationToken) =>
        _queries.QuestsAsync(filter, cancellationToken);

    public Task<MapDto> MapAsync(CancellationToken cancellationToken) => _queries.MapAsync(cancellationToken);

    public Task<DropsResult> DropsAsync(CancellationToken cancellationToken) => _queries.DropsAsync(cancellationToken);
    public Task<ScriptOptionsResult> ScriptOptionsAsync(string script, CancellationToken cancellationToken) =>
        _scripts.OptionsAsync(script);

    // A client going away mustn't abandon a start half done, so the start ignores cancellation.
    public Task<ScriptStartResult> ScriptStartAsync(
        string script, IReadOnlyDictionary<string, string>? options, DialogMode? dialogs, int? dialogTimeoutSec, CancellationToken cancellationToken) =>
        _scripts.StartAsync(script, options, dialogs, dialogTimeoutSec);

    public Task<ScriptStopResult> ScriptStopAsync(CancellationToken cancellationToken) =>
        _scripts.StopAsync(cancellationToken);

    public Task<ScriptStatusDto> ScriptStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_scripts.Status());

    public Task<ScriptWaitResult> ScriptWaitAsync(int? timeoutSec, CancellationToken cancellationToken) =>
        _scripts.WaitAsync(timeoutSec, cancellationToken);

    public Task<DialogsResult> DialogsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new DialogsResult(_dialogs.Pending()));

    public Task<DialogAnswerResult> DialogAnswerAsync(int id, string choice, CancellationToken cancellationToken) =>
        Task.FromResult(_dialogs.Answer(id, choice));

    public Task<EvalResult> EvalAsync(string code, int? timeoutSec, CancellationToken cancellationToken) =>
        _eval.EvalAsync(code, timeoutSec, cancellationToken);

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
            await StopAsync();
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

    /// <summary>
    /// The shutdown order: the Script stops cooperatively, then the Game Host closes (without logging out), then the connections,
    /// then the socket; the lock goes last.
    /// </summary>
    private async Task StopAsync()
    {
        EngineLog.Write("Shutting down.");
        try
        {
            if ((await _scripts.StopAsync(CancellationToken.None)).WasRunning)
                EngineLog.Write("Stopped the running Script.");
        }
        catch (Exception e)
        {
            EngineLog.Write($"Stopping the Script failed: {e}");
        }
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
