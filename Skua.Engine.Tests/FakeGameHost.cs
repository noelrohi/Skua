using System.Diagnostics;
using System.Runtime.CompilerServices;
using Skua.MacOS.GameHost;

namespace Skua.Engine.Tests;

/// <summary>
/// A scenario for the fake Game Host (see Skua.FakeGameHost/Program.cs), and the environment that makes an Engine run it.
/// </summary>
public sealed class FakeGameHost
{
    private readonly List<string> _lines = [];

    public FakeGameHost(EngineSandbox sandbox)
    {
        ScenarioPath = Path.Combine(sandbox.SkuaDir, "fake-gamehost.scenario");
        PidFile = Path.Combine(sandbox.SkuaDir, "fake-gamehost.pid");
        _lines.Add($"pidfile {PidFile}");
    }

    public string ScenarioPath { get; }

    public string PidFile { get; }

    public string CallLog { get; private set; } = "";

    /// <summary>
    /// Makes every Engine these tests start run an idle fake Game Host, find no Test Account, no servers API and no Script Source unless a
    /// test says otherwise, so none ever runs the real Game Host, reads the real Keychain or reaches content.aq.com or GitHub.
    /// Child processes inherit it, including Engines auto-started by the CLI.
    /// </summary>
    [ModuleInitializer]
    internal static void UseByDefault()
    {
        string swf = Path.Combine(EngineSandbox.BinDir, "fake-skua.swf");
        if (!File.Exists(swf))
            File.WriteAllBytes(swf, []);
        System.Environment.SetEnvironmentVariable(GameHostLaunch.ExecutableVariable, EngineSandbox.FakeGameHostExecutable);
        System.Environment.SetEnvironmentVariable(GameHostLaunch.SwfVariable, swf);
        System.Environment.SetEnvironmentVariable("SKUA_SECURITY_TOOL", FakeKeychain.Empty(EngineSandbox.BinDir));
        // The discard port: nothing listens, so the request fails at once.
        System.Environment.SetEnvironmentVariable(Skua.Core.Scripts.ScriptServers.ServersUrlEnvironmentVariable, "http://127.0.0.1:9/game/api/data/servers");
        System.Environment.SetEnvironmentVariable(Skua.Core.Models.GitHub.ScriptSource.RawUrlEnvironmentVariable, "http://127.0.0.1:9/raw/");
        System.Environment.SetEnvironmentVariable(Skua.Core.Models.GitHub.ScriptSource.ApiUrlEnvironmentVariable, "http://127.0.0.1:9/api/");
    }

    /// <summary>
    /// Records the name of every call the Engine makes into the Game Client, and <c>screenshot &lt;maxWidth&gt;</c> for every screenshot request;
    /// read them with <see cref="CallsAsync"/>.
    /// </summary>
    public FakeGameHost LogCalls()
    {
        CallLog = Path.Combine(Path.GetDirectoryName(ScenarioPath)!, "fake-gamehost.calls");
        _lines.Add($"calllog {CallLog}");
        return this;
    }

    /// <summary>
    /// Simulates the AQW game behind skua.swf, which accepts the account of <paramref name="keychain"/> and lists
    /// <paramref name="servers"/> after the account logs in; the Game Client reports <c>loaded</c> at once.
    /// </summary>
    public FakeGameHost Game(FakeKeychain keychain, params FakeServer[] servers)
    {
        Control();
        _lines.Add($"game {keychain.Username} {keychain.Password}");
        _lines.Add($"servers {FakeServer.ListJson(servers)}");
        _lines.Add("""send E <invoke name="loaded" returntype="xml"><arguments></arguments></invoke>""");
        return this;
    }

    /// <summary>How long connecting to a server takes in the simulated game; 300 ms unless set.</summary>
    public FakeGameHost ConnectDelay(int milliseconds)
    {
        _lines.Insert(_lines.FindIndex(l => l.StartsWith("game ", StringComparison.Ordinal)) + 1, $"connect-delay {milliseconds}");
        return this;
    }

    /// <summary>The simulated game also accepts this account, e.g. one that <c>skua account add</c> stores.</summary>
    public FakeGameHost Account(string username, string password)
    {
        _lines.Insert(_lines.FindIndex(l => l.StartsWith("game ", StringComparison.Ordinal)) + 1, $"account {username} {password}");
        return this;
    }

    /// <summary>Connecting to <paramref name="server"/> fails with this connection message in the simulated game.</summary>
    public FakeGameHost Reject(string server, string message)
    {
        _lines.Insert(_lines.FindIndex(l => l.StartsWith("game ", StringComparison.Ordinal)) + 1, $"reject {server} {message}");
        return this;
    }

    public string ControlFile { get; private set; } = "";

    /// <summary>Lets <see cref="DoAsync"/> run directives while the fake runs, so a test decides when they happen.</summary>
    public FakeGameHost Control()
    {
        if (ControlFile.Length > 0)
            return this;
        ControlFile = Path.Combine(Path.GetDirectoryName(ScenarioPath)!, "fake-gamehost.control");
        _lines.Add($"control {ControlFile}");
        return this;
    }

    /// <summary>
    /// Runs a directive in the running fake, e.g. <c>repeat 3 send F line {i}</c>, or makes the simulated game act, e.g. <c>kick</c> or
    /// <c>lose-connection &lt;message&gt;</c>, and returns once it has; see <see cref="Control"/> and Skua.FakeGameHost/Program.cs.
    /// </summary>
    public async Task DoAsync(string directive)
    {
        int count = ++_directives;
        await File.AppendAllTextAsync(ControlFile, directive + "\n", TestContext.Current.CancellationToken);
        string done = ControlFile + ".done";
        for (int i = 0; i < 400 && (!File.Exists(done) || (await File.ReadAllTextAsync(done, TestContext.Current.CancellationToken)).Length < count); i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);
    }

    private int _directives;

    public FakeGameHost Reply(string function, string xml)
    {
        _lines.Add($"reply {function} {xml}");
        return this;
    }

    public FakeGameHost Delay(string function, int milliseconds)
    {
        _lines.Add($"delay {function} {milliseconds}");
        return this;
    }

    /// <summary>Answers screenshot requests with no image, as the Game Host does when it can't capture a frame.</summary>
    public FakeGameHost NoImage()
    {
        _lines.Add("noimage");
        return this;
    }

    public FakeGameHost Send(char type, string text)
    {
        _lines.Add($"send {type} {text}");
        return this;
    }

    /// <summary>Sends an 'L' log line at a level (1 error, 2 warn).</summary>
    public FakeGameHost Log(int level, string text)
    {
        _lines.Add($"log {level} {text}");
        return this;
    }

    /// <summary>Runs one directive <paramref name="count"/> times, with <c>{i}</c> in it replaced by 0, 1, 2…</summary>
    public FakeGameHost Repeat(int count, string directive)
    {
        _lines.Add($"repeat {count} {directive}");
        return this;
    }

    /// <summary>Sends a frame header that declares a length of 0, which corrupts the Bridge stream.</summary>
    public FakeGameHost Corrupt()
    {
        _lines.Add("corrupt");
        return this;
    }

    public FakeGameHost Sleep(int milliseconds)
    {
        _lines.Add($"sleep {milliseconds}");
        return this;
    }

    public FakeGameHost Exit(int code)
    {
        _lines.Add($"exit {code}");
        return this;
    }

    /// <summary>Writes the scenario file, for a fake started directly with it as its argument.</summary>
    public string Write()
    {
        File.WriteAllLines(ScenarioPath, _lines);
        return ScenarioPath;
    }

    public IDictionary<string, string> Environment()
    {
        Write();
        return new Dictionary<string, string>
        {
            ["SKUA_GAMEHOST"] = EngineSandbox.FakeGameHostExecutable,
            ["SKUA_FAKE_GAMEHOST_SCENARIO"] = ScenarioPath,
        };
    }

    /// <summary>The fake's pid, once it has started.</summary>
    public async Task<int> PidAsync()
    {
        for (int i = 0; i < 200 && !File.Exists(PidFile); i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        return int.Parse(await File.ReadAllTextAsync(PidFile, TestContext.Current.CancellationToken));
    }

    /// <summary>The names of the calls the Engine has made so far, in order; see <see cref="LogCalls"/>.</summary>
    public async Task<string[]> CallsAsync() =>
        File.Exists(CallLog) ? await File.ReadAllLinesAsync(CallLog, TestContext.Current.CancellationToken) : [];

    /// <summary>Waits until the Engine has made <paramref name="call"/>, after the last <paramref name="after"/> when given, and returns the calls so far.</summary>
    public async Task<string[]> WaitForCallAsync(string call, string? after = null)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            string[] calls = await CallsAsync();
            int from = after is null ? 0 : Array.LastIndexOf(calls, after) + 1;
            if ((after is null || from > 0) && calls[from..].Contains(call))
                return calls;
            if (waited.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException($"No '{call}' call{(after is null ? "" : $" after '{after}'")}.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
