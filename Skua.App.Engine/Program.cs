using System.Runtime.Versioning;
using Skua.App.Engine;
using Skua.Control;
using Skua.Engine;

[assembly: UnsupportedOSPlatform("windows")]

string name = EngineName.Default;
bool detach = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--name" when i + 1 < args.Length:
            name = args[++i];
            break;
        case "--detach":
            detach = true;
            break;
        default:
            Console.Error.WriteLine("usage: skua-engine [--name <engine-name>] [--detach]");
            return EngineExitCodes.Usage;
    }
}

if (detach)
    Detach.FromTerminal();

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

HostedEngine engine;
try
{
    engine = await HostedEngine.StartAsync(endpoint, new EngineHostOptions
    {
        Mode = EngineHostMode.Headless,
        // Before anything touches Console, and only once the lock is ours, so an auto-start that lost the race leaves the log alone.
        LockAcquired = detach ? e => Detach.RedirectStdio(e.LogPath) : null,
    });
}
catch (EngineStartException e)
{
    // An auto-start that lost the race stays quiet; its client connects to the running Engine. The Engine has logged any other failure.
    if (e.ExitCode == EngineExitCodes.AlreadyRunning && !detach)
        Console.Error.WriteLine(e.Message);
    return e.ExitCode;
}
return await engine.Completion;
