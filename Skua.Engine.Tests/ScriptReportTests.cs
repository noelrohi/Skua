using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>Script Reports: <c>Bot.Report</c> and its <c>script.report</c> event, against the fake Game Host.</summary>
public class ScriptReportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_Script_Report_is_a_script_report_event_with_the_run_the_script_the_name_and_the_data_as_JSON()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Report.cs", TestScripts.Main("""
            bot.Report("ultra.attempt", new { boss = "Nulgath", outcome = "kill", bossHp = 0, deaths = new[] { new { atSec = 41.5 } } });
            bot.Log("after the report");
            """));

        ScriptStartResult start = await game.Connection.ScriptStartAsync("Tests/Report.cs", cancellationToken: Ct);
        ScriptWaitResult wait = await game.Connection.ScriptWaitAsync(60, Ct);

        Assert.Equal(ScriptOutcome.Completed, wait.Status.LastRun!.Outcome);
        LogEntryDto report = Assert.Single(await ReportsAsync(game.Connection));
        Assert.Equal(start.Run, report.Run);
        Assert.Equal(start.Run, report.Data!.Value.GetProperty("run").GetInt32());
        Assert.Equal(("Tests/Report.cs", "ultra.attempt"), (ScriptEvents.Get(report, "script"), ScriptEvents.Get(report, "name")));
        Assert.Equal("""{"boss":"Nulgath","outcome":"kill","bossHp":0,"deaths":[{"atSec":41.5}]}""", Data(report).GetRawText());
        Assert.False(report.Truncated);
        List<LogEntryDto> lines = await game.Connection.WaitForLogsAsync(LogKind.Script, 2, e => e.Text is "[report] ultra.attempt" or "after the report");
        Assert.Equal(["[report] ultra.attempt", "after the report"], lines.Select(e => e.Text));
        Assert.All(lines, e => Assert.Equal(start.Run, e.Run));
    }

    [Fact]
    public async Task Data_over_64_KB_is_its_JSON_text_cut_to_64_KB()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/BigReport.cs", TestScripts.Main("""
            string[] Rows(int count)
            {
                string[] rows = new string[count];
                System.Array.Fill(rows, "abcdefghij");
                return rows;
            }
            bot.Report("big", new { rows = Rows(10_000) });
            bot.Report("fits", new { rows = Rows(4_000) });
            """));

        await game.Connection.ScriptStartAsync("Tests/BigReport.cs", cancellationToken: Ct);
        await game.Connection.ScriptWaitAsync(60, Ct);

        List<LogEntryDto> reports = await ReportsAsync(game.Connection);
        Assert.Equal(["big", "fits"], reports.Select(r => ScriptEvents.Get(r, "name")));
        string cut = Data(reports[0]).GetString()!;
        Assert.Equal(64 * 1024, cut.Length);
        Assert.StartsWith("""{"rows":["abcdefghij","abcdefghij",""", cut);
        Assert.True(reports[0].Truncated);
        Assert.Equal(4_000, Data(reports[1]).GetProperty("rows").GetArrayLength());
        Assert.False(reports[1].Truncated);
    }

    [Fact]
    public async Task Data_that_cant_be_serialized_still_reports_with_the_error_and_the_Script_goes_on()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/LoopReport.cs", TestScripts.Main("""
            List<object> loop = new();
            loop.Add(loop);
            bot.Report("loop", loop);
            bot.Log("after the report");
            """));

        await game.Connection.ScriptStartAsync("Tests/LoopReport.cs", cancellationToken: Ct);
        ScriptWaitResult wait = await game.Connection.ScriptWaitAsync(60, Ct);

        Assert.Equal(ScriptOutcome.Completed, wait.Status.LastRun!.Outcome);
        LogEntryDto report = Assert.Single(await ReportsAsync(game.Connection));
        Assert.Equal("loop", ScriptEvents.Get(report, "name"));
        Assert.StartsWith("JsonException: A possible object cycle was detected", Data(report).GetProperty("error").GetString());
        await game.Connection.WaitForLogsAsync(LogKind.Script, 2, e => e.Text is "[report] loop" or "after the report");
    }

    [Fact]
    public async Task A_report_returns_at_once_and_never_throws_even_while_the_Script_is_stopping()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/StoppingReport.cs", TestScripts.Main("""
            bot.Log("fighting");
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bot.Report("attempt", new { outcome = "stopped" });
            bot.Report("returned", new { ms = clock.ElapsedMilliseconds });
            """));

        ScriptStartResult start = await game.Connection.ScriptStartAsync("Tests/StoppingReport.cs", cancellationToken: Ct);
        await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "fighting");
        await game.Connection.ScriptStopAsync(Ct);
        await game.Connection.ScriptWaitAsync(60, Ct);

        List<LogEntryDto> reports = await ReportsAsync(game.Connection);
        Assert.Equal(["attempt", "returned"], reports.Select(r => ScriptEvents.Get(r, "name")));
        Assert.All(reports, r => Assert.Equal(start.Run, r.Data!.Value.GetProperty("run").GetInt32()));
        Assert.Equal("stopped", Data(reports[0]).GetProperty("outcome").GetString());
        Assert.InRange(Data(reports[1]).GetProperty("ms").GetInt64(), 0, 1000);
    }

    [Fact]
    public async Task A_report_from_an_eval_outside_a_run_has_a_null_run_and_script()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);

        EvalResult result = await game.Connection.EvalAsync("""Bot.Report("probe", new { hp = 1200 })""", cancellationToken: Ct);

        Assert.Null(result.Error);
        Assert.Equal(["[report] probe"], result.Logs);
        LogEntryDto report = Assert.Single(await ReportsAsync(game.Connection));
        Assert.Null(report.Run);
        Assert.Equal(JsonValueKind.Null, report.Data!.Value.GetProperty("run").ValueKind);
        Assert.Equal(JsonValueKind.Null, report.Data!.Value.GetProperty("script").ValueKind);
        Assert.Equal("""{"hp":1200}""", Data(report).GetRawText());
    }

    private static JsonElement Data(LogEntryDto report) => report.Data!.Value.GetProperty("data");

    private static async Task<List<LogEntryDto>> ReportsAsync(EngineConnection connection) =>
        (await ScriptEvents.AllOfAsync(connection)).Where(e => e.Type == EventTypes.ScriptReport).ToList();
}
