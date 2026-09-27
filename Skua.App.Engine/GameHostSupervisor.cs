using Microsoft.Extensions.DependencyInjection;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.MacOS.GameHost;

namespace Skua.App.Engine;

/// <summary>
/// Starts the Game Client in the Game Host and brings it to the login screen, as the Windows app does at start-up, and logs what the Game Host does.
/// </summary>
internal sealed class GameHostSupervisor : IDisposable
{
    private readonly BridgeFlashUtil _flash;

    private GameHostSupervisor(BridgeFlashUtil flash)
    {
        _flash = flash;
    }

    /// <summary>Prepares the data folder and Core, then starts the Game Host.</summary>
    public static GameHostSupervisor Start(IServiceProvider services)
    {
        IClientFilesService clientFiles = services.GetRequiredService<IClientFilesService>();
        clientFiles.CreateDirectories();
        clientFiles.CreateFiles();
        _ = services.GetRequiredService<IScriptInterface>();

        BridgeFlashUtil flash = services.GetRequiredService<BridgeFlashUtil>();
        GameHostLaunch launch = services.GetRequiredService<GameHostLaunch>();
        flash.GameHostStarted += pid => EngineLog.Write($"gamehost.started: pid {pid}, {launch.Executable} {launch.Swf}");
        flash.GameHostExited += code => EngineLog.Write($"gamehost.exited: the Game Host exited with code {code}.");
        flash.GameHostLog += line => EngineLog.Write($"[gamehost] {line}");
        flash.FlashLog += line => EngineLog.Write($"[flash] {line}");
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
                    break;
            }
        };
        flash.InitializeFlash();
        return new GameHostSupervisor(flash);
    }

    public GameStatusDto Status()
    {
        bool up = _flash.IsGameHostRunning;
        return new GameStatusDto(up, up ? null : GameState.NotStarted, null);
    }

    /// <summary>Closes the Game Host. Safe to call more than once.</summary>
    public void Dispose() => _flash.Dispose();
}
