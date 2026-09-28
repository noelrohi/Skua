using Newtonsoft.Json.Linq;
using Skua.Control;
using Skua.Core.Interfaces;

namespace Skua.Engine.Game;

/// <summary>
/// The items dropped for the player since the login and not yet picked up, from the game's own <c>dropItem</c> and <c>getDrop</c> replies.
/// </summary>
/// <remarks>
/// Not Core's drop list, which drops an item only when Core itself picks it up, so pickups by the game or a Script's packets would stay listed.
/// Rejecting a drop happens only in the Game Client, so a rejected item stays listed until the next login.
/// </remarks>
internal sealed class DropTracker
{
    private readonly object _lock = new();

    /// <summary>The drops in the order they first dropped.</summary>
    private readonly List<DropDto> _drops = [];

    public DropTracker(IFlashUtil flash, GameStateTracker tracker)
    {
        flash.FlashCall += OnFlashCall;
        // Each login starts a new world, with none of the last one's drops.
        tracker.Playing += () =>
        {
            lock (_lock)
                _drops.Clear();
        };
    }

    public IReadOnlyList<DropDto> Drops
    {
        get
        {
            lock (_lock)
                return [.. _drops];
        }
    }

    private void OnFlashCall(string function, object[] args)
    {
        if (function != "pext" || args is not [string packet])
            return;
        try
        {
            if (JObject.Parse(packet)["params"] is not JObject { } parameters || (string?)parameters["type"] != "json" || parameters["dataObj"] is not JObject data)
                return;
            switch ((string?)data["cmd"])
            {
                // items: { "<id>": { ItemID, sName, iQty, … } }
                case "dropItem" when data["items"] is JObject items:
                    foreach (JObject item in items.Properties().Select(p => p.Value).OfType<JObject>())
                        Add((int)item["ItemID"]!, ((string?)item["sName"])?.Trim() ?? "", (int?)item["iQty"] ?? 1);
                    break;
                case "getDrop" when (int?)data["bSuccess"] == 1:
                    Remove((int)data["ItemID"]!);
                    break;
            }
        }
        catch (Exception e) when (e is Newtonsoft.Json.JsonException or InvalidCastException or FormatException or ArgumentException)
        {
            EngineLog.Write($"Couldn't read a drop from the game's pext call: {e.Message}");
        }
    }

    private void Add(int id, string name, int qty)
    {
        lock (_lock)
        {
            int index = _drops.FindIndex(d => d.Id == id);
            if (index < 0)
                _drops.Add(new DropDto(id, name, qty));
            else
                _drops[index] = _drops[index] with { Qty = _drops[index].Qty + qty };
        }
    }

    private void Remove(int id)
    {
        lock (_lock)
            _drops.RemoveAll(d => d.Id == id);
    }
}
