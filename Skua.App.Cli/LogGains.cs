using System.Text.Json;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>What paid an item gain: a quest's turn-ins, or monster drops picked up.</summary>
public enum GainSourceKind
{
    Quest,
    Drops,
}

/// <summary>An item's quantity, and how much that is an hour of the run; null for a run with no length.</summary>
public sealed record ItemCount(int ItemId, string? Name, long Qty, double? PerHour);

/// <summary>
/// One source of a run's gains: a quest, with its successful turn-ins and what they took (the items each turn-in sent), or monster drops,
/// with no turn-ins.
/// </summary>
public sealed record GainSource(
    GainSourceKind Kind, int? QuestId, string Name, int? TurnIns, double? TurnInsPerHour, IReadOnlyList<ItemCount> Gained, IReadOnlyList<ItemCount> Spent);

/// <summary>An item over the whole run: what every source paid, what the turn-ins took, and the difference.</summary>
public sealed record ItemGains(int ItemId, string? Name, long Gained, long Spent, long Net, double? NetPerHour);

/// <summary><c>skua logs gains</c>: run <c>Run</c> of a session log, from its first entry to its last.</summary>
public sealed record GainsResult(string File, int Run, string? Script, double DurationSec, IReadOnlyList<GainSource> Sources, IReadOnlyList<ItemGains> Items);

/// <summary>
/// Attributes every item gain of a run in an Engine's session log to the source that paid it. A quest's rewards arrive between its
/// <c>turnIn</c> and its <c>ccqr</c> packets, and count once the <c>ccqr</c> says it succeeded, together with what the <c>turnIn</c> took.
/// Anything else that arrives, an <c>addItems</c> or a <c>getDrop</c>, is a monster drop picked up. Temporary items are no gain.
/// </summary>
internal static class LogGains
{
    private const string DropsName = "Monster drops";

    /// <summary>Reads <paramref name="file"/>, or else the newest session log of the Engine, for run <paramref name="run"/>, or else its last run.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/>: no such log, or no such run in it.</exception>
    public static async Task<GainsResult> ReadAsync(string? file, int? run, CancellationToken cancellationToken)
    {
        string path = file ?? NewestLog(Cli.Endpoint());
        Dictionary<int, RunTally> runs = [];
        Dictionary<int, string> names = [];
        try
        {
            // The Engine may still be writing it.
            using StreamReader reader = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                Read(line, runs, names);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ControlException(ErrorCode.InvalidArgument, $"There is no session log at {path}.");
        }

        if (runs.Count == 0)
            throw new ControlException(ErrorCode.InvalidArgument, $"{path} has no runs.");
        int number = run ?? runs.Keys.Max();
        if (!runs.TryGetValue(number, out RunTally? tally))
        {
            int[] numbers = [.. runs.Keys.Order()];
            string list = numbers.Length == 1 ? $"run {numbers[0]}" : $"runs {string.Join(", ", numbers[..^1])} and {numbers[^1]}";
            throw new ControlException(ErrorCode.InvalidArgument, $"Run {number} isn't in {path}; it has {list}.");
        }
        return tally.Result(path, number, names);
    }

    /// <summary>The newest of the Engine's session logs, which are named after their start time.</summary>
    private static string NewestLog(EngineEndpoint endpoint) =>
        (Directory.Exists(endpoint.LogFilesDir) ? Directory.GetFiles(endpoint.LogFilesDir, "*.jsonl").Order(StringComparer.Ordinal).LastOrDefault() : null)
        ?? throw new ControlException(ErrorCode.InvalidArgument, $"Engine '{endpoint.Name}' has no session logs in {endpoint.LogFilesDir}.");

    private static void Read(string line, Dictionary<int, RunTally> runs, Dictionary<int, string> names)
    {
        LogEntryDto? entry;
        try
        {
            entry = JsonSerializer.Deserialize<LogEntryDto>(line, ControlJson.Options);
        }
        catch (JsonException)
        {
            return;
        }
        if (entry is null)
            return;

        JsonElement? packet = entry.Kind == LogKind.Flash ? Received(entry.Text) : null;
        if (packet is { } received)
            CollectNames(received, names);
        if (entry.Kind == LogKind.Events && entry.Type == EventTypes.ScriptStarted && entry.Data is { } data)
            CollectNames(data, names);
        if (entry.Run is not { } number)
            return;

        if (!runs.TryGetValue(number, out RunTally? tally))
            runs[number] = tally = new RunTally(entry.Ts);
        tally.Last = entry.Ts;
        if (entry.Type == EventTypes.ScriptStarted && entry.Data?.TryGetProperty("script", out JsonElement script) == true)
            tally.Script ??= script.GetString();
        if (packet is { } o)
            tally.Add(o);
    }

    /// <summary>The <c>b.o</c> object of a packet the game server sent, as the flash log records it: <c>[Net] [ RECEIVED ]: {…}, (len: n)</c>.</summary>
    private static JsonElement? Received(string? text)
    {
        if (text is null || !text.Contains("RECEIVED", StringComparison.Ordinal))
            return null;
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end < start)
            return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text.AsMemory(start, end - start + 1));
            return document.RootElement.TryGetProperty("b", out JsonElement b) && b.ValueKind == JsonValueKind.Object
                && b.TryGetProperty("o", out JsonElement o) && o.ValueKind == JsonValueKind.Object
                ? o.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Item names, which a reward's <c>addItems</c> lacks: from the items of other packets (<c>dropItem</c>, a drop's <c>addItems</c>), and from
    /// what the player held as a run started.
    /// </summary>
    private static void CollectNames(JsonElement source, Dictionary<int, string> names)
    {
        if (source.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty item in items.EnumerateObject())
            {
                if (int.TryParse(item.Name, out int id) && item.Value.ValueKind == JsonValueKind.Object
                    && item.Value.TryGetProperty("sName", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                    names[id] = name.GetString()!;
            }
        }
        foreach (string store in (string[])["inventory", "temp", "bank"])
        {
            if (!source.TryGetProperty(store, out JsonElement held) || held.ValueKind != JsonValueKind.Array)
                continue;
            foreach (JsonElement item in held.EnumerateArray())
            {
                if (item.TryGetProperty("id", out JsonElement id) && id.TryGetInt32(out int itemId)
                    && item.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                    names.TryAdd(itemId, name.GetString()!);
            }
        }
    }

    private static long Number(JsonElement o, string property) =>
        o.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;

    /// <summary>A flag the game sends as true or as 1.</summary>
    private static bool Flag(JsonElement o, string property) =>
        o.TryGetProperty(property, out JsonElement value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.GetDouble() != 0);

    /// <summary>Whether a <c>ccqr</c> says the turn-in succeeded: <c>bSuccess</c> 1, or true.</summary>
    private static bool Succeeded(JsonElement o) =>
        o.TryGetProperty("bSuccess", out JsonElement value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.GetDouble() == 1);

    private static void Add(Dictionary<int, long> counts, int item, long qty) => counts[item] = counts.GetValueOrDefault(item) + qty;

    private sealed class RunTally(long first)
    {
        private readonly Dictionary<int, QuestTally> _quests = [];
        private readonly Dictionary<int, long> _drops = [];

        /// <summary>A turn-in is under way: what arrives is its reward, and <see cref="_taking"/> what it took.</summary>
        private bool _turningIn;
        private readonly Dictionary<int, long> _paying = [];
        private readonly Dictionary<int, long> _taking = [];

        public long Last { get; set; } = first;

        public string? Script { get; set; }

        public void Add(JsonElement o)
        {
            switch (o.TryGetProperty("cmd", out JsonElement cmd) ? cmd.GetString() : null)
            {
                case "turnIn":
                    _turningIn = true;
                    _paying.Clear();
                    _taking.Clear();
                    if (o.TryGetProperty("sItems", out JsonElement sItems) && sItems.ValueKind == JsonValueKind.String)
                    {
                        foreach (string part in sItems.GetString()!.Split(','))
                        {
                            if (part.Split(':') is [string item, string qty] && int.TryParse(item, out int id) && long.TryParse(qty, out long n))
                                LogGains.Add(_taking, id, n);
                        }
                    }
                    break;
                case "addItems" when o.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Object:
                    foreach (JsonProperty item in items.EnumerateObject())
                    {
                        if (int.TryParse(item.Name, out int id) && item.Value.ValueKind == JsonValueKind.Object && !Flag(item.Value, "bTemp"))
                            LogGains.Add(_turningIn ? _paying : _drops, id, Number(item.Value, "iQty"));
                    }
                    break;
                case "getDrop" when !_turningIn && o.TryGetProperty("ItemID", out JsonElement itemId) && itemId.TryGetInt32(out int dropped):
                    LogGains.Add(_drops, dropped, Number(o, "iQty"));
                    break;
                case "ccqr" when _turningIn:
                    _turningIn = false;
                    if (!Succeeded(o) || !o.TryGetProperty("QuestID", out JsonElement questId) || !questId.TryGetInt32(out int quest))
                        break;
                    if (!_quests.TryGetValue(quest, out QuestTally? tally))
                        _quests[quest] = tally = new QuestTally(o.TryGetProperty("sName", out JsonElement name) ? name.GetString() : null, _quests.Count);
                    tally.TurnIns++;
                    foreach ((int id, long qty) in _paying)
                        LogGains.Add(tally.Gained, id, qty);
                    foreach ((int id, long qty) in _taking)
                        LogGains.Add(tally.Spent, id, qty);
                    break;
            }
        }

        public GainsResult Result(string file, int run, IReadOnlyDictionary<int, string> names)
        {
            double hours = (Last - first) / 3.6e6;
            double? PerHour(long qty) => hours > 0 ? qty / hours : null;
            List<ItemCount> Counts(Dictionary<int, long> counts) =>
                [.. counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key).Select(c => new ItemCount(c.Key, names.GetValueOrDefault(c.Key), c.Value, PerHour(c.Value)))];

            List<GainSource> sources =
            [
                .. _quests
                    .OrderByDescending(q => q.Value.TurnIns).ThenBy(q => q.Value.Order)
                    .Select(q => new GainSource(GainSourceKind.Quest, q.Key, q.Value.Name ?? $"quest {q.Key}", q.Value.TurnIns, PerHour(q.Value.TurnIns),
                        Counts(q.Value.Gained), Counts(q.Value.Spent))),
            ];
            if (_drops.Count > 0)
                sources.Add(new GainSource(GainSourceKind.Drops, null, DropsName, null, null, Counts(_drops), []));

            Dictionary<int, long> gained = [];
            Dictionary<int, long> spent = [];
            foreach (GainSource source in sources)
            {
                foreach (ItemCount item in source.Gained)
                    LogGains.Add(gained, item.ItemId, item.Qty);
                foreach (ItemCount item in source.Spent)
                    LogGains.Add(spent, item.ItemId, item.Qty);
            }
            List<ItemGains> items =
            [
                .. gained.Keys.Union(spent.Keys)
                    .Select(id => (Id: id, Gained: gained.GetValueOrDefault(id), Spent: spent.GetValueOrDefault(id)))
                    .OrderByDescending(i => i.Gained - i.Spent).ThenBy(i => i.Id)
                    .Select(i => new ItemGains(i.Id, names.GetValueOrDefault(i.Id), i.Gained, i.Spent, i.Gained - i.Spent, PerHour(i.Gained - i.Spent))),
            ];
            return new GainsResult(file, run, Script, (Last - first) / 1000.0, sources, items);
        }
    }

    /// <param name="Order">How many quests had a turn-in before this one's first: ties in turn-ins keep that order.</param>
    private sealed record QuestTally(string? Name, int Order)
    {
        public int TurnIns { get; set; }

        public Dictionary<int, long> Gained { get; } = [];

        public Dictionary<int, long> Spent { get; } = [];
    }
}
