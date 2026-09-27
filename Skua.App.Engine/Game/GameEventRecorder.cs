using CommunityToolkit.Mvvm.Messaging;
using Newtonsoft.Json.Linq;
using Skua.App.Engine.Logging;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;

namespace Skua.App.Engine.Game;

/// <summary>
/// Turns what the game reports into events and tracker edges, all the time and not only while a Script runs: the Game Client's
/// <c>pext</c> and <c>packet</c> calls, and Core's game-event messages.
/// </summary>
internal sealed class GameEventRecorder
{
    private readonly EngineLogs _logs;
    private readonly GameStateTracker _tracker;
    private readonly IScriptOption _options;
    private readonly object _lock = new();
    private string? _username;
    private string? _map;
    private string? _cell;

    private GameEventRecorder(EngineLogs logs, GameStateTracker tracker, IScriptOption options)
    {
        _logs = logs;
        _tracker = tracker;
        _options = options;
    }

    /// <summary>Starts recording. Call it after Core's Script API is built, so Core has handled each game call first.</summary>
    public static GameEventRecorder Start(IFlashUtil flash, IScriptOption options, EngineLogs logs, GameStateTracker tracker)
    {
        GameEventRecorder recorder = new(logs, tracker, options);
        flash.FlashCall += recorder.OnFlashCall;

        IMessenger messenger = StrongReferenceMessenger.Default;
        int channel = (int)MessageChannels.GameEvents;
        messenger.Register<GameEventRecorder, PlayerDeathMessage, int>(recorder, channel, static (r, _) => r.OnDeath());
        messenger.Register<GameEventRecorder, PlayerAFKMessage, int>(recorder, channel, static (r, _) => r._logs.Event(EventTypes.PlayerAfk, new { }));
        messenger.Register<GameEventRecorder, CellChangedMessage, int>(recorder, channel, static (r, m) => r.OnCellChanged(m.Cell));
        messenger.Register<GameEventRecorder, ReloginTriggeredMessage, int>(recorder, channel, static (r, m) => r.OnReloginTriggered(m.WasKicked));
        messenger.Register<GameEventRecorder, ReloginFinishedMessage, int>(recorder, channel, static (r, m) => r.OnReloginFinished(m.Success));
        return recorder;
    }

    /// <summary>The Test Account's name, which finds the player among the users of a joined map.</summary>
    public void SetUsername(string username)
    {
        lock (_lock)
            _username = username;
    }

    private void OnFlashCall(string function, object[] args)
    {
        try
        {
            switch (function)
            {
                case "pext" when args is [string packet]:
                    OnExtensionPacket(JObject.Parse(packet));
                    break;
                // The client's own packets: this one is the in-game logout.
                case "packet" when args is [string packet] && packet.Split('%', StringSplitOptions.RemoveEmptyEntries) is [_, _, "cmd", _, "logout", ..]:
                    _tracker.LoggedOutInGame();
                    break;
            }
        }
        catch (Exception e) when (e is Newtonsoft.Json.JsonException or InvalidCastException or FormatException)
        {
            EngineLog.Write($"Couldn't read the game's {function} call: {e.Message}");
        }
    }

    private void OnExtensionPacket(JObject packet)
    {
        JToken? data = packet["params"]?["dataObj"];
        switch ((string?)packet["params"]?["type"], data)
        {
            case ("json", JObject json) when (string?)json["cmd"] == "moveToArea":
                OnMapJoined(json);
                break;
            case ("str", JArray { Count: > 2 } parts) when (string?)parts[0] == "loginResponse":
                // Accepted: [cmd, -1, "true", id, username, …]; refused: [cmd, -1, "false", …, message].
                bool accepted = (string?)parts[2] == "true";
                _tracker.LoginResponse(accepted, accepted ? null : parts.Skip(3).Select(p => (string?)p).LastOrDefault(p => !string.IsNullOrWhiteSpace(p)));
                break;
        }
    }

    private void OnMapJoined(JObject data)
    {
        string? map = (string?)data["strMapName"];
        string? cell;
        lock (_lock)
        {
            string? me = _username?.ToLowerInvariant();
            cell = data["uoBranch"] is JArray users
                ? (string?)users.OfType<JObject>().FirstOrDefault(u => me is not null && (string?)u["uoName"] == me)?["strFrame"]
                : null;
            (_map, _cell) = (map, cell);
        }
        _logs.Event(EventTypes.MapJoined, new { map, roomId = (int?)data["areaId"], cell });
    }

    private void OnCellChanged(string cell)
    {
        lock (_lock)
            _cell = cell;
    }

    private void OnDeath()
    {
        lock (_lock)
            _logs.Event(EventTypes.PlayerDeath, new { map = _map, cell = _cell });
    }

    private void OnReloginTriggered(bool wasKicked)
    {
        _tracker.ReloginTriggered();
        // The delay Core's auto-relogin waits before it logs in again.
        int delayMs = !_options.SafeRelogin && !wasKicked ? _options.ReloginTryDelay : 70_000;
        _logs.Event(EventTypes.GameRelogin, new { phase = "triggered", wasKicked, delayMs });
        _tracker.ReloginStarted();
    }

    private void OnReloginFinished(bool ok)
    {
        _logs.Event(EventTypes.GameRelogin, new { phase = "finished", ok });
        _tracker.ReloginFinished();
    }
}
