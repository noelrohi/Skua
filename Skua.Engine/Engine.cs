using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Skua.Engine.Game;
using Skua.Engine.Logging;
using Skua.Engine.Scripts;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.MacOS.GameHost;
using Skua.MacOS.Services;
using StreamJsonRpc;

namespace Skua.Engine;

/// <summary>
/// One Engine, in skua-engine or the Mac App: it holds the lock, serves the Control Surface on the socket and owns the Game Host until it
/// shuts down.
/// </summary>
internal sealed class Engine : IEngineRpc
{
    /// <summary>Extra secrets to redact from every log, one per line; for development and tests.</summary>
    public const string RedactVariable = "SKUA_REDACT";

    private static readonly TimeSpan LockRetry = TimeSpan.FromSeconds(1);

    private readonly EngineEndpoint _endpoint;
    private readonly EngineHostOptions _options;
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
    private readonly ScriptRuns _runs;
    private readonly ActionSlot _slot;

    private Engine(EngineEndpoint endpoint, GameHostSupervisor gameHost, EngineLogs logs, IServiceProvider services, EngineHostOptions options)
    {
        _endpoint = endpoint;
        _options = options;
        _gameHost = gameHost;
        _logs = logs;
        IScriptManager manager = services.GetRequiredService<IScriptManager>();
        ScriptDialogBroker broker = services.GetRequiredService<ScriptDialogBroker>();
        _dialogs = new DialogOperations(broker, logs);
        _runs = new(logs, manager, services.GetRequiredService<IScriptOption>(), broker, keepLagKillerOn: options.IsHeadless);
        _slot = new();
        SemaphoreSlim compiling = new(1, 1);
        _scriptSource = new ScriptSourceOperations(services.GetRequiredService<IGetScriptsService>(), _runs, _slot, _shutdown.Token);
        _screenshots = new ScreenshotOperations(services.GetRequiredService<BridgeFlashUtil>(), services.GetRequiredService<IScriptOption>());
        GameActionSlot gameSlot = new(gameHost.Tracker, _runs, _slot);
        _game = new GameOperations(
            services.GetRequiredService<IScriptServers>(), services.GetRequiredService<IFlashUtil>(), services.GetRequiredService<ISettingsService>(), logs,
            gameHost.Tracker, gameSlot);
        _moves = new MoveOperations(
            services.GetRequiredService<IScriptMap>(), services.GetRequiredService<IScriptPlayer>(), services.GetRequiredService<IScriptWait>(), gameHost.Tracker, gameSlot);
        _queries = new GameQueries(services.GetRequiredService<IScriptInterface>(), services.GetRequiredService<IFlashUtil>(), gameHost.Tracker, gameSlot);
        _scripts = new ScriptOperations(manager, _runs, broker, _slot, compiling);
        _eval = new EvalOperations(manager, services.GetRequiredService<IScriptInterface>(), logs, compiling);
    }

    /// <summary>The build a CLI compares with its own, which it shares when built together, to tell whether this Engine is stale.</summary>
    public static string Build => ControlProtocol.Build;

    /// <summary>
    /// Takes the lock, starts the Game Host and binds the socket, then serves the Control Surface in the background until the Engine stops.
    /// </summary>
    /// <exception cref="EngineStartException">Another Engine holds the name, or the Game Host is missing.</exception>
    public static async Task<HostedEngine> StartAsync(EngineEndpoint endpoint, EngineHostOptions options)
    {
        foreach (string dir in new[] { endpoint.EnginesDir, Path.GetDirectoryName(endpoint.SocketPath)! })
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        EngineLock engineLock = await AcquireLockAsync(endpoint)
            ?? throw new EngineStartException(EngineExitCodes.AlreadyRunning, $"Engine '{endpoint.Name}' is already running; it holds {endpoint.LockPath}.");
        EngineLogs? logs = null;
        try
        {
            options.LockAcquired?.Invoke(endpoint);
            DateTimeOffset started = DateTimeOffset.UtcNow;
            (LogFile? file, string? fileError) = OpenLogFile(endpoint, started);
            logs = new(started, file);
            foreach (string secret in (Environment.GetEnvironmentVariable(RedactVariable) ?? "").Split('\n'))
                logs.AddSecret(secret);
            EngineLog.Attach(logs, echo: options.IsHeadless);
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
                throw new EngineStartException(EngineExitCodes.GameHostMissing, e.Message);
            }
            // Only the Mac App shows the game, so only its Game Host gets a Frame Buffer.
            if (!options.IsHeadless)
                launch = launch with { WantsFrameBuffer = true };

            // Like the Windows app, the Engine never disposes Core's singletons: they stop with the process, and their Dispose paths throw.
            ServiceProvider services = EngineServices.Build(launch, logs, options.ConfigureServices);
            StatusChanges statusChanges = StatusChanges.Start(logs, services.GetRequiredService<IFlashUtil>());
            GameHostSupervisor gameHost = GameHostSupervisor.Start(services, logs, endpoint.Name, keepLagKillerOn: options.IsHeadless);
            Engine engine;
            Socket listener;
            try
            {
                engine = new(endpoint, gameHost, logs, services, options);
                listener = engine.Bind();
            }
            catch
            {
                gameHost.Dispose();
                throw;
            }
            Task<int> completion = engine.ServeAsync(listener, engineLock, logs);
            return new HostedEngine(endpoint, services, engine, statusChanges, completion, engine._shutdown.Cancel);
        }
        catch
        {
            logs?.Dispose();
            engineLock.Dispose();
            throw;
        }
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

    private EngineHost Host => _options.IsHeadless ? EngineHost.Engine : EngineHost.App;

    public Task<HelloResult> HelloAsync(int protocol, CancellationToken cancellationToken) =>
        Task.FromResult(new HelloResult(ControlProtocol.Version, Build, _endpoint.Name, Environment.ProcessId, Host));

    public async Task<StatusDto> StatusAsync(CancellationToken cancellationToken)
    {
        EngineInfoDto engine = new(_endpoint.Name, Build, ControlProtocol.Version, Math.Round(_uptime.Elapsed.TotalSeconds, 1), Environment.ProcessId, Host);
        return new StatusDto(engine, _gameHost.Status() with { Player = await _queries.PlayerAsync() }, _scripts.Status(), _dialogs.Pending());
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        EnsureHeadless("Shutdown");
        EngineLog.Write("Shutdown requested over the Control Surface.");
        _shutdown.Cancel();
        return Task.CompletedTask;
    }

    public Task ShutdownIfIdleAsync(CancellationToken cancellationToken)
    {
        EnsureHeadless("A replacement by another build");
        const string action = "replace the Engine";
        // The lease is never returned, so no command starts a Script while the Engine shuts down.
        IDisposable lease = _slot.Take(action);
        try
        {
            _runs.EnsureIdle(action);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        EngineLog.Write("Shutdown requested over the Control Surface, to replace this Engine with another build.");
        _shutdown.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>The Mac App owns its Engine: only quitting the app stops it, so the CLI never takes the window's game away (ADR 0006).</summary>
    private void EnsureHeadless(string request)
    {
        if (_options.IsHeadless)
            return;
        EngineLog.Write($"{request} requested over the Control Surface; refused, as the Skua app owns this Engine.");
        throw RpcErrors.Of(ErrorCode.EngineOwnedByApp, $"The Skua app owns Engine '{_endpoint.Name}'; quit the app to stop it.");
    }

    public Task<ScriptsSearchResult> ScriptsSearchAsync(string query, string? tag, CancellationToken cancellationToken) =>
        _scriptSource.SearchAsync(query, tag, cancellationToken);

    public Task<ScriptsUpdateResult> ScriptsUpdateAsync(CancellationToken cancellationToken) =>
        _scriptSource.UpdateAsync();

    public Task<ScriptsListResult> ScriptsListAsync(string? folder, CancellationToken cancellationToken) =>
        _scriptSource.ListAsync(folder, cancellationToken);

    public Task<ScriptsNewResult> ScriptsNewAsync(string? since, CancellationToken cancellationToken) =>
        Task.FromResult(_scriptSource.New(since));

    public Task<ScriptSourceResult> ScriptsSourceAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_scriptSource.Source());

    public Task<ScriptSourceResult> ScriptsSourceSetAsync(string? source, CancellationToken cancellationToken) =>
        Task.FromResult(_scriptSource.SetSource(source));

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

    public Task<LoginResult> LoginAsync(string? server, int? timeoutSec, bool asAgent, CancellationToken cancellationToken) =>
        _game.LoginAsync(server, timeoutSec, asAgent, cancellationToken);

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

    /// <summary>Binds the socket; a umask of 0177 makes bind create it as 0600, so others never reach it, even in a shared directory.</summary>
    /// <remarks>The umask is process-wide, so the Mac App binds before its UI threads start.</remarks>
    private Socket Bind()
    {
        File.Delete(_endpoint.SocketPath);
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
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
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        EngineLog.Write($"Engine '{_endpoint.Name}' (build {Build}, protocol {ControlProtocol.Version}) listening on {_endpoint.SocketPath}.");
        return listener;
    }

    /// <summary>Serves the Control Surface until the Engine shuts down, then stops it and lets go of its logs and, last, its lock.</summary>
    private async Task<int> ServeAsync(Socket listener, EngineLock engineLock, EngineLogs logs)
    {
        // The Mac App quits through its own lifetime, so only skua-engine stops on a signal.
        using PosixSignalRegistration? sigterm = _options.IsHeadless ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal) : null;
        using PosixSignalRegistration? sigint = _options.IsHeadless ? PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal) : null;
        using (engineLock)
        using (logs)
        {
            try
            {
                await AcceptAsync(listener);
            }
            finally
            {
                listener.Dispose();
                await StopAsync();
            }
        }
        return EngineExitCodes.Success;
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
