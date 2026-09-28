using System.Runtime.InteropServices;
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

// SIGTERM (and Ctrl-C in a terminal) quits as Cmd-Q does, without asking: the Engine stops cleanly after the window goes.
App? app = null;
bool quitEarly = false;
void OnSignal(PosixSignalContext context)
{
    context.Cancel = true;
    if (Volatile.Read(ref app) is { } running)
        running.RequestQuit();
    else
        Volatile.Write(ref quitEarly, true);
}
using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
using PosixSignalRegistration sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

// The Engine binds its socket before Avalonia starts any thread: the umask around bind is process-wide (ADR 0006).
HostedEngine? engine = null;
string? failure = null;
int exitCode = EngineExitCodes.Success;
try
{
    engine = HostedEngine.StartAsync(endpoint, new EngineHostOptions { Mode = EngineHostMode.App }).GetAwaiter().GetResult();
}
catch (EngineStartException e) when (e.ExitCode == EngineExitCodes.AlreadyRunning)
{
    // Another Engine holds the name: the app offers to take it over.
}
catch (EngineStartException e)
{
    failure = e.Message;
    exitCode = e.ExitCode;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
{
    // E.g. the Frame Buffer or the socket couldn't be made: say so in a window, as there is no terminal to read.
    failure = $"Skua's Engine didn't start: {e.Message}";
    exitCode = 1;
}

App skua = new(endpoint, engine, failure);
Volatile.Write(ref app, skua);
if (Volatile.Read(ref quitEarly))
    skua.RequestQuit();
AppBuilder.Configure(() => skua).UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime([]);

// On the thread pool: Avalonia's SynchronizationContext outlives its loop on this thread, so an await here would never resume.
return Task.Run(async () => await skua.EngineAsync() is { } hosted ? await hosted.StopAsync() : exitCode).GetAwaiter().GetResult();
