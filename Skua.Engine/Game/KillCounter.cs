using Newtonsoft.Json.Linq;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Engine.Logging;

namespace Skua.Engine.Game;

/// <summary>
/// Counts the monsters the player is credited with killing: the game sends an <c>addGoldExp</c> extension packet of type <c>m</c> for each,
/// with the gold and XP it gave, which may be none. A run's kills and kill rate tell a Script grinding a rare drop from one that is stuck.
/// </summary>
internal sealed class KillCounter
{
    /// <summary>How far back the kill rate looks, and how long the counter remembers kills.</summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Memory = TimeSpan.FromHours(24);

    private readonly object _lock = new();
    private readonly Queue<DateTime> _kills = new();

    public KillCounter(IFlashUtil flash)
    {
        flash.FlashCall += (function, args) =>
        {
            // Most packets aren't kills; only those naming the command are parsed.
            if (function == "pext" && args is [string packet] && packet.Contains("\"addGoldExp\"", StringComparison.Ordinal) && IsKill(packet))
                Record(DateTime.UtcNow);
        };
    }

    /// <summary><paramref name="run"/> with its kills so far and its kill rate.</summary>
    public ScriptRunDto? WithKills(ScriptRunDto? run)
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
            return run with { Kills = kills, KillsPerMin = Math.Round(recent / span.TotalMinutes, 1) };
        }
    }

    private void Record(DateTime now)
    {
        lock (_lock)
        {
            _kills.Enqueue(now);
            while (_kills.TryPeek(out DateTime oldest) && now - oldest > Memory)
                _kills.Dequeue();
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
