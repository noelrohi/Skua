using System.Runtime.Versioning;
using Avalonia;
using Skua.App.Mac;
using Skua.Control;
using Skua.Engine;

[assembly: UnsupportedOSPlatform("windows")]

string name = EngineName.Default;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--name" when i + 1 < args.Length:
            name = args[++i];
            break;
        default:
            Console.Error.WriteLine("usage: Skua [--name <engine-name>]");
            return EngineExitCodes.Usage;
    }
}

EngineEndpoint endpoint;
try
{
    endpoint = EngineEndpoint.FromEnvironment(name);
}
catch (ControlException e)
{
    Console.Error.WriteLine(e.Message);
    return EngineExitCodes.Usage;
}

// The Engine binds its socket before Avalonia starts any thread: the umask around bind is process-wide (ADR 0006).
HostedEngine? engine = null;
string? failure = null;
int exitCode = EngineExitCodes.Success;
try
{
    engine = HostedEngine.StartAsync(endpoint, new EngineHostOptions { Mode = EngineHostMode.App }).GetAwaiter().GetResult();
}
catch (EngineStartException e)
{
    failure = e.ExitCode == EngineExitCodes.AlreadyRunning
        ? $"Another Skua Engine named '{endpoint.Name}' is already running, so this window can't start its own. Stop it with `skua engine stop{(endpoint.Name == EngineName.Default ? "" : $" --name {endpoint.Name}")}`, then open Skua again."
        : e.Message;
    exitCode = e.ExitCode;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
{
    // E.g. the Frame Buffer or the socket couldn't be made: say so in a window, as there is no terminal to read.
    failure = $"Skua's Engine didn't start: {e.Message}";
    exitCode = 1;
}

AppBuilder.Configure(() => new App(engine, failure)).UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime([]);

if (engine is not null)
    exitCode = engine.StopAsync().GetAwaiter().GetResult();
return exitCode;
