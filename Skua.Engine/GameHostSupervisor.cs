using Microsoft.Extensions.DependencyInjection;
using Skua.Engine.Game;
using Skua.Engine.Logging;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.MacOS.GameHost;

namespace Skua.Engine;

/// <summary>
/// Starts the Game Client in the Game Host and brings it to the login screen, as the Windows app does at start-up,
/// and records what the Game Host and the game do in the logs, the events and the game state.
/// </summary>
internal sealed class GameHostSupervisor : IDisposable
{
    /// <summary>Seconds between the Game Host stats lines in the debug log: 60 unless set; 0 turns them off.</summary>
    public const string StatsIntervalVariable = "SKUA_GAMEHOST_STATS_SEC";

    /// <summary>Starts each debug line with the Game Host's stats JSON; the live-game tests read frame rates and tick gaps from them.</summary>
    public const string StatsPrefix = "[gamehost] stats ";

    private static readonly TimeSpan StatsTimeout = TimeSpan.FromSeconds(5);

    private readonly BridgeFlashUtil _flash;
    private readonly GameStateTracker _tracker;
    private readonly Timer? _stats;

    private GameHostSupervisor(BridgeFlashUtil flash, GameStateTracker tracker, EngineLogs logs)
    {
        _flash = flash;
        _tracker = tracker;
        TimeSpan interval = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable(StatsIntervalVariable), out int seconds) && seconds >= 0 ? seconds : 60);
        if (interval > TimeSpan.Zero)
            _stats = new Timer(_ => WriteStats(logs), null, interval, interval);
    }

    /// <summary>The game state, which this supervisor feeds and owns.</summary>
    public GameStateTracker Tracker => _tracker;

    /// <summary>Prepares the data folder and Core, then starts the Game Host.</summary>
    /// <param name="engineName">Names the Engine in the idle-sleep assertion it holds while logged in.</param>
    /// <param name="keepLagKillerOn">Turns the lag killer on at every login, for an Engine that never shows the game.</param>
    public static GameHostSupervisor Start(IServiceProvider services, EngineLogs logs, string engineName, bool keepLagKillerOn)
    {
        IClientFilesService clientFiles = services.GetRequiredService<IClientFilesService>();
        clientFiles.CreateDirectories();
        clientFiles.CreateFiles();
        // Builds Core's Script API before the Game Client loads, so its handlers see every call from the start, as in the Windows app.
        _ = services.GetRequiredService<IScriptInterface>();
        IScriptOption options = services.GetRequiredService<IScriptOption>();
        GameStateTracker tracker = new(services.GetRequiredService<IFlashUtil>(), logs, new PowerAssertion($"Skua Engine '{engineName}' is logged in"));
        // skua-engine never shows the game, so nothing is lost by not drawing the world; on again after any login, since a stopped Script turns it off.
        if (keepLagKillerOn)
            tracker.Playing += () => options.LagKiller = true;
        GameEventRecorder.Start(services.GetRequiredService<IFlashUtil>(), options, services.GetRequiredService<IScriptPlayer>(), logs, tracker);

        BridgeFlashUtil flash = services.GetRequiredService<BridgeFlashUtil>();
        GameHostLaunch launch = services.GetRequiredService<GameHostLaunch>();
        flash.GameHostStarted += pid =>
        {
            logs.Event(EventTypes.GameHostStarted, new { pid, executable = launch.Executable, swf = launch.Swf });
            EngineLog.Write($"Game Host started (pid {pid}): {launch.Executable} {launch.Swf}");
            tracker.GameHostStarted();
        };
        flash.GameHostExited += code =>
        {
            logs.Event(EventTypes.GameHostExited, new { code });
            EngineLog.Write($"Game Host exited with code {code}.");
            tracker.GameHostExited();
        };
        flash.GameHostLog += line => logs.Write(LogKind.Debug, $"[gamehost] {line}");
        flash.FlashLog += line => logs.Write(LogKind.Flash, line);
        flash.BridgeFailed += error => logs.Event(EventTypes.BridgeError, new { error });
        flash.FlashCall += (function, _) =>
        {
            switch (function)
            {
                // The same hand-off as SkuaStartupHandler: skua.swf asks, and the Engine tells it to load the game.
                case "requestLoadGame":
                    flash.Call("loadClient");
                    break;
                case "loaded":
                    EngineLog.Write($"The Game Client has loaded; {flash.Callbacks.Count} callbacks registered.");
                    tracker.Loaded();
                    break;
            }
        };
        flash.InitializeFlash();
        return new GameHostSupervisor(flash, tracker, logs);
    }

    /// <summary>
    /// Records the Game Host's counters since the last line, so a post-mortem or a live-game test sees its frame rate and tick gaps
    /// (App Nap and timer throttling show up as long gaps).
    /// </summary>
    private void WriteStats(EngineLogs logs)
    {
        try
        {
            if (_flash.Stats(StatsTimeout) is { } json)
                logs.Write(LogKind.Debug, StatsPrefix + json);
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
        {
            // The Game Host is gone or busy; gamehost.exited or the next line tells.
        }
    }

    public GameStatusDto Status() => new(_flash.IsGameHostRunning, _tracker.State, _tracker.Server);

    /// <summary>Closes the Game Host and stops tracking the game. Safe to call more than once.</summary>
    public void Dispose()
    {
        _stats?.Dispose();
        _flash.Dispose();
        _tracker.Dispose();
    }
}
