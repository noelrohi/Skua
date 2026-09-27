using System.Text.Json;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>Runs one command against the Engine and turns its result or failure into output and an exit code.</summary>
internal static class Cli
{
    public static async Task<int> RunAsync<T>(bool json, Func<EngineClientOptions, Task<T>> command, Func<T, string> human)
    {
        try
        {
            T result = await command(new EngineClientOptions { Endpoint = EngineEndpoint.FromEnvironment() });
            Console.WriteLine(json ? JsonSerializer.Serialize(result, Output.JsonOptions) : human(result));
            return ExitCodes.Success;
        }
        catch (ControlException e)
        {
            return Fail(json, e);
        }
    }

    /// <summary>
    /// Replays the entries of one kind after the cursor, then prints new ones as they arrive until interrupted:
    /// one line each, or one JSON entry per line with <c>--json</c>. A gap is reported on stderr.
    /// </summary>
    public static async Task<int> FollowLogsAsync(bool json, LogKind kind, string? after, CancellationToken cancellationToken)
    {
        try
        {
            using EngineConnection connection = await EngineClient.ConnectAsync(
                new EngineClientOptions { Endpoint = EngineEndpoint.FromEnvironment() }, cancellationToken);
            await foreach (LogPage page in connection.SubscribeAsync([kind], after, cancellationToken))
            {
                if (page.Gap)
                    Console.Error.WriteLine($"skua: {Output.GapNotice}");
                foreach (LogEntryDto entry in page.Entries)
                    Console.WriteLine(json ? JsonSerializer.Serialize(entry, ControlJson.Options) : Output.Entry(entry));
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
    }

    private static int Fail(bool json, ControlException e)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new ErrorOutput(new ErrorBody(e.Code, e.Message)), Output.JsonOptions));
        else
            Console.Error.WriteLine($"skua: {e.Message}");
        return ExitCodes.For(e.Code);
    }

    private sealed record ErrorOutput(ErrorBody Error);

    private sealed record ErrorBody(ErrorCode Code, string Message);
}
