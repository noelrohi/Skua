using System.Text;
using System.Text.Json;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>Human-readable renderings of the Control Surface DTOs; <c>--json</c> prints the DTOs themselves.</summary>
internal static class Output
{
    public static JsonSerializerOptions JsonOptions { get; } = new(ControlJson.Options) { WriteIndented = true };

    public static string Status(StatusDto status)
    {
        EngineInfoDto engine = status.Engine;
        GameStatusDto game = status.Game;
        string gameLine = game.GameHostUp
            ? $"Game Host up{(game.State is { } state ? $", {Name(state)}" : "")}{(game.Server is { } server ? $" on {server}" : "")}"
            : "Game Host down";
        return $"""
            Engine  {engine.Name} (pid {engine.Pid}, up {engine.UptimeSec:0} s, build {engine.Build}, protocol {engine.Protocol})
            Game    {gameLine}
            """;
    }

    public static string ScriptsSearch(ScriptsSearchResult result)
    {
        if (result.Matched == 0)
            return $"No Scripts in {Source(result.Source)} match.";

        StringBuilder text = new(result.Matched > result.Scripts.Count
            ? $"{result.Matched} Scripts in {Source(result.Source)} match; showing the first {result.Scripts.Count}, so narrow the search."
            : $"{result.Matched} Scripts in {Source(result.Source)} match.");
        int width = result.Scripts.Max(s => s.Path.Length);
        foreach (ScriptDto script in result.Scripts)
        {
            string state = script.Outdated ? "outdated" : script.Downloaded ? "downloaded" : "missing";
            string tags = script.Tags.Count > 0 ? $" [{string.Join(", ", script.Tags)}]" : "";
            text.AppendLine().Append($"  {script.Path.PadRight(width)}  {state,-10}  {script.Name}{tags}".TrimEnd());
        }
        return text.ToString();
    }

    public static string ScriptsUpdate(ScriptsUpdateResult result)
    {
        string commit = result.Commit.Length > 7 ? result.Commit[..7] : result.Commit;
        string text = result.Mode switch
        {
            ScriptsUpdateMode.Full => $"Downloaded {result.Downloaded} Scripts from {Source(result.Source)} at {commit} (full download).",
            ScriptsUpdateMode.Incremental => $"Downloaded {result.Downloaded} changed Scripts from {Source(result.Source)} at {commit}.",
            _ => $"The Scripts are up to date with {Source(result.Source)} at {commit}.",
        };
        if (result.Failed.Count > 0)
            text += $"\n{result.Failed.Count} failed to download; run 'skua scripts update' again: {string.Join(", ", result.Failed)}";
        return text;
    }

    /// <summary>Only the path, so a script can use it.</summary>
    public static string Screenshot(ScreenshotFile file) => file.Path;

    public static string Engine(EngineStateDto engine) => engine.State switch
    {
        EngineState.Running when engine.Compatible == false =>
            $"Engine '{engine.Name}' is running (pid {engine.Pid}, build {engine.Build}) on protocol {engine.Protocol}, not {ControlProtocol.Version}; run 'skua engine stop'.",
        EngineState.Running => $"Engine '{engine.Name}' is running (pid {engine.Pid}, build {engine.Build}).",
        EngineState.StartingOrHung => $"Engine '{engine.Name}' is starting or hung; its socket {engine.Socket} doesn't answer.",
        _ => $"Engine '{engine.Name}' is stopped.",
    };

    private static string Source(ScriptSourceDto source) => $"{source.Owner}/{source.Repo}@{source.Branch}";


    public const string GapNotice = "gap: entries after the cursor are no longer held (evicted, or the Engine restarted).";

    public static string Logs(LogPage page)
    {
        StringBuilder text = new();
        if (page.Gap)
            text.AppendLine($"-- {GapNotice}");
        foreach (LogEntryDto entry in page.Entries)
            text.AppendLine(Entry(entry));
        return text.Append($"-- next {page.Next}").ToString();
    }

    /// <summary>One line: seq, UTC time, kind and run, then the text or the event type and its data.</summary>
    public static string Entry(LogEntryDto entry)
    {
        string time = DateTimeOffset.FromUnixTimeMilliseconds(entry.Ts).UtcDateTime.ToString("HH:mm:ss.fff");
        string run = entry.Run is { } number ? $" run {number}" : "";
        string body = entry.Text ?? $"{entry.Type} {JsonSerializer.Serialize(entry.Data, ControlJson.Options)}";
        return $"{entry.Seq} {time} {Name(entry.Kind)}{run}{(entry.Truncated ? " (truncated)" : "")} {body}";
    }

    private static string Name<TEnum>(TEnum value) where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
