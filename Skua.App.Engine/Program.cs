using System.Runtime.Versioning;
using Skua.App.Engine;
using Skua.Control;

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

return await Engine.RunAsync(endpoint, detach);
