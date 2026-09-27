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
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(new ErrorOutput(new ErrorBody(e.Code, e.Message)), Output.JsonOptions));
            else
                Console.Error.WriteLine($"skua: {e.Message}");
            return ExitCodes.For(e.Code);
        }
    }

    private sealed record ErrorOutput(ErrorBody Error);

    private sealed record ErrorBody(ErrorCode Code, string Message);
}
