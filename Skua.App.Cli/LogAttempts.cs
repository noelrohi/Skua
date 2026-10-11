using System.Globalization;
using System.Text.Json;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>A death of an account in an Attempt, in seconds since its Attempt started.</summary>
public sealed record AttemptDeath(double AtSec);

/// <summary>
/// One account's <c>ultra.attempt</c> report of an Attempt: its Engine, class and Role, how its Attempt ended, its deaths, and what it wore
/// (<c>loadout</c>, as the Script reported it, or null).
/// </summary>
/// <param name="StartedAt">When it engaged the boss, in UTC milliseconds since the Unix epoch; <paramref name="EndedAt"/> likewise.</param>
public sealed record AttemptAccount(
    string Engine, string? Class, string? Role, string? Outcome, long StartedAt, long? EndedAt, double? DurationSec, IReadOnlyList<AttemptDeath> Deaths,
    long? BossHp, long? BossMaxHp, int? Run, string? Script, JsonElement? Loadout);

/// <summary>
/// One Attempt at an Ultra boss: the reports of the accounts that fought it, the same boss and Comp started within a few seconds. It started with the
/// first account and ended with the last; its outcome is <c>kill</c> when any account saw the boss die, else <c>wipe</c> when any wiped, else
/// <c>stopped</c>; its boss HP is the lowest any account saw.
/// </summary>
public sealed record Attempt(
    string? Boss, string? Comp, string? Outcome, long StartedAt, long? EndedAt, double? DurationSec, long? BossHp, long? BossMaxHp, IReadOnlyList<AttemptAccount> Accounts);

/// <summary>A session log <c>skua logs attempts</c> read, and the Engine it is of.</summary>
public sealed record AttemptLog(string Engine, string File);

/// <summary><c>skua logs attempts</c>: the Attempts of the logs read, oldest first, and how many reports were cut too short to read.</summary>
public sealed record AttemptsResult(IReadOnlyList<AttemptLog> Logs, IReadOnlyList<Attempt> Attempts, int Unreadable);

/// <summary>
/// Reads the <c>ultra.attempt</c> Script Reports that the Ultra boss Scripts record per account per Attempt, from the newest session log of each
/// Engine, and groups them into Attempts across accounts.
/// </summary>
internal static class LogAttempts
{
    public const string ReportName = "ultra.attempt";

    /// <summary>How far apart the accounts of one Attempt may start: they engage the boss together, give or take their own pace.</summary>
    private const long SameAttemptMs = 15_000;

    private static readonly string[] OutcomeOrder = ["kill", "wipe", "stopped"];

    /// <summary>One account's report, with the boss and Comp that group it.</summary>
    private sealed record Report(string? Boss, string? Comp, AttemptAccount Account);

    /// <summary>
    /// Reads <paramref name="files"/>, or else the newest session log of each named Engine, or with <paramref name="allEngines"/> of every Engine
    /// with session logs, or else of the Engine <c>--engine</c> names.
    /// </summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/>: no such log, or an Engine without one.</exception>
    public static async Task<AttemptsResult> ReadAsync(string[] files, string[]? engines, bool allEngines, CancellationToken cancellationToken)
    {
        string skuaDir = EngineEndpoint.DefaultSkuaDir();
        List<(string? Engine, string File)> logs = [];
        if (files.Length > 0)
            logs.AddRange(files.Select(file => ((string?)null, Path.GetFullPath(file))));
        else
        {
            IReadOnlyList<EngineEndpoint> endpoints = allEngines ? EngineEndpoint.WithLogFiles(skuaDir)
                : engines is null ? [Cli.Endpoint()]
                : [.. engines.Distinct().Select(name => EngineEndpoint.Resolve(name, skuaDir))];
            if (endpoints.Count == 0)
                throw new ControlException(ErrorCode.InvalidArgument, $"No Engine has session logs in {EngineEndpoint.LogFilesRoot(skuaDir)}.");
            logs.AddRange(endpoints.Select(endpoint => ((string?)endpoint.Name, SessionLog.Newest(endpoint))));
        }

        List<AttemptLog> read = [];
        List<Report> reports = [];
        int unreadable = 0;
        foreach ((string? engine, string file) in logs)
        {
            (string name, List<Report> found, int cut) = await ReadLogAsync(engine, file, cancellationToken);
            read.Add(new AttemptLog(name, file));
            reports.AddRange(found);
            unreadable += cut;
        }
        return new AttemptsResult(read, Group(reports), unreadable);
    }

    /// <summary>
    /// The reports in one log, and how many couldn't be read: cut to 64 KB, or without a start time. The Engine is the one named, else the one
    /// whose session log folder (<c>engines/logs/&lt;name&gt;/</c>) holds the log, else the one its <c>engine.started</c> names, else the log file's
    /// name.
    /// </summary>
    private static async Task<(string Engine, List<Report> Reports, int Unreadable)> ReadLogAsync(string? engine, string file, CancellationToken cancellationToken)
    {
        string folder = Path.GetDirectoryName(file)!;
        engine ??= Path.GetFileName(Path.GetDirectoryName(folder)) == "logs" && EngineName.IsValid(Path.GetFileName(folder)) ? Path.GetFileName(folder) : null;
        string? started = null;
        List<(int? Run, string? Script, JsonElement Data)> found = [];
        int unreadable = 0;
        static bool MayHold(string line) =>
            line.Contains(EventTypes.ScriptReport, StringComparison.Ordinal) || line.Contains(EventTypes.EngineStarted, StringComparison.Ordinal);
        await foreach (LogEntryDto entry in SessionLog.ReadAsync(file, MayHold, cancellationToken))
        {
            if (entry is not { Kind: LogKind.Events, Data: { ValueKind: JsonValueKind.Object } data })
                continue;
            if (entry.Type == EventTypes.EngineStarted)
                started ??= SessionLog.String(data, "name");
            else if (entry.Type == EventTypes.ScriptReport && SessionLog.String(data, "name") == ReportName)
            {
                if (ReportData(data) is { } report)
                    found.Add((SessionLog.Int(data, "run"), SessionLog.String(data, "script"), report));
                else
                    unreadable++;
            }
        }

        engine ??= started ?? Path.GetFileNameWithoutExtension(file);
        List<Report> reports = [];
        foreach ((int? run, string? script, JsonElement data) in found)
        {
            long? endedAt = Time(data, "endedAt");
            if (Time(data, "startedAt") is not { } start)
            {
                unreadable++;
                continue;
            }
            List<AttemptDeath> deaths = [];
            if (data.TryGetProperty("deaths", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement death in list.EnumerateArray())
                {
                    if ((death.ValueKind == JsonValueKind.Object && death.TryGetProperty("atSec", out JsonElement at) ? at : death) is { ValueKind: JsonValueKind.Number } seconds)
                        deaths.Add(new AttemptDeath(seconds.GetDouble()));
                }
            }
            AttemptAccount account = new(engine, SessionLog.String(data, "class"), SessionLog.String(data, "role"), SessionLog.String(data, "outcome"),
                start, endedAt, endedAt is { } end ? (end - start) / 1000.0 : null, deaths, SessionLog.Long(data, "bossHp"), SessionLog.Long(data, "bossMaxHp"),
                run, script, data.TryGetProperty("loadout", out JsonElement loadout) && loadout.ValueKind != JsonValueKind.Null ? loadout.Clone() : null);
            reports.Add(new Report(SessionLog.String(data, "boss"), SessionLog.String(data, "comp"), account));
        }
        return (engine, reports, unreadable);
    }

    /// <summary>A report's data: the object, or the object a string holds; null for data cut to 64 KB, which no longer parses.</summary>
    private static JsonElement? ReportData(JsonElement report)
    {
        if (!report.TryGetProperty("data", out JsonElement data))
            return null;
        if (data.ValueKind == JsonValueKind.Object)
            return data.Clone();
        if (data.ValueKind != JsonValueKind.String)
            return null;
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(data.GetString()!);
            return parsed.RootElement.ValueKind == JsonValueKind.Object ? parsed.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Groups reports into Attempts, oldest first: a report joins the Attempt of the same boss and Comp that its Engine has no report in yet, and
    /// whose first account started at most <see cref="SameAttemptMs"/> before it.
    /// </summary>
    private static List<Attempt> Group(List<Report> reports)
    {
        List<(string? Boss, string? Comp, List<AttemptAccount> Accounts)> groups = [];
        foreach ((string? boss, string? comp, AttemptAccount account) in reports.OrderBy(r => r.Account.StartedAt))
        {
            (string? Boss, string? Comp, List<AttemptAccount> Accounts) group = groups.LastOrDefault(g =>
                g.Boss == boss && g.Comp == comp && account.StartedAt - g.Accounts[0].StartedAt <= SameAttemptMs && g.Accounts.All(a => a.Engine != account.Engine));
            if (group.Accounts is null)
                groups.Add((boss, comp, [account]));
            else
                group.Accounts.Add(account);
        }

        return [.. groups.Select(g =>
        {
            List<AttemptAccount> accounts = [.. g.Accounts.OrderBy(a => a.Engine, StringComparer.Ordinal)];
            long start = accounts.Min(a => a.StartedAt);
            long? end = accounts.Max(a => a.EndedAt);
            string? outcome = OutcomeOrder.FirstOrDefault(o => accounts.Any(a => a.Outcome == o)) ?? accounts.Select(a => a.Outcome).FirstOrDefault(o => o is not null);
            long? bossHp = outcome == "kill" ? 0 : accounts.Min(a => a.BossHp);
            return new Attempt(g.Boss, g.Comp, outcome, start, end, end is { } e ? (e - start) / 1000.0 : null, bossHp, accounts.Max(a => a.BossMaxHp), accounts);
        })];
    }

    /// <summary>A time the Script gave as an ISO 8601 timestamp (UTC unless it says otherwise) or as epoch milliseconds, in epoch milliseconds.</summary>
    private static long? Time(JsonElement o, string property)
    {
        if (!o.TryGetProperty(property, out JsonElement value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double ms))
            return (long)ms;
        if (value.ValueKind != JsonValueKind.String)
            return null;
        string text = value.GetString()!;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
            return number;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time)
            ? time.ToUnixTimeMilliseconds()
            : null;
    }
}
