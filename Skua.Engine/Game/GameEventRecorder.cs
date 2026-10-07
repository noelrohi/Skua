using CommunityToolkit.Mvvm.Messaging;
using Newtonsoft.Json.Linq;
using Skua.Engine.Logging;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;

namespace Skua.Engine.Game;

/// <summary>
/// Turns what the game reports into events and tracker edges, all the time and not only while a Script runs: the Game Client's
/// <c>pext</c> and <c>packet</c> calls, and Core's game-event messages; and records the game's chat as game messages, hands quest turn-ins
/// and the game server's answers to <see cref="QuestTurnIns"/>, and the player's quest packets and the answers to its accepts to
/// <see cref="QuestTraffic"/>.
/// </summary>
internal sealed class GameEventRecorder
{
    private readonly EngineLogs _logs;
    private readonly GameStateTracker _tracker;
    private readonly IScriptOption _options;
    private readonly IScriptPlayer _player;
    private readonly IScriptInventory _inventory;
    private readonly QuestTurnIns _turnIns;
    private readonly QuestTraffic _questTraffic;
    private readonly object _lock = new();
    private string? _map;
    private string? _cell;

    /// <summary>Whether the inventory has been recorded as full since a check last found a free slot.</summary>
    private bool _recordedFull;

    /// <summary>The drops recorded as having no slot since then.</summary>
    private readonly HashSet<int> _noSlotDrops = [];

    private GameEventRecorder(EngineLogs logs, GameStateTracker tracker, IScriptOption options, IScriptPlayer player, IScriptInventory inventory, QuestTurnIns turnIns,
        QuestTraffic questTraffic)
    {
        _turnIns = turnIns;
        _questTraffic = questTraffic;
        _logs = logs;
        _tracker = tracker;
        _options = options;
        _player = player;
        _inventory = inventory;
    }

    /// <summary>Starts recording. Call it after Core's Script API is built, so Core has handled each game call first.</summary>
    public static GameEventRecorder Start(
        IFlashUtil flash, IScriptOption options, IScriptPlayer player, IScriptInventory inventory, EngineLogs logs, GameStateTracker tracker, QuestTurnIns turnIns,
        QuestTraffic questTraffic)
    {
        GameEventRecorder recorder = new(logs, tracker, options, player, inventory, turnIns, questTraffic);
        flash.FlashCall += recorder.OnFlashCall;
        // Each login starts a new world, so a full inventory is recorded again.
        tracker.Playing += () =>
        {
            lock (recorder._lock)
                recorder.Rearm();
        };

        IMessenger messenger = StrongReferenceMessenger.Default;
        int channel = (int)MessageChannels.GameEvents;
        messenger.Register<GameEventRecorder, PlayerDeathMessage, int>(recorder, channel, static (r, _) => r.OnDeath());
        messenger.Register<GameEventRecorder, PlayerAFKMessage, int>(recorder, channel, static (r, _) => r._logs.Event(EventTypes.PlayerAfk, new { }));
        messenger.Register<GameEventRecorder, CellChangedMessage, int>(recorder, channel, static (r, m) => r.OnCellChanged(m.Cell));
        messenger.Register<GameEventRecorder, ReloginTriggeredMessage, int>(recorder, channel, static (r, m) => r.OnReloginTriggered(m.WasKicked));
        messenger.Register<GameEventRecorder, ReloginFinishedMessage, int>(recorder, channel, static (r, m) => r.OnReloginFinished(m.Success));
        return recorder;
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
                // The Game Client's own packets: this one is the in-game logout.
                case "packet" when args is [string packet] && packet.Split('%', StringSplitOptions.RemoveEmptyEntries) is [_, _, "cmd", _, "logout", ..]:
                    _tracker.LoggedOutInGame();
                    break;
                // %xt%zm%tryQuestComplete%<room>%<quest>%<reward>%…, whoever sent it.
                case "packet" when args is [string packet] && packet.Split('%', StringSplitOptions.RemoveEmptyEntries) is [_, _, "tryQuestComplete", _, string id, ..]
                    && int.TryParse(id, out int quest):
                    _turnIns.Sent(quest);
                    _questTraffic.Sent();
                    break;
                // %xt%zm%acceptQuest%<room>%<quest>%, whoever sent it.
                case "packet" when args is [string packet] && packet.Split('%', StringSplitOptions.RemoveEmptyEntries) is [_, _, "acceptQuest", _, string id, ..]
                    && int.TryParse(id, out int quest):
                    _questTraffic.Accepting(quest);
                    break;
                case "packet" when args is [string packet] && packet.Split('%', StringSplitOptions.RemoveEmptyEntries) is [_, _, "getQuests", ..]:
                    _questTraffic.Sent();
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
                CheckInventory(null, null);
                break;
            // items: { "<id>": { ItemID, sName, … } }
            case ("json", JObject json) when (string?)json["cmd"] == "dropItem" && json["items"] is JObject items:
                foreach (JObject item in items.Properties().Select(p => p.Value).OfType<JObject>())
                    if ((int?)item["ItemID"] is { } id)
                        CheckInventory(id, ((string?)item["sName"])?.Trim() ?? "");
                break;
            // The game server's answer to a turn-in: {bSuccess: 1, QuestID, sName, …}, or {bSuccess: 0, msg?}, which names no quest.
            case ("json", JObject json) when (string?)json["cmd"] == "ccqr":
                _turnIns.Answered((int?)json["QuestID"], (int?)json["bSuccess"] == 1, (string?)json["sName"],
                    (string?)json["msg"] is { } message && !string.IsNullOrWhiteSpace(message) ? message : null);
                break;
            // The game server's answer to an accept: {cmd: acceptQuest, bSuccess, QuestID, msg}.
            case ("json", JObject json) when (string?)json["cmd"] == "acceptQuest" && (int?)json["QuestID"] is { } acceptedQuest:
                _questTraffic.Answered(acceptedQuest);
                break;
            case ("json", JObject json) when (string?)json["cmd"] is "addItems"
                || ((string?)json["cmd"] == "getDrop" && (int?)json["bSuccess"] == 1) || ((string?)json["cmd"] == "buyItem" && (int?)json["bitSuccess"] == 1):
                CheckInventory(null, null);
                break;
            // String packets are their fields after xt: [cmd, room, …].
            // [chatm, room, "<channel>~<text>", from, …]: zone, party, guild and the other chat channels.
            case ("str", JArray { Count: > 3 } parts) when (string?)parts[0] == "chatm" && ((string?)parts[2])?.Split('~', 2) is [string channel, string text]:
                _logs.Game(channel, (string?)parts[3], null, text);
                break;
            // [whisper, room, text, from, to, …]
            case ("str", JArray { Count: > 4 } parts) when (string?)parts[0] == "whisper":
                _logs.Game("whisper", (string?)parts[3], (string?)parts[4], (string?)parts[2] ?? "");
                break;
            // [server|warning, room, text]
            case ("str", JArray { Count: > 2 } parts) when (string?)parts[0] is "server" or "warning":
                _logs.Game((string)parts[0]!, null, null, (string?)parts[2] ?? "");
                // "Please slow down. Last action was too soon!": the game server refused an action without its answer.
                if ((string?)parts[0] == "warning" && ((string?)parts[2])?.StartsWith("Please slow down", StringComparison.OrdinalIgnoreCase) == true)
                    _questTraffic.SlowedDown();
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
        // The player's cell is on their entry among the map's users.
        string? me = _player.Username?.ToLowerInvariant();
        string? cell = data["uoBranch"] is JArray users
            ? (string?)users.OfType<JObject>().FirstOrDefault(u => me is not null && (string?)u["uoName"] == me)?["strFrame"]
            : null;
        lock (_lock)
            (_map, _cell) = (map, cell);
        _logs.Event(EventTypes.MapJoined, new { map, roomId = (int?)data["areaId"], cell });
    }

    /// <summary>Records <see cref="EventTypes.InventoryFull"/> once as the inventory fills, and once per drop it has no slot for while it stays full.</summary>
    private void CheckInventory(int? dropId, string? dropName)
    {
        int used = _inventory.UsedSlots, slots = _inventory.Slots;
        // No slots is an inventory that hasn't loaded.
        bool full = slots > 0 && used >= slots;
        // A drop of an item already in the inventory stacks onto it.
        bool noSlot = full && dropId is { } id && !_inventory.TryGetItem(id, out _);
        lock (_lock)
        {
            if (!full)
            {
                Rearm();
                return;
            }
            bool filled = !_recordedFull;
            _recordedFull = true;
            bool newDrop = noSlot && _noSlotDrops.Add(dropId!.Value);
            if (!filled && !newDrop)
                return;
        }
        _logs.Event(EventTypes.InventoryFull, new { used, slots, drop = noSlot ? new { id = dropId, name = dropName } : null });
    }

    private void Rearm()
    {
        _recordedFull = false;
        _noSlotDrops.Clear();
    }

    private void OnCellChanged(string cell)
    {
        lock (_lock)
            _cell = cell;
    }

    private void OnDeath()
    {
        string? map, cell;
        lock (_lock)
            (map, cell) = (_map, _cell);
        _logs.Event(EventTypes.PlayerDeath, new { map, cell });
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
