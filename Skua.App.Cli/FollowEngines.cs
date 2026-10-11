using System.Text.Json;
using System.Text.Json.Nodes;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>
/// <c>skua logs -f</c> over several Engines, named with <c>--engine a,b,c</c> or every running one with <c>--all-engines</c>: each line is
/// prefixed with its Engine Name (<c>--json</c>: an <c>engine</c> field). The replay is merged by time, then entries print as each Engine pushes
/// them. An Engine that goes away is said on stderr while the rest go on.
/// </summary>
internal static class FollowEngines
{
    /// <summary>The Engine Names of <c>--engine</c>, comma-separated; one name is a list of one.</summary>
    public static string[] Names(string engineOption) => engineOption.Split(',');

    /// <summary>Whether <c>--engine</c> names several Engines.</summary>
    public static bool Several(string? engineOption) => engineOption?.Contains(',') == true;

    /// <param name="names">The Engines to follow, or null for every running Engine in the data folder.</param>
    public static async Task<int> RunAsync(bool json, string[]? names, LogKind[] kinds, int? tail, CancellationToken cancellationToken)
    {
        List<(string Name, EngineConnection Connection)> engines = [];
        try
        {
            try
            {
                engines = await ConnectAsync(names, cancellationToken);
            }
            catch (ControlException e)
            {
                return Cli.Fail(json, e);
            }

            int width = engines.Max(engine => engine.Name.Length);
            object output = new();
            void Print(string engine, LogEntryDto entry)
            {
                string line = json ? Json(engine, entry) : $"{engine.PadRight(width)} {Output.Entry(entry)}";
                lock (output)
                    Console.WriteLine(line);
            }

            // Each Engine's replay, in its seq order; an Engine that goes away meanwhile is said and dropped.
            List<(string Name, EngineConnection Connection, string Cursor, IReadOnlyList<LogEntryDto> Replay)> follows = [];
            foreach ((string name, EngineConnection connection) in engines)
            {
                try
                {
                    Cli.Replay backlog = await Cli.ReplayAsync(connection, kinds, null, tail, cancellationToken);
                    if (backlog.Gap)
                        Console.Error.WriteLine($"skua: {name}: {Output.GapNotice}");
                    follows.Add((name, connection, backlog.Next, backlog.Entries));
                }
                catch (ControlException e)
                {
                    Console.Error.WriteLine($"skua: {name}: {e.Message}");
                }
            }
            if (follows.Count == 0)
                return ExitCodes.For(ErrorCode.EngineUnavailable);
            foreach ((int engine, LogEntryDto entry) in MergeByTime([.. follows.Select(follow => follow.Replay)]))
                Print(follows[engine].Name, entry);

            bool[] lost = await Task.WhenAll(follows.Select(follow =>
                FollowAsync(follow.Name, follow.Connection, kinds, follow.Cursor, [.. follow.Replay.Select(entry => entry.Seq)], Print, cancellationToken)));
            return lost.All(l => l) ? ExitCodes.For(ErrorCode.EngineUnavailable) : ExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Success;
        }
        finally
        {
            foreach ((_, EngineConnection connection) in engines)
                connection.Dispose();
        }
    }

    /// <summary>
    /// Connects to the named Engines, or to every one in the data folder that answers, and only to those that run: it starts and replaces none.
    /// A named one that isn't running, or speaks another protocol, fails the follow; with <c>--all-engines</c> such an Engine is skipped, and at
    /// least one must be left.
    /// </summary>
    private static async Task<List<(string Name, EngineConnection Connection)>> ConnectAsync(string[]? names, CancellationToken cancellationToken)
    {
        string skuaDir = EngineEndpoint.DefaultSkuaDir();
        IEnumerable<EngineEndpoint> endpoints = names is not null
            ? names.Distinct().Select(name => EngineEndpoint.Resolve(name, skuaDir))
            : EngineEndpoint.InDataFolder(skuaDir);
        List<(string, EngineConnection)> connected = [];
        try
        {
            foreach (EngineEndpoint endpoint in endpoints)
            {
                EngineConnection? connection = await EngineClient.TryConnectAsync(endpoint, cancellationToken);
                string? refused = connection is null ? $"Engine '{endpoint.Name}' isn't running."
                    : !connection.IsCompatible ? $"Engine '{endpoint.Name}' speaks protocol {connection.Hello.Protocol}, not {ControlProtocol.Version}."
                    : null;
                if (refused is null)
                {
                    connected.Add((endpoint.Name, connection!));
                    continue;
                }
                connection?.Dispose();
                if (names is not null)
                    throw new ControlException(connection is null ? ErrorCode.EngineUnavailable : ErrorCode.ProtocolMismatch, refused);
                if (connection is not null)
                    Console.Error.WriteLine($"skua: {refused} Skipping it.");
            }
            if (connected.Count == 0)
                throw new ControlException(ErrorCode.EngineUnavailable, $"No Engine in {skuaDir} is running.");
            return connected;
        }
        catch
        {
            foreach ((_, EngineConnection connection) in connected)
                connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The replays of several Engines as one, by time: each step takes the earliest of the Engines' next entries, so each Engine's own entries
    /// stay in seq order even where a later seq carries an earlier time. Ties go to the Engine listed first.
    /// </summary>
    private static IEnumerable<(int Engine, LogEntryDto Entry)> MergeByTime(IReadOnlyList<LogEntryDto>[] replays)
    {
        int[] next = new int[replays.Length];
        while (true)
        {
            int earliest = -1;
            for (int engine = 0; engine < replays.Length; engine++)
            {
                if (next[engine] < replays[engine].Count
                    && (earliest < 0 || replays[engine][next[engine]].Ts < replays[earliest][next[earliest]].Ts))
                    earliest = engine;
            }
            if (earliest < 0)
                yield break;
            yield return (earliest, replays[earliest][next[earliest]++]);
        }
    }

    /// <summary>Prints one Engine's new entries until it goes away, which it says on stderr; returns whether it went away.</summary>
    private static async Task<bool> FollowAsync(
        string name, EngineConnection connection, LogKind[] kinds, string? cursor, HashSet<long> replayed, Action<string, LogEntryDto> print,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (LogPage page in connection.SubscribeAsync(kinds, cursor, cancellationToken))
            {
                if (page.Gap)
                    Console.Error.WriteLine($"skua: {name}: {Output.GapNotice}");
                foreach (LogEntryDto entry in page.Entries)
                {
                    if (!replayed.Remove(entry.Seq))
                        print(name, entry);
                }
            }
            return false;
        }
        catch (ControlException e)
        {
            Console.Error.WriteLine($"skua: {name}: {e.Message}");
            return true;
        }
    }

    /// <summary>The entry as <c>logs -f --json</c> prints it, with its Engine Name first.</summary>
    private static string Json(string engine, LogEntryDto entry)
    {
        JsonObject line = new() { ["engine"] = engine };
        foreach ((string key, JsonNode? value) in JsonSerializer.SerializeToNode(entry, ControlJson.Options)!.AsObject())
            line[key] = value?.DeepClone();
        return line.ToJsonString(ControlJson.Options);
    }
}
