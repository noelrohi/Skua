using System.Runtime.CompilerServices;
using System.Text.Json;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>
/// Reading an Engine's session logs, the JSONL file of each start in <see cref="EngineEndpoint.LogFilesDir"/>, as <c>skua logs gains</c> and
/// <c>skua logs attempts</c> do; and the fields of the entries' JSON data.
/// </summary>
internal static class SessionLog
{
    /// <summary>The Engine's newest session log.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> when it has none.</exception>
    public static string Newest(EngineEndpoint endpoint) =>
        endpoint.NewestLogFile() ?? throw new ControlException(ErrorCode.InvalidArgument, $"Engine '{endpoint.Name}' has no session logs in {endpoint.LogFilesDir}.");

    /// <summary>
    /// The entries of a session log, in file order, skipping lines that aren't entries; with <paramref name="mayHold"/>, only those of the lines
    /// it passes, so a reader that wants a few entries parses no others.
    /// </summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> when there is no such file.</exception>
    public static async IAsyncEnumerable<LogEntryDto> ReadAsync(
        string path, Func<string, bool>? mayHold = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using StreamReader reader = Open(path);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (mayHold is not null && !mayHold(line))
                continue;
            LogEntryDto? entry;
            try
            {
                entry = JsonSerializer.Deserialize<LogEntryDto>(line, ControlJson.Options);
            }
            catch (JsonException)
            {
                continue;
            }
            if (entry is not null)
                yield return entry;
        }
    }

    private static StreamReader Open(string path)
    {
        try
        {
            // The Engine may still be writing it.
            return new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ControlException(ErrorCode.InvalidArgument, $"There is no session log at {path}.");
        }
    }

    /// <summary>A string field, or null when it is missing or another type.</summary>
    public static string? String(JsonElement o, string property) =>
        o.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A whole-number field (a fraction is cut), or null when it is missing or another type.</summary>
    public static long? Long(JsonElement o, string property) =>
        o.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) ? (long)number : null;

    /// <summary>An int field, or null when it is missing, another type or out of range.</summary>
    public static int? Int(JsonElement o, string property) =>
        o.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
}
