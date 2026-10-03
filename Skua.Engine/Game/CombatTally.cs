using CommunityToolkit.Mvvm.Messaging;
using Newtonsoft.Json.Linq;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Engine.Logging;

namespace Skua.Engine.Game;

/// <summary>
/// Counts the monsters the player is credited with killing, and the player's deaths. For a kill the game sends an <c>addGoldExp</c>
/// extension packet of type <c>m</c>, with the gold and XP it gave, which may be none; a death is Core's death message, as
/// <see cref="EventTypes.PlayerDeath"/> records it. A run's kills and kill rate tell a Script grinding a rare drop from one that is stuck.
/// </summary>
internal sealed class CombatTally
{
    /// <summary>How far back the kill rate looks, and how long the tally remembers.</summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Memory = TimeSpan.FromHours(24);

    private readonly object _lock = new();
    private readonly Queue<DateTime> _kills = new();
    private readonly Queue<DateTime> _deaths = new();

    public CombatTally(IFlashUtil flash)
    {
        flash.FlashCall += (function, args) =>
        {
            // Most packets aren't kills; only those naming the command are parsed.
            if (function == "pext" && args is [string packet] && packet.Contains("\"addGoldExp\"", StringComparison.Ordinal) && IsKill(packet))
                Record(_kills, DateTime.UtcNow);
        };
        StrongReferenceMessenger.Default.Register<CombatTally, PlayerDeathMessage, int>(
            this, (int)MessageChannels.GameEvents, static (t, _) => t.Record(t._deaths, DateTime.UtcNow));
    }

    /// <summary><paramref name="run"/> with its kills, kill rate and deaths so far.</summary>
    public ScriptRunDto? WithTally(ScriptRunDto? run)
    {
        if (run is null)
            return null;
        DateTime now = DateTime.UtcNow;
        DateTime started = run.StartedAt.UtcDateTime;
        // Over the last 5 minutes, or the run's time if shorter, but at least a minute, so the first kills don't read as a burst.
        TimeSpan span = TimeSpan.FromMinutes(Math.Max(1, Math.Min(RateWindow.TotalMinutes, (now - started).TotalMinutes)));
        lock (_lock)
        {
            int kills = _kills.Count(k => k >= started);
            int recent = _kills.Count(k => k >= started && now - k <= span);
            return run with { Kills = kills, KillsPerMin = Math.Round(recent / span.TotalMinutes, 1), Deaths = _deaths.Count(d => d >= started) };
        }
    }

    private void Record(Queue<DateTime> tally, DateTime now)
    {
        lock (_lock)
        {
            tally.Enqueue(now);
            while (tally.TryPeek(out DateTime oldest) && now - oldest > Memory)
                tally.Dequeue();
        }
    }

    /// <summary>Whether the packet is <c>addGoldExp</c> for a monster: <c>{"cmd":"addGoldExp","intGold":…,"intExp":…,"typ":"m","id":…}</c>.</summary>
    private static bool IsKill(string packet)
    {
        try
        {
            JToken? data = JObject.Parse(packet)["params"]?["dataObj"];
            return (string?)data?["cmd"] == "addGoldExp" && (string?)data["typ"] == "m";
        }
        catch (Exception e) when (e is Newtonsoft.Json.JsonException or InvalidCastException or FormatException)
        {
            EngineLog.Write($"Couldn't read the game's addGoldExp packet: {e.Message}");
            return false;
        }
    }
}
