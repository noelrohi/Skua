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

    public IDictionary<string, string> Environment()
    {
        File.WriteAllLines(ScenarioPath, _lines);
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
}
