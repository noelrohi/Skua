using System.Diagnostics;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>Waits on the Engine's logs through the Control Surface.</summary>
public static class LogWaits
{
    /// <summary>Waits until at least <paramref name="count"/> entries of the kind are held, and returns them all.</summary>
    public static async Task<List<LogEntryDto>> WaitForLogsAsync(this EngineConnection connection, LogKind kind, int count, Func<LogEntryDto, bool>? match = null)
    {
        List<LogEntryDto> entries = [];
        string? cursor = null;
        Stopwatch waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(20))
        {
            LogPage page = await connection.LogsAsync(kind, cursor, 1000, TestContext.Current.CancellationToken);
            entries.AddRange(page.Entries.Where(match ?? (_ => true)));
            cursor = page.Next;
            if (entries.Count >= count)
                return entries;
            if (page.Entries.Count == 0)
                await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"Only {entries.Count} of {count} {kind} entries arrived.");
    }

    /// <summary>Waits for the first event of <paramref name="type"/> that matches, and returns it.</summary>
    public static async Task<LogEntryDto> WaitForEventAsync(this EngineConnection connection, string type, Func<LogEntryDto, bool>? match = null) =>
        (await connection.WaitForLogsAsync(LogKind.Events, 1, e => e.Type == type && (match?.Invoke(e) ?? true)))[0];
}
