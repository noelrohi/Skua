using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Engine;

// Stands in for the Mac App: it parses the app's command line, hosts an Engine as the app does, with the account the Skua Manager launched it
// for, logs that account in (and starts its Script), and quits on SIGTERM. What a test checks lands next to the Engine's lock:
//   engines/<name>.argv    its command line, one argument per line
//   engines/<name>.shown   a line per show signal (SIGUSR1), which the Manager's "Bring to front" sends
//   engines/<name>.login   the login's outcome: "ok <username> <server>" or "failed <message>"
// SKUA_FAKE_APP_SCENARIO, when set, is the fake Game Host's scenario for this app, so it never shares the test process's.
// SKUA_FAKE_APP_PLUGINS=1 loads the plugins in the data folder, as the app does once its window is up, before the login.

AppArguments arguments;
EngineEndpoint endpoint;
try
{
    arguments = AppArguments.Parse(args);
    endpoint = EngineEndpoint.FromEnvironment(arguments.Name);
}
catch (ControlException e)
{
    Console.Error.WriteLine(e.Message);
    return EngineExitCodes.Usage;
}
if (Environment.GetEnvironmentVariable("SKUA_FAKE_APP_SCENARIO") is { Length: > 0 } scenario)
    Environment.SetEnvironmentVariable("SKUA_FAKE_GAMEHOST_SCENARIO", scenario);

Directory.CreateDirectory(endpoint.EnginesDir);
string Output(string kind) => Path.Combine(endpoint.EnginesDir, $"{endpoint.Name}.{kind}");
File.WriteAllLines(Output("argv"), args);

TaskCompletionSource quit = new();
using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    quit.TrySetResult();
});
using PosixSignalRegistration show = PosixSignalRegistration.Create((PosixSignal)30, context =>
{
    context.Cancel = true;
    File.AppendAllLines(Output("shown"), ["show"]);
});

HostedEngine engine;
try
{
    engine = await HostedEngine.StartAsync(endpoint, new EngineHostOptions
    {
        Mode = EngineHostMode.App,
        AccountService = arguments.Account is { } account ? Accounts.ServiceOf(account) : null,
    });
}
catch (EngineStartException e)
{
    Console.Error.WriteLine(e.Message);
    return e.ExitCode;
}

if (Environment.GetEnvironmentVariable("SKUA_FAKE_APP_PLUGINS") == "1")
    engine.Services.GetRequiredService<IPluginManager>().Initialize();

if (arguments.Account is not null)
{
    try
    {
        LoginResult login = await engine.Rpc.LoginAsync(arguments.Server, null, asAgent: false, account: null, CancellationToken.None);
        if (arguments.Script is { } script)
            await engine.Rpc.ScriptStartAsync(script, cancellationToken: CancellationToken.None);
        File.WriteAllText(Output("login"), $"ok {login.Username} {login.Server}");
    }
    catch (Exception e)
    {
        File.WriteAllText(Output("login"), $"failed {e.Message}");
    }
}

await Task.WhenAny(quit.Task, engine.Completion);
return await engine.StopAsync();
