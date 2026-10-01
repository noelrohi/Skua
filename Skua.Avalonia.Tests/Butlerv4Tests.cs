using System.Diagnostics;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Skua.Control;
using Skua.Engine.Tests;

namespace Skua.Avalonia.Tests;

/// <summary>
/// The Scripts' Butlerv4 ("Butlerv4 (TCP)", <c>Tools/Butlerv4</c>) between two Mac Apps, played by <c>fake-app</c>, or two windowless
/// Engines, each with the simulated game: its plugin, <c>LeaderButlerSyncv2.dll</c>, loads in each, the leader's tells the butler its port
/// through a port file, and the butler follows the leader. Each test runs over the Scripts checkout named by <c>SKUA_SCRIPTS_CHECKOUT</c>, whose DLL and Scripts it uses as
/// they are, and is skipped without one.
/// </summary>
/// <remarks>
/// The apps and Engines run as a Mac user whose home is a throwaway folder (<c>CFFIXED_USER_HOME</c>), with that home's default data folder, so what
/// the Scripts and the plugin put in the Mac's application data folder stays there.
/// </remarks>
public sealed class Butlerv4Tests : IAsyncDisposable
{
    private const string PluginFile = "LeaderButlerSyncv2.dll";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _home = Path.Combine("/tmp", "skua-home-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<Process> _apps = [];

    /// <summary>The Mac's application data folder for the throwaway home, as <c>SpecialFolder.ApplicationData</c> gives it.</summary>
    private string ApplicationData => Path.Combine(_home, "Library", "Application Support");

    /// <summary>The default data folder for the throwaway home, where Skua and the Scripts both put Skua's files.</summary>
    private string SkuaDir => Path.Combine(ApplicationData, "Skua");

    /// <summary>Names the Scripts checkout, as for the Engine tests' compile check.</summary>
    private const string CheckoutVariable = "SKUA_SCRIPTS_CHECKOUT";

    private static string? Checkout => Environment.GetEnvironmentVariable(CheckoutVariable) is { Length: > 0 } checkout ? checkout : null;

    [Fact]
    public async Task DownloadDLL_puts_the_plugin_in_the_Mac_Apps_plugins_folder()
    {
        SkipWithoutCheckout();
        UseCheckout();
        (_, EngineConnection app) = await StartAsync("installer", "InstallTester", Host.App);
        using (app)
        {
            await CheckApplicationDataAsync(app);
            await app.ScriptStartAsync("Tools/Butlerv4/DownloadDLL.cs", cancellationToken: Ct);
            await app.ScriptWaitAsync(60, Ct);

            // From the Scripts' own copy, into the plugins folder the app loads plugins from at its start.
            string installed = Path.Combine(SkuaDir, "plugins", PluginFile);
            Assert.True(File.Exists(installed), $"{PluginFile} isn't in {Path.GetDirectoryName(installed)}");
            Assert.Equal(File.ReadAllBytes(Path.Combine(Checkout!, "Tools", "Butlerv4", PluginFile)), File.ReadAllBytes(installed));
            await app.WaitForLogsAsync(LogKind.Script, 1, e => e.Text!.Contains($"{PluginFile} Downloaded successfully", StringComparison.Ordinal));
        }
    }

    [Fact]
    public Task Butlerv4_follows_its_leader_through_the_LeaderButlerSyncv2_plugin_in_two_apps() => FollowsItsLeaderAsync(Host.AppWithPlugins);

    [Fact]
    public Task Butlerv4_follows_its_leader_through_the_LeaderButlerSyncv2_plugin_in_two_windowless_Engines() => FollowsItsLeaderAsync(Host.Engine);

    private async Task FollowsItsLeaderAsync(Host host)
    {
        SkipWithoutCheckout();
        UseCheckout();
        InstallPluginOnLoopback();
        (string leaderControl, EngineConnection leader) = await StartAsync("leader", "LeaderTester", host);
        (_, EngineConnection butler) = await StartAsync("butler", "ButlerTester", host);
        using (leader)
        using (butler)
        {
            // Both loaded the plugin, which listens on a port of its own.
            int leaderPort = await ListeningPortAsync(leader);
            int butlerPort = await ListeningPortAsync(butler);
            Assert.NotEqual(leaderPort, butlerPort);

            await CheckApplicationDataAsync(leader);
            await CheckApplicationDataAsync(butler);
            await leader.LoginAsync(cancellationToken: Ct);
            await butler.LoginAsync(cancellationToken: Ct);

            // Once its player has loaded, the leader's plugin writes its port under its username, where the butler's reads it.
            await leader.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == $"[LeaderButlerSync] Port file written: LeaderTester.port → {leaderPort}");
            Assert.Equal(leaderPort.ToString(), File.ReadAllText(Path.Combine(SkuaDir, "character_locations", "LeaderTester.port")));

            await butler.ScriptStartAsync("Tools/Butlerv4/Butlerv4.cs", new Dictionary<string, string>
            {
                ["Leader1Name"] = "LeaderTester",
                ["Leader1Butlers"] = "ButlerTester",
                ["AutoEnhance"] = "false",
            }, cancellationToken: Ct);
            try
            {
                await leader.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "[LeaderButlerSync] Butler 'ButlerTester' connected. Total: 1");
                // Both stand in battleon's Enter, where the game puts them at login, so the butler stays put through a few broadcasts.
                await Task.Delay(1500, Ct);
                Assert.DoesNotContain("goto LeaderTester", Calls("butler"));

                // The leader moves; the butler reads where from the leader's broadcasts and goes to it.
                await File.AppendAllTextAsync(leaderControl, "join yulgar\n", Ct);
                await WaitForAsync(() => Calls("butler").Contains("goto LeaderTester"), "the butler's goto to its leader");
            }
            finally
            {
                await butler.ScriptStopAsync(Ct);
            }
        }
    }

    private static void SkipWithoutCheckout() =>
        Assert.SkipWhen(Checkout is null, $"Set {CheckoutVariable} to a Scripts checkout to run Butlerv4 and its plugin.");

    /// <summary>The checkout as the data folder's Scripts, as a Scripts update leaves it; copied, since compiling writes into it.</summary>
    private void UseCheckout()
    {
        string scripts = Path.Combine(SkuaDir, "Scripts");
        foreach (string file in Directory.EnumerateFiles(Checkout!, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(Checkout!, file);
            if (relative.Split(Path.DirectorySeparatorChar)[0] == ".git")
                continue;
            string target = Path.Combine(scripts, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>
    /// Puts the checkout's plugin in the plugins folder, changed only to listen on the loopback address instead of every address, so the
    /// test opens no port to the network and macOS's firewall never asks about it. The butlers it serves are on the same Mac either way.
    /// </summary>
    private void InstallPluginOnLoopback()
    {
        string plugins = Directory.CreateDirectory(Path.Combine(SkuaDir, "plugins")).FullName;
        using AssemblyDefinition plugin = AssemblyDefinition.ReadAssembly(Path.Combine(Checkout!, "Tools", "Butlerv4", PluginFile));
        List<Instruction> any = [.. AllTypes(plugin.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference { Name: "Any", DeclaringType.FullName: "System.Net.IPAddress" })];
        Assert.Single(any);
        FieldReference field = (FieldReference)any[0].Operand;
        any[0].Operand = new FieldReference("Loopback", field.FieldType, field.DeclaringType);
        plugin.Write(Path.Combine(plugins, PluginFile));
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) =>
        types.SelectMany(t => AllTypes(t.NestedTypes).Prepend(t));

    private enum Host
    {
        /// <summary>A <c>fake-app</c> that loads no plugins.</summary>
        App,
        /// <summary>A <c>fake-app</c> that loads the plugins, as the Mac App does once its window is up.</summary>
        AppWithPlugins,
        /// <summary><c>skua-engine</c>, which loads the plugins as it starts.</summary>
        Engine,
    }

    /// <summary>
    /// Starts an app or a windowless Engine for the Engine Name <paramref name="name"/>, whose Test Account is <paramref name="username"/> in a
    /// Keychain of its own, and returns the file whose lines make its simulated game act, and a connection to its Engine.
    /// </summary>
    private async Task<(string Control, EngineConnection Connection)> StartAsync(string name, string username, Host host)
    {
        string dir = Directory.CreateDirectory(Path.Combine(_home, name)).FullName;
        FakeKeychain keychain = new(dir, username, $"{name}-Sekrit-3b9e");
        string control = Path.Combine(dir, "gamehost.control");
        string scenario = Path.Combine(dir, "gamehost.scenario");
        File.WriteAllLines(scenario,
        [
            $"calllog {Path.Combine(dir, "gamehost.calls")}",
            $"control {control}",
            $"game {keychain.Username} {keychain.Password}",
            $"servers {FakeServer.ListJson(AppEngine.Servers)}",
            """send E <invoke name="loaded" returntype="xml"><arguments></arguments></invoke>""",
        ]);

        // The fake Game Host and its SWF are this process's, as AppEngine set them.
        string executable = host == Host.Engine ? EngineSandbox.EngineExecutable : Path.Combine(AppContext.BaseDirectory, "fake-app");
        ProcessStartInfo start = new(executable) { RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("--name");
        start.ArgumentList.Add(name);
        start.Environment["CFFIXED_USER_HOME"] = _home;
        start.Environment[EngineEndpoint.SkuaDirVariable] = SkuaDir;
        start.Environment.Remove(EngineEndpoint.SocketVariable);
        if (host == Host.Engine)
        {
            start.Environment["SKUA_FAKE_GAMEHOST_SCENARIO"] = scenario;
        }
        else
        {
            start.Environment["SKUA_FAKE_APP_SCENARIO"] = scenario;
            start.Environment["SKUA_FAKE_APP_PLUGINS"] = host == Host.AppWithPlugins ? "1" : "0";
        }
        foreach ((string key, string value) in keychain.Environment())
            start.Environment[key] = value;
        Process app = Process.Start(start)!;
        _apps.Add(app);
        _ = app.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        Task<string> errors = app.StandardError.ReadToEndAsync();

        EngineEndpoint endpoint = EngineEndpoint.Resolve(name, SkuaDir);
        Stopwatch waited = Stopwatch.StartNew();
        while (waited.Elapsed < EngineSandbox.StopTimeout)
        {
            if (await EngineClient.TryConnectAsync(endpoint, Ct) is { } connection)
                return (control, connection);
            if (app.HasExited)
                throw new InvalidOperationException($"{Path.GetFileName(executable)} {name} exited with {app.ExitCode}: {await errors}");
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException($"{Path.GetFileName(executable)} {name} didn't answer.");
    }

    /// <summary>Checks the app sees the throwaway home's application data folder, before anything is written to it.</summary>
    private async Task CheckApplicationDataAsync(EngineConnection app)
    {
        EvalResult result = await app.EvalAsync("return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);", cancellationToken: Ct);
        Assert.Null(result.Error);
        Assert.Equal(ApplicationData, result.Value?.GetString());
    }

    private static async Task<int> ListeningPortAsync(EngineConnection app)
    {
        const string prefix = "[LeaderButlerSync] Listening on port ";
        string line = (await app.WaitForLogsAsync(LogKind.Script, 1, e => e.Text!.StartsWith(prefix, StringComparison.Ordinal)))[0].Text!;
        return int.Parse(line[prefix.Length..]);
    }

    private string[] Calls(string name)
    {
        string log = Path.Combine(_home, name, "gamehost.calls");
        return File.Exists(log) ? File.ReadAllLines(log) : [];
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(100, Ct);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    /// <summary>Quits each app or Engine as the Skua Manager's Stop does, with SIGTERM, and deletes the throwaway home.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (Process app in _apps)
            {
                if (!app.HasExited)
                    kill(app.Id, 15);
            }
            foreach (Process app in _apps)
            {
                using CancellationTokenSource timeout = new(EngineSandbox.StopTimeout);
                try
                {
                    await app.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    app.Kill(entireProcessTree: true);
                    await app.WaitForExitAsync();
                }
                app.Dispose();
            }
        }
        finally
        {
            EngineSandbox.DeleteFolder(_home);
        }
    }
}
