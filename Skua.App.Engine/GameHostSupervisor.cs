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

    private GameHostSupervisor(BridgeFlashUtil flash, GameStateTracker tracker, GameEventRecorder recorder)
    {
        _flash = flash;
        _tracker = tracker;
        Recorder = recorder;
    }

    public GameEventRecorder Recorder { get; }

    /// <summary>Prepares the data folder and Core, then starts the Game Host.</summary>
    public static GameHostSupervisor Start(IServiceProvider services, EngineLogs logs, GameStateTracker tracker)
    {
        IClientFilesService clientFiles = services.GetRequiredService<IClientFilesService>();
        clientFiles.CreateDirectories();
        clientFiles.CreateFiles();
        // Builds Core's Script API before the Game Client loads, so its handlers see every call from the start, as in the Windows app.
        _ = services.GetRequiredService<IScriptInterface>();
        GameEventRecorder recorder = GameEventRecorder.Start(services.GetRequiredService<IFlashUtil>(), services.GetRequiredService<IScriptOption>(), logs, tracker);

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
        return new GameHostSupervisor(flash, tracker, recorder);
    }

    public GameStatusDto Status() => new(_flash.IsGameHostRunning, _tracker.State, _tracker.Server);

    /// <summary>Closes the Game Host and stops tracking the game. Safe to call more than once.</summary>
    public void Dispose()
    {
        _flash.Dispose();
        _tracker.Dispose();
    }
}
