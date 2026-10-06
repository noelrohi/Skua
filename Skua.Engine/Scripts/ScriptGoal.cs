using System.Globalization;
using Skua.Control;
using Skua.Engine.Logging;

namespace Skua.Engine.Scripts;

/// <summary>
/// What the running Script is working toward and why, read from the log lines of CoreBots, which nearly every Script uses: the quest it
/// does, the item it farms the materials to buy, the material it farms, the monster it kills, and each death that resets a wave. Counts
/// come from what the player owns, sampled with the quests. A Script that logs none of these lines has no goal.
/// </summary>
/// <remarks>
/// The lines it reads, after CoreBots' <c>[hh:mm:ss] (Caller) </c> prefix: <c>Doing Quest: [id] - "Name"</c>,
/// <c>Farming to buy Item (#n/m)</c>, <c>Bought n Item</c>, <c>Farming Item (n/m)</c>, <c>Killing Monster for item: "Item" n/m</c>
/// (or <c>for Item (n/m)</c>), and <c>Death - Resetting</c>.
/// </remarks>
internal sealed class ScriptGoal
{
    /// <summary>How long a step is measured before its rate says: 600 s unless set.</summary>
    public const string RateVariable = "SKUA_GOAL_RATE_SEC";

    private readonly object _lock = new();
    private readonly TimeSpan _rateAfter;
    private Step? _quest;
    private Item? _buy;
    private Item? _farm;
    private Step? _now;
    private readonly List<DateTime> _resets = [];
    private Dictionary<string, int> _owned = new(StringComparer.OrdinalIgnoreCase);

    public ScriptGoal(EngineLogs logs)
    {
        // A farm loop gains in bursts, kills until the quest items drop and then several turn-ins at once, so its first minutes swing
        // widely; ten minutes spans several of those rounds.
        _rateAfter = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable(RateVariable), out int sec) && sec > 0 ? sec : 600);
        logs.TextWritten += (kind, text) =>
        {
            if (kind == LogKind.Script)
                Read(text, DateTime.UtcNow);
        };
    }

    /// <summary>Takes what the player owns now, by item name: the inventory's, the temporary inventory's and the bank's.</summary>
    public void Sample(Dictionary<string, int> owned)
    {
        lock (_lock)
            _owned = owned;
    }

    /// <summary><paramref name="run"/> with its goal; none from before the run started, so a run never shows the last one's.</summary>
    public ScriptRunDto? WithGoal(ScriptRunDto? run)
    {
        if (run is null)
            return null;
        DateTime started = run.StartedAt.UtcDateTime;
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            Step? quest = _quest is { At: var q } && q >= started ? _quest : null;
            Item? buy = _buy is { At: var b } && b >= started ? _buy : null;
            Item? farm = _farm is { At: var f } && f >= started ? _farm : null;
            Step? doing = _now is { At: var n } && n >= started ? _now : null;
            List<DateTime> resets = _resets.Where(r => r >= started).ToList();
            if (quest is null && buy is null && farm is null && doing is null)
                return run;
            return run with
            {
                Goal = new ScriptGoalDto(quest?.Text, buy is null ? null : ToDto(buy, now), farm is null ? null : ToDto(farm, now), doing?.Text,
                    resets.Count, resets.Count > 0 ? new DateTimeOffset(resets[^1]) : null),
            };
        }
    }

    private GoalItemDto ToDto(Item item, DateTime now)
    {
        int? have = _owned.Count > 0 ? _owned.GetValueOrDefault(item.Name) : null;
        // The pace since the Script logged the step, from the count it logged then, once there is a gain and it has been measured long enough.
        TimeSpan measured = now - item.At;
        double? perHour = have is int h && h > item.Start && measured >= _rateAfter ? Math.Round((h - item.Start) / measured.TotalHours, 1) : null;
        return new GoalItemDto(item.Name, item.Want, have, perHour);
    }

    private void Read(string line, DateTime at)
    {
        string text = Strip(line);
        lock (_lock)
        {
            if (After(text, "Doing Quest: [") is { } quest)
            {
                // A new quest starts the chain afresh.
                _quest = new Step(quest.Split("] - ", 2) is [_, string name] ? name.Trim('"', ' ') : quest, at);
                (_buy, _farm, _now) = (null, null, null);
                _resets.Clear();
            }
            else if (After(text, "Farming to buy ") is { } toBuy && Counted(toBuy) is var (buyName, _, buyWant))
            {
                _buy = new Item(buyName, buyWant, 0, at);
                (_farm, _now) = (null, null);
                _resets.Clear();
            }
            else if (After(text, "Bought ") is { } bought && _buy is { } buying && bought.EndsWith(buying.Name, StringComparison.OrdinalIgnoreCase))
            {
                (_buy, _farm, _now) = (null, null, null);
                _resets.Clear();
            }
            else if (After(text, "Farming ") is { } farming && Counted(farming) is var (name, have, want))
            {
                SetFarm(name, have, want, at);
            }
            else if (text.Contains("Death - Resetting", StringComparison.Ordinal))
            {
                _resets.Add(at);
            }
            else if (text.IndexOf("Killing ", StringComparison.Ordinal) is int k and >= 0 && text[(k + 8)..].Split(" for ", 2) is [string monster, string target])
            {
                (string item, int? itemHave, int? itemWant) = KillTarget(target);
                _now = new Step($"killing {monster} for {item}", at);
                // A kill for a counted item is the farm step when no material is logged, as for a quest's own requirement.
                if (itemHave is int kh && itemWant is int kw && (_farm is null || !_farm.Name.Equals(item, StringComparison.OrdinalIgnoreCase)))
                    SetFarm(item, kh, kw, at);
            }
        }
    }

    private void SetFarm(string name, int have, int want, DateTime at)
    {
        if (_farm is not null && _farm.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            return;
        _farm = new Item(name, want, have, at);
        _resets.Clear();
    }

    /// <summary>The line without CoreBots' <c>[hh:mm:ss] (Caller) </c> prefix, or as it is.</summary>
    private static string Strip(string line)
    {
        string text = line.TrimStart();
        if (text.StartsWith('[') && text.IndexOf("] ", StringComparison.Ordinal) is int stamp and > 0)
            text = text[(stamp + 2)..];
        if (text.StartsWith('(') && text.IndexOf(") ", StringComparison.Ordinal) is int caller and > 0)
            text = text[(caller + 2)..];
        return text;
    }

    /// <summary>What a kill is for: <c>item: "Item" n/m</c>, or <c>Item (n/m) …</c>, or just the item.</summary>
    private static (string Item, int? Have, int? Want) KillTarget(string target)
    {
        if (target.StartsWith("item: ", StringComparison.Ordinal))
            target = target[6..];
        if (target.StartsWith('"') && target.IndexOf('"', 1) is int end and > 0)
        {
            string[] count = target[(end + 1)..].Trim().Split(' ', 2)[0].Split('/');
            return count is [string n, string m] && int.TryParse(n, CultureInfo.InvariantCulture, out int have)
                && int.TryParse(m, CultureInfo.InvariantCulture, out int want)
                ? (target[1..end], have, want)
                : (target[1..end], null, null);
        }
        return target.IndexOf(')') is int close and > 0 && Counted(target[..(close + 1)]) is var (name, h, w)
            ? (name, h, w)
            : (target.Trim(), null, null);
    }

    private static string? After(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : null;

    /// <summary><c>Name (n/m)</c> or <c>Name (#n/m)</c>.</summary>
    private static (string Name, int Have, int Want)? Counted(string text)
    {
        int open = text.LastIndexOf(" (", StringComparison.Ordinal);
        if (open < 0 || !text.EndsWith(')'))
            return null;
        string[] parts = text[(open + 2)..^1].TrimStart('#').Split('/');
        return parts is [string n, string m]
            && int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out int have)
            && int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out int want)
            ? (text[..open].Trim(), have, want)
            : null;
    }

    private sealed record Step(string Text, DateTime At);

    private sealed record Item(string Name, int Want, int Start, DateTime At);
}
