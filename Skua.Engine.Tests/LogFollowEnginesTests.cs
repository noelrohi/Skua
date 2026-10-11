using System.Diagnostics;
using System.Text.Json;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary><c>skua logs -f</c> over several Engines (#272): <c>--engine alt1,alt2</c> and <c>--all-engines</c>, against two sandboxed Engines.</summary>
public class LogFollowEnginesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Following_several_Engines_replays_their_tails_merged_by_time_then_prints_each_new_entry_with_its_Engine()
    {
        await using EngineSandbox sandbox = new();
        EngineEndpoint alt1 = EngineEndpoint.Resolve("alt1", sandbox.SkuaDir);
        EngineEndpoint alt2 = EngineEndpoint.Resolve("alt2", sandbox.SkuaDir);
        try
        {
            await StartAsync(sandbox, "alt1");
            await StartAsync(sandbox, "alt2");
            await ReportAsync(sandbox, "alt1", "before");

            Process follow = sandbox.StartCli("logs", "events", "-f", "--engine", "alt1,alt2", "--tail", "1", "--json");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            // alt2's newest event is from its start, before alt1's report.
            JsonElement first = await NextAsync(follow, timeout.Token);
            JsonElement second = await NextAsync(follow, timeout.Token);
            Assert.Equal("alt2", first.GetProperty("engine").GetString());
            Assert.Equal(("alt1", "script.report", "before"), (second.GetProperty("engine").GetString(), second.GetProperty("type").GetString(), ReportName(second)));
            Assert.True(first.GetProperty("ts").GetInt64() <= second.GetProperty("ts").GetInt64());

            await ReportAsync(sandbox, "alt2", "live");
            JsonElement live = await NextReportAsync(follow, timeout.Token);
            Assert.Equal(("alt2", "live"), (live.GetProperty("engine").GetString(), ReportName(live)));

            // An Engine that stops is said on stderr, and the others are still followed.
            await EngineClient.StopAsync(alt2, EngineSandbox.StopTimeout, Ct);
            Assert.StartsWith("skua: alt2: ", await follow.StandardError.ReadLineAsync(timeout.Token));
            await ReportAsync(sandbox, "alt1", "after");
            JsonElement after = await NextReportAsync(follow, timeout.Token);
            Assert.Equal(("alt1", "after"), (after.GetProperty("engine").GetString(), ReportName(after)));
            Assert.False(follow.HasExited);
        }
        finally
        {
            await EngineClient.StopAsync(alt1, EngineSandbox.StopTimeout);
            await EngineClient.StopAsync(alt2, EngineSandbox.StopTimeout);
        }
    }

    [Fact]
    public async Task All_engines_follows_every_running_Engine_with_its_name_before_each_line()
    {
        await using EngineSandbox sandbox = new();
        EngineEndpoint alt1 = EngineEndpoint.Resolve("alt1", sandbox.SkuaDir);
        EngineEndpoint alt10 = EngineEndpoint.Resolve("alt10", sandbox.SkuaDir);
        try
        {
            await StartAsync(sandbox, "alt1");
            await StartAsync(sandbox, "alt10");
            // A socket left behind by an Engine that went away is skipped.
            File.WriteAllText(EngineEndpoint.Resolve("gone", sandbox.SkuaDir).SocketPath, "");

            Process follow = sandbox.StartCli("logs", "events", "script", "-f", "--all-engines");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await ReportAsync(sandbox, "alt1", "ultra.attempt");

            string line;
            do
                line = (await follow.StandardOutput.ReadLineAsync(timeout.Token))!;
            while (!line.Contains("script.report", StringComparison.Ordinal));
            // Names are padded to the longest, so the entries line up.
            Assert.Matches(@"^alt1  \d+ \d\d:\d\d:\d\d\.\d{3} events script\.report \{.*""name"":""ultra\.attempt""", line);
            Assert.Equal(["alt1", "alt10"], EngineEndpoint.InDataFolder(sandbox.SkuaDir).Select(e => e.Name).Where(n => n != "gone"));
        }
        finally
        {
            await EngineClient.StopAsync(alt1, EngineSandbox.StopTimeout);
            await EngineClient.StopAsync(alt10, EngineSandbox.StopTimeout);
        }
    }

    [Fact]
    public async Task Several_Engines_need_a_follow_without_a_cursor_and_start_none()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult page = await sandbox.RunCliAsync("logs", "events", "--engine", "alt1,alt2");
        ProcessResult after = await sandbox.RunCliAsync("logs", "events", "-f", "--all-engines", "--after", "1.1");
        ProcessResult both = await sandbox.RunCliAsync("logs", "-f", "--all-engines", "--engine", "alt1");
        ProcessResult status = await sandbox.RunCliAsync("--engine", "alt1,alt2", "status");
        ProcessResult invalid = await sandbox.RunCliAsync("logs", "-f", "--engine", "alt1,Not Valid");
        ProcessResult notRunning = await sandbox.RunCliAsync("logs", "-f", "--engine", "alt1,alt2");
        ProcessResult noneRunning = await sandbox.RunCliAsync("logs", "-f", "--all-engines");

        Assert.Equal((1, true), (page.ExitCode, page.Stderr.Contains("follow several with -f")));
        Assert.Equal((1, true), (after.ExitCode, after.Stderr.Contains("--after")));
        Assert.Equal((1, true), (both.ExitCode, both.Stderr.Contains("not both")));
        Assert.Equal((1, true), (status.ExitCode, status.Stderr.Contains("several Engines")));
        Assert.Equal((1, true), (invalid.ExitCode, invalid.Stderr.Contains("'Not Valid' is invalid")));
        Assert.Equal(ExitCodes.For(ErrorCode.EngineUnavailable), notRunning.ExitCode);
        Assert.Equal("skua: Engine 'alt1' isn't running.", notRunning.Stderr.Trim());
        Assert.Equal(ExitCodes.For(ErrorCode.EngineUnavailable), noneRunning.ExitCode);
        Assert.Contains("No Engine", noneRunning.Stderr);
        Assert.Empty(EngineEndpoint.InDataFolder(sandbox.SkuaDir));
    }

    private static async Task StartAsync(EngineSandbox sandbox, string name)
    {
        ProcessResult started = await sandbox.RunCliAsync("--engine", name, "engine", "start");
        Assert.True(started.ExitCode == 0, started.Stderr);
    }

    /// <summary>Records a Script Report named <paramref name="name"/> on the Engine, with an <c>eval</c>.</summary>
    private static async Task ReportAsync(EngineSandbox sandbox, string engine, string name)
    {
        ProcessResult eval = await sandbox.RunCliAsync("--engine", engine, "eval", $$"""Bot.Report("{{name}}", new { boss = "UltraNulgath" }); return 1;""");
        Assert.True(eval.ExitCode == 0, eval.Stderr + eval.Stdout);
    }

    private static async Task<JsonElement> NextAsync(Process follow, CancellationToken cancellationToken)
    {
        using JsonDocument line = JsonDocument.Parse((await follow.StandardOutput.ReadLineAsync(cancellationToken))!);
        return line.RootElement.Clone();
    }

    /// <summary>The next <c>script.report</c> the follow prints, past any other event.</summary>
    private static async Task<JsonElement> NextReportAsync(Process follow, CancellationToken cancellationToken)
    {
        JsonElement entry;
        do
            entry = await NextAsync(follow, cancellationToken);
        while (entry.GetProperty("type").GetString() != EventTypes.ScriptReport);
        return entry;
    }

    private static string? ReportName(JsonElement entry) => entry.GetProperty("data").GetProperty("name").GetString();
}
