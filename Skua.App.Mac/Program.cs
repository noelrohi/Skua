using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Skua.App.Mac;
using Skua.Avalonia.Manager;
using Skua.Control;
using Skua.Engine;

[assembly: UnsupportedOSPlatform("windows")]

AppArguments arguments;
EngineEndpoint endpoint;
try
{
    string skuaDir = EngineEndpoint.DefaultSkuaDir();
    arguments = AppArguments.Parse(args, LastEngineName.Read(skuaDir));
    if (arguments.Manager)
        return ManagerApp.Run();
    LastEngineName.Remember(skuaDir, arguments);
    endpoint = EngineEndpoint.FromEnvironment(arguments.Name);
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
// The Skua Manager's "Bring to front": the main window shows again, as the Dock icon shows it.
using PosixSignalRegistration show = PosixSignalRegistration.Create((PosixSignal)AppInstances.ShowSignal, context =>
{
    context.Cancel = true;
    Volatile.Read(ref app)?.RequestShow();
});

// Questions name the thread that raised them: this one runs the window.
Thread.CurrentThread.Name ??= "UI Thread";

// The Engine binds its socket before Avalonia starts any thread: the umask around bind is process-wide (ADR 0006).
HostedEngine? engine = null;
string? failure = null;
int exitCode = EngineExitCodes.Success;
try
{
    engine = HostedEngine.StartAsync(endpoint, App.EngineOptions(arguments)).GetAwaiter().GetResult();
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

App skua = new(endpoint, arguments, engine, failure);
Volatile.Write(ref app, skua);
if (Volatile.Read(ref quitEarly))
    skua.RequestQuit();
AppBuilder.Configure(() => skua).UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime([]);

// On the thread pool: Avalonia's SynchronizationContext outlives its loop on this thread, so an await here would never resume.
return Task.Run(async () => await skua.EngineAsync() is { } hosted ? await hosted.StopAsync() : exitCode).GetAwaiter().GetResult();
