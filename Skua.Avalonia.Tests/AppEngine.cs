using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Skua.Control;
using Skua.Engine;
using Skua.Engine.Tests;
using Skua.MacOS.GameHost;

[assembly: AssemblyFixture(typeof(Skua.Avalonia.Tests.AppEngine))]

namespace Skua.Avalonia.Tests;

/// <summary>
/// An Engine hosted in this process as the Mac App hosts it, running the fake Game Host with a Frame Buffer, in a throwaway data folder.
/// One per test process: an Engine configures <c>Ioc.Default</c>, which a process configures once.
/// </summary>
public sealed class AppEngine : IAsyncLifetime
{
    /// <summary>Under /tmp, because $TMPDIR on macOS is too long for a socket path.</summary>
    public static readonly string SkuaDir = Path.Combine("/tmp", "skua-app-" + Guid.NewGuid().ToString("N")[..8]);

    public static string CallLog => Path.Combine(SkuaDir, "fake-gamehost.calls");

    /// <summary>The fake Game Host runs each line appended to it; see <see cref="DoAsync"/>.</summary>
    public static string ControlFile => Path.Combine(SkuaDir, "fake-gamehost.control");

    /// <summary>The servers both the servers API and the simulated game list.</summary>
    public static readonly FakeServer[] Servers =
    [
        new("Artix", Count: 1500, Max: 1500),
        new("Galanoth", Count: 300),
        new("Twig", Count: 50, Online: false),
        new("Sir Ver", Count: 200),
    ];

    /// <summary>The servers API the Engine lists servers from.</summary>
    public static FakeAqApi Api { get; private set; } = null!;

    /// <summary>The security tool the Engine reads the Active Account with; it holds the Test Account.</summary>
    public static FakeKeychain Keychain { get; private set; } = null!;

    private HostedEngine? _engine;

    /// <summary>
    /// Points Core, the Engine and the fake at the throwaway folder before anything reads the environment, and keeps the Engine off
    /// the real Game Host, Keychain, servers API and GitHub. The fake simulates the game, which loads to the login screen and accepts
    /// the Test Account in the fake Keychain.
    /// </summary>
    [ModuleInitializer]
    internal static void UseTheSandbox()
    {
        Directory.CreateDirectory(SkuaDir);
        string swf = Path.Combine(SkuaDir, "fake-skua.swf");
        File.WriteAllBytes(swf, []);
        Api = new FakeAqApi(Servers);
        Keychain = new FakeKeychain(SkuaDir);
        string scenario = Path.Combine(SkuaDir, "fake-gamehost.scenario");
        File.WriteAllLines(scenario,
        [
            $"calllog {CallLog}",
            $"control {ControlFile}",
            $"game {Keychain.Username} {Keychain.Password}",
            $"servers {FakeServer.ListJson(Servers)}",
            """send E <invoke name="loaded" returntype="xml"><arguments></arguments></invoke>""",
        ]);
        Environment.SetEnvironmentVariable(EngineEndpoint.SkuaDirVariable, SkuaDir);
        Environment.SetEnvironmentVariable(EngineEndpoint.SocketVariable, null);
        Environment.SetEnvironmentVariable(GameHostLaunch.ExecutableVariable, Path.Combine(AppContext.BaseDirectory, "fake-gamehost"));
        Environment.SetEnvironmentVariable(GameHostLaunch.SwfVariable, swf);
        Environment.SetEnvironmentVariable("SKUA_FAKE_GAMEHOST_SCENARIO", scenario);
        Environment.SetEnvironmentVariable(Skua.Control.Keychain.ToolVariable, Keychain.Tool);
        foreach ((string key, string value) in Api.Environment())
            Environment.SetEnvironmentVariable(key, value);
        Environment.SetEnvironmentVariable(Skua.Core.Models.GitHub.ScriptSource.RawUrlEnvironmentVariable, "http://127.0.0.1:9/raw/");
        Environment.SetEnvironmentVariable(Skua.Core.Models.GitHub.ScriptSource.ApiUrlEnvironmentVariable, "http://127.0.0.1:9/api/");
    }

    public HostedEngine Engine => _engine ?? throw new InvalidOperationException("The Engine hasn't started.");

    public BridgeFlashUtil Flash => Engine.Services.GetRequiredService<BridgeFlashUtil>();

    public async ValueTask InitializeAsync()
    {
        _engine = await HostedEngine.StartAsync(EngineEndpoint.FromEnvironment(), new EngineHostOptions
        {
            Mode = EngineHostMode.App,
            ConfigureServices = services => services.AddSingleton(new HostMarker()),
        });
        // By the first ping reply the fake has mapped the Frame Buffer.
        Stopwatch waited = Stopwatch.StartNew();
        while (!Flash.IsGameHostRunning && waited.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
            await _engine.StopAsync();
        await Api.DisposeAsync();
        try
        {
            Directory.Delete(SkuaDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Makes the simulated game act, e.g. <c>cell r2</c> or <c>gain 3000 0</c>, and returns once it has. A restarted fake runs every
    /// directive again, so use only ones that are harmless to repeat.
    /// </summary>
    public static async Task DoAsync(string directive)
    {
        string done = ControlFile + ".done";
        int before = File.Exists(done) ? (await File.ReadAllTextAsync(done)).Length : 0;
        await File.AppendAllTextAsync(ControlFile, directive + "\n");
        for (int i = 0; i < 400 && (!File.Exists(done) || (await File.ReadAllTextAsync(done)).Length <= before); i++)
            await Task.Delay(25);
    }

    /// <summary>The input and view lines the fake has logged so far, in order.</summary>
    public static string[] Calls() =>
        File.Exists(CallLog) ? File.ReadAllLines(CallLog).Where(l => l.StartsWith("input ", StringComparison.Ordinal) || l.StartsWith("view ", StringComparison.Ordinal)).ToArray() : [];
}

/// <summary>A service the test host adds through <see cref="EngineHostOptions.ConfigureServices"/>.</summary>
public sealed class HostMarker;
