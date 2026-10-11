using Newtonsoft.Json.Linq;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Engine.Logging;

namespace Skua.Engine.Game;

/// <summary>
/// The AQW game client the Game Client loaded, which it records as <c>game.version</c> and <c>status</c> reports, so a new client, whose changes
/// can break what Skua reads of the game, shows. See docs/game-client-updates.md.
/// </summary>
internal sealed class GameClientVersion(IFlashUtil flash, EngineLogs logs)
{
    private volatile GameClientDto? _loaded;

    /// <summary>The client the Game Client last loaded, or null while none has.</summary>
    public GameClientDto? Loaded => _loaded;

    /// <summary>Reads and records the client. Call it when the Game Client reports <c>loaded</c>.</summary>
    public void Read()
    {
        // skua.swf loads the client that https://game.aq.com/game/api/data/gameversion names, and keeps that data as the game's params.vars:
        // {sFile, sTitle, sBG, sVersion}.
        JObject? vars = flash.GetGameObject<JObject>("params.vars");
        if ((string?)vars?["sFile"] is not { Length: > 0 } file)
        {
            _loaded = null;
            EngineLog.Write("The Game Client loaded, but its gameversion data names no game client.");
            return;
        }
        GameClientDto client = new(file, (string?)vars["sVersion"], (string?)vars["sTitle"]);
        _loaded = client;
        logs.Event(EventTypes.GameVersion, new { file, version = client.Version, title = client.Title });
        EngineLog.Write($"The game client is {file} ({client.Version}).");
    }

    /// <summary>Forgets the client, as the Game Host exits.</summary>
    public void Clear() => _loaded = null;
}
