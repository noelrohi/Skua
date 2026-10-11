using Newtonsoft.Json.Linq;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Engine.Logging;

namespace Skua.Engine.Game;

/// <summary>
/// The Game SWF the Game Client loaded, which it records as <c>game.version</c> and <c>status</c> reports, so a new Game SWF, whose changes
/// can break what Skua reads of the game, shows. See docs/game-swf-updates.md.
/// </summary>
internal sealed class GameSwfVersion(IFlashUtil flash, EngineLogs logs)
{
    private volatile GameSwfDto? _loaded;

    /// <summary>The Game SWF the Game Client last loaded, or null while it has loaded none.</summary>
    public GameSwfDto? Loaded => _loaded;

    /// <summary>Reads and records the Game SWF. Call it when the Game Client reports <c>loaded</c>.</summary>
    public void Read()
    {
        // skua.swf loads the Game SWF that https://game.aq.com/game/api/data/gameversion names, and keeps that data as the game's params.vars:
        // {sFile, sTitle, sBG, sVersion}.
        JObject? vars = flash.GetGameObject<JObject>("params.vars");
        if ((string?)vars?["sFile"] is not { Length: > 0 } file)
        {
            _loaded = null;
            EngineLog.Write("The Game Client loaded, but its gameversion data names no Game SWF.");
            return;
        }
        GameSwfDto swf = new(file, (string?)vars["sVersion"], (string?)vars["sTitle"]);
        _loaded = swf;
        logs.Event(EventTypes.GameVersion, swf);
        EngineLog.Write($"The Game SWF is {file} ({swf.Version}).");
    }

    /// <summary>Forgets the Game SWF, as the Game Host exits.</summary>
    public void Clear() => _loaded = null;
}
