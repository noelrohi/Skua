using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>
/// Starts Engines one after another against the fakes, to catch a crash at start that happens only now and then (#129). Opt-in: set
/// <c>SKUA_START_LOOP</c> to the number of starts, e.g. 50. It never logs in and never reaches a real server.
/// </summary>
public class StartLoopTests
{
    public const string CountVariable = "SKUA_START_LOOP";

    private static readonly string[] CrashMarks = ["Unhandled exception", "handler failed", "The Bridge read failed", "stderr read failed"];

    [Fact]
    public async Task Engines_start_and_stop_cleanly_every_time()
    {
        Assert.SkipUnless(int.TryParse(Environment.GetEnvironmentVariable(CountVariable), out int count) && count > 0,
            $"Set {CountVariable} to the number of Engine starts to run.");

        List<string> failures = [];
        for (int i = 1; i <= count; i++)
        {
            await using EngineSandbox sandbox = new();
            await using GameFixture game = await GameFixture.StartAsync(sandbox);
            Assert.True(await EngineClient.StopAsync(sandbox.Endpoint, EngineSandbox.StopTimeout, TestContext.Current.CancellationToken));
            await game.Engine.WaitForExitAsync(TestContext.Current.CancellationToken);

            string stderr = await game.Engine.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            string logs = string.Concat(Directory.GetFiles(sandbox.Endpoint.LogFilesDir, "*.jsonl").Select(File.ReadAllText));
            if (game.Engine.ExitCode != EngineExitCodes.Success)
                failures.Add($"start {i}: skua-engine exited with {game.Engine.ExitCode}");
            foreach (string line in (stderr + "\n" + logs).Split('\n').Where(l => CrashMarks.Any(m => l.Contains(m, StringComparison.Ordinal))))
                failures.Add($"start {i}: {line}");
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }
}
