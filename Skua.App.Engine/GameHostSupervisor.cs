using Microsoft.Extensions.DependencyInjection;
using Skua.App.Engine.Game;
using Skua.App.Engine.Logging;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.MacOS.GameHost;

namespace Skua.App.Engine;

/// <summary>
/// Starts the Game Client in the Game Host and brings it to the login screen, as the Windows app does at start-up,
/// and records what the Game Host and the game do in the logs, the events and the game state.
/// </summary>
internal sealed class GameHostSupervisor : IDisposable
{
    private readonly BridgeFlashUtil _flash;
    private readonly GameStateTracker _tracker;

    private GameHostSupervisor(BridgeFlashUtil flash, GameStateTracker tracker)
    {
        _flash = flash;
        _tracker = tracker;
    }

    /// <summary>The game state, which this supervisor feeds and owns.</summary>
    public GameStateTracker Tracker => _tracker;

    /// <summary>Prepares the data folder and Core, then starts the Game Host.</summary>
    /// <param name="engineName">Names the Engine in the idle-sleep assertion it holds while logged in.</param>
    public static GameHostSupervisor Start(IServiceProvider services, EngineLogs logs, string engineName)
    {
        IClientFilesService clientFiles = services.GetRequiredService<IClientFilesService>();
        clientFiles.CreateDirectories();
        clientFiles.CreateFiles();
        // Builds Core's Script API before the Game Client loads, so its handlers see every call from the start, as in the Windows app.
        _ = services.GetRequiredService<IScriptInterface>();
        IScriptOption options = services.GetRequiredService<IScriptOption>();
        GameStateTracker tracker = new(services.GetRequiredService<IFlashUtil>(), logs, new PowerAssertion($"Skua Engine '{engineName}' is logged in"));
        // The Engine never shows the game, so nothing is lost by not drawing the world; on again after any login, since a stopped Script turns it off.
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
        return new GameHostSupervisor(flash, tracker);
    }

    public GameStatusDto Status() => new(_flash.IsGameHostRunning, _tracker.State, _tracker.Server);

    /// <summary>Closes the Game Host and stops tracking the game. Safe to call more than once.</summary>
    public void Dispose()
    {
        _flash.Dispose();
        _tracker.Dispose();
    }
}
