using System.Runtime.CompilerServices;

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
    /// Makes every Engine these tests start run an idle fake Game Host unless a test says otherwise, so none ever runs the real one.
    /// Child processes inherit it, including Engines auto-started by the CLI.
    /// </summary>
    [ModuleInitializer]
    internal static void UseByDefault()
    {
        string swf = Path.Combine(EngineSandbox.BinDir, "fake-skua.swf");
        if (!File.Exists(swf))
            File.WriteAllBytes(swf, []);
        System.Environment.SetEnvironmentVariable("SKUA_GAMEHOST", EngineSandbox.FakeGameHostExecutable);
        System.Environment.SetEnvironmentVariable("SKUA_SWF", swf);
    }

    /// <summary>Records the name of every call the Engine makes into the Game Client; read them with <see cref="CallsAsync"/>.</summary>
    public FakeGameHost LogCalls()
    {
        CallLog = Path.Combine(Path.GetDirectoryName(ScenarioPath)!, "fake-gamehost.calls");
        _lines.Add($"calllog {CallLog}");
        return this;
    }

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

    public FakeGameHost Send(char type, string text)
    {
        _lines.Add($"send {type} {text}");
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
}
