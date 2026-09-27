using System.Globalization;
using System.Text.Json;
using Skua.Control;
using Skua.Core.Models;

namespace Skua.App.Engine.Scripts;

/// <summary>
/// The record of what each Scripts update added and changed on disk, in <c>&lt;SkuaDIR&gt;/scripts-history.json</c> next to
/// <c>scripts-commit.txt</c>, which <c>scripts_new</c> reads without reaching the Script Source.
/// </summary>
internal sealed class ScriptHistory
{
    /// <summary>The window <c>scripts_new</c> looks back over without a start point.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    /// <summary>The updates kept; older ones are dropped.</summary>
    private const int MaxUpdates = 200;

    private static readonly JsonSerializerOptions Json = new(ControlJson.Options) { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();

    public ScriptHistory(string? path = null)
    {
        _path = path ?? Path.Combine(ClientFileSources.SkuaDIR, "scripts-history.json");
    }

    /// <summary>Records an update that downloaded Scripts.</summary>
    /// <param name="full">Whether it was a full download, which later windows don't count as news.</param>
    public void Record(ScriptSourceDto source, string commit, bool full, IReadOnlyList<ScriptEntry> added, IReadOnlyList<ScriptEntry> changed)
    {
        lock (_lock)
        {
            List<ScriptUpdate> updates = Load();
            updates.Add(new ScriptUpdate(Key(source), commit, DateTimeOffset.UtcNow, full, full ? [] : added, full ? [] : changed));
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(updates.TakeLast(MaxUpdates), Json));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                EngineLog.Write($"Couldn't record the Scripts update in {_path}: {e.Message}");
            }
        }
    }

    /// <summary>The Scripts that updates from <paramref name="source"/> added or changed after <paramref name="since"/>.</summary>
    /// <exception cref="StreamJsonRpc.LocalRpcException"><see cref="ErrorCode.InvalidArgument"/> for a start point that is neither a date nor a recorded commit.</exception>
    public ScriptsNewResult New(ScriptSourceDto source, string? since)
    {
        List<ScriptUpdate> updates;
        lock (_lock)
            updates = Load().Where(u => u.Source == Key(source)).OrderBy(u => u.At).ToList();

        DateTimeOffset start = Start(updates, since);
        List<ScriptUpdate> window = updates.Where(u => u.At > start && !u.Full && (u.Added.Count > 0 || u.Changed.Count > 0)).ToList();
        // A Script added in the window counts as added however often it changed after.
        Dictionary<string, NewScriptDto> scripts = new(StringComparer.Ordinal);
        foreach (ScriptUpdate update in window)
        {
            foreach ((ScriptEntry script, ScriptChange change) in update.Added.Select(s => (s, ScriptChange.Added)).Concat(update.Changed.Select(s => (s, ScriptChange.Changed))))
            {
                bool added = change == ScriptChange.Added || scripts.GetValueOrDefault(script.Path)?.Change == ScriptChange.Added;
                scripts[script.Path] = new NewScriptDto(script.Path, script.Name, added ? ScriptChange.Added : ScriptChange.Changed, update.At, update.Commit);
            }
        }
        return new ScriptsNewResult(source, start, window.Count,
            scripts.Values.OrderByDescending(s => s.At).ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Where the window starts: the given time, or the time of the update that synced to the given commit.</summary>
    private static DateTimeOffset Start(List<ScriptUpdate> updates, string? since)
    {
        if (string.IsNullOrWhiteSpace(since))
            return DateTimeOffset.UtcNow - DefaultWindow;
        since = since.Trim();
        // A short commit, as git abbreviates it; so a year alone is read as a date.
        if (since.Length >= 7 && updates.LastOrDefault(u => u.Commit.StartsWith(since, StringComparison.OrdinalIgnoreCase)) is { } reached)
            return reached.At;
        // Local time, as the user who typed the date means it: the Engine takes its time zone from the CLI that starts it.
        if (DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset time))
            return time;
        throw RpcErrors.Of(ErrorCode.InvalidArgument,
            $"'{since}' is neither a date (e.g. 2026-09-01) nor a commit a Scripts update synced to; 'skua scripts new' lists recent commits.");
    }

    private List<ScriptUpdate> Load()
    {
        try
        {
            return JsonSerializer.Deserialize<List<ScriptUpdate>>(File.ReadAllText(_path), Json) ?? [];
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            EngineLog.Write($"Couldn't read the Scripts update history in {_path}, so it starts afresh: {e.Message}");
            return [];
        }
    }

    private static string Key(ScriptSourceDto source) => $"{source.Owner}/{source.Repo}@{source.Branch}";

    /// <summary>One Script an update touched, with its name from <c>scripts.json</c>.</summary>
    public sealed record ScriptEntry(string Path, string? Name);

    private sealed record ScriptUpdate(string Source, string Commit, DateTimeOffset At, bool Full, IReadOnlyList<ScriptEntry> Added, IReadOnlyList<ScriptEntry> Changed);
}
