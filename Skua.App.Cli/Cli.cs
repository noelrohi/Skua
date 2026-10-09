using System.Text.Json;
using System.Text.Json.Serialization;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>Runs one command against the Engine and turns its result or failure into output and an exit code.</summary>
internal static class Cli
{
    /// <summary>The Engine Name given with <c>--engine</c>, or null.</summary>
    public static string? EngineName { get; set; }

    /// <summary>
    /// The Engine every command talks to: the one <c>--engine</c> names, at its own socket even when <c>SKUA_ENGINE_SOCKET</c> is set, or else
    /// the one at that socket, named after its file, or else the default one.
    /// </summary>
    public static EngineEndpoint Endpoint() => EngineName is { } name
        ? EngineEndpoint.Resolve(name, EngineEndpoint.DefaultSkuaDir())
        : EngineEndpoint.FromEnvironment();

    /// <summary>
    /// How every command connects: it replaces an idle Engine from another build, and says so on stderr. A command that only reads keeps one that
    /// speaks this protocol version, and answers from it.
    /// </summary>
    public static EngineClientOptions Options(bool reads = false) => new()
    {
        Endpoint = Endpoint(),
        ReplaceStale = true,
        KeepCompatibleStale = reads,
        Notice = line => Console.Error.WriteLine($"skua: {line}"),
    };

    /// <summary>The <c>skua</c> command line that runs <paramref name="command"/> on the Engine named <paramref name="engine"/>.</summary>
    public static string Command(string engine, string command) =>
        engine == Control.EngineName.Default ? $"skua {command}" : $"skua --engine {engine} {command}";

    /// <param name="exitCode">The exit code for a result, for a command whose result can be a failure; success by default.</param>
    /// <param name="reads">Whether the command only reads, so it keeps a compatible Engine from another build; see <see cref="Options"/>.</param>
    public static async Task<int> RunAsync<T>(
        bool json, Func<EngineClientOptions, Task<T>> command, Func<T, string> human, Func<T, int>? exitCode = null, bool reads = false)
    {
        try
        {
            T result = await command(Options(reads));
            Console.WriteLine(json ? JsonSerializer.Serialize(result, Output.JsonOptions) : human(result));
            return exitCode?.Invoke(result) ?? ExitCodes.Success;
        }
        catch (ControlException e)
        {
            return Fail(json, e);
        }
    }

    /// <summary>
    /// Replays the entries of the given kinds after the cursor, or with <paramref name="tail"/> only the newest that many of them, then prints new
    /// ones as they arrive until interrupted: one line each, or one JSON entry per line with <c>--json</c>. A gap is reported on stderr.
    /// </summary>
    public static async Task<int> FollowLogsAsync(bool json, LogKind[] kinds, string? after, int? tail, CancellationToken cancellationToken)
    {
        try
        {
            using EngineConnection connection = await EngineClient.ConnectAsync(Options(reads: true), cancellationToken);
            HashSet<long> replayed = [];
            if (tail is { } newest)
            {
                // A tail reads one kind, so several are read one after another and merged. Following on from the first one's cursor misses no
                // entry recorded in between; one a later tail already printed is skipped.
                List<LogPage> pages = [];
                foreach (LogKind kind in kinds)
                    pages.Add(await connection.LogsAsync(kind, after, null, newest, cancellationToken));
                if (pages.Any(page => page.Gap))
                    Console.Error.WriteLine($"skua: {Output.GapNotice}");
                foreach (LogEntryDto entry in pages.SelectMany(page => page.Entries).OrderBy(entry => entry.Seq).TakeLast(newest))
                {
                    replayed.Add(entry.Seq);
                    Print(entry);
                }
                after = pages[0].Next;
            }
            await foreach (LogPage page in connection.SubscribeAsync(kinds, after, cancellationToken))
            {
                if (page.Gap)
                    Console.Error.WriteLine($"skua: {Output.GapNotice}");
                foreach (LogEntryDto entry in page.Entries)
                {
                    if (!replayed.Remove(entry.Seq))
                        Print(entry);
                }
            }
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Success;
        }
        catch (ControlException e)
        {
            return Fail(json, e);
        }

        void Print(LogEntryDto entry) => Console.WriteLine(json ? JsonSerializer.Serialize(entry, ControlJson.Options) : Output.Entry(entry));
    }

    /// <summary>
    /// Brings the Scripts up to date before <c>skua script start</c>, and says so in one line when it downloaded any: on stdout, or on stderr with
    /// <c>--json</c> so stdout stays the start's result. When the Script Source can't be reached it warns, and the start goes on with the Scripts on
    /// disk.
    /// </summary>
    public static async Task UpdateBeforeStartAsync(EngineConnection connection, bool json, CancellationToken cancellationToken)
    {
        try
        {
            ScriptsUpdateResult update = await connection.ScriptsUpdateAsync(cancellationToken);
            if (Output.StartUpdate(update) is { } line)
                (json ? Console.Error : Console.Out).WriteLine(json ? $"skua: {line}" : line);
        }
        // The start refuses the same way, and says why.
        catch (ControlException e) when (e.Code == ErrorCode.ScriptRunning)
        {
        }
        catch (ControlException e) when (e.Code is not (ErrorCode.EngineUnavailable or ErrorCode.ProtocolMismatch))
        {
            Console.Error.WriteLine($"skua: couldn't update the Scripts ({e.Message}); starting the local copy.");
        }
    }

    public static int Fail(bool json, ControlException e)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new ErrorOutput(new ErrorBody(e.Code, e.Message, e.Diagnostics)), Output.JsonOptions));
        else
            Console.Error.WriteLine($"skua: {e.Message}");
        return ExitCodes.For(e.Code);
    }

    private sealed record ErrorOutput(ErrorBody Error);

    private sealed record ErrorBody(
        ErrorCode Code, string Message, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Diagnostics);
}
