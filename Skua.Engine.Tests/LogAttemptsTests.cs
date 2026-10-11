using System.Text.Json;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>
/// <c>skua logs attempts</c> (#273) on the session logs of a three-account party, alt1…alt3, at Ultra Warden: a kill, then a wipe. alt2 reports its
/// first Attempt's data as a JSON string and alt3 its times as epoch milliseconds; alt3 also has a report cut to 64 KB.
/// </summary>
public class LogAttemptsTests
{
    private static string Fixture(string engine) => Path.Combine(EngineSandbox.BinDir, "fixtures", $"attempts-{engine}.jsonl");

    [Fact]
    public async Task Attempts_groups_each_accounts_report_of_one_Attempt_and_passes_the_loadout_through()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("logs", "attempts", "--file", Fixture("alt1"), "--file", Fixture("alt2"), "--file", Fixture("alt3"), "--json");

        Assert.True(result.ExitCode == 0, result.Stderr);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        JsonElement root = json.RootElement;
        Assert.Equal(["alt1", "alt2", "alt3"], root.GetProperty("logs").EnumerateArray().Select(l => l.GetProperty("engine").GetString()));
        Assert.Equal(1, root.GetProperty("unreadable").GetInt32());
        JsonElement[] attempts = [.. root.GetProperty("attempts").EnumerateArray()];
        Assert.Equal(2, attempts.Length);

        JsonElement kill = attempts[0];
        Assert.Equal(("UltraWarden", "loo-lr-sc-csh", "kill"), (Text(kill, "boss"), Text(kill, "comp"), Text(kill, "outcome")));
        Assert.Equal((1791208800200L, 1791209034200L, 234.0, 0L), (kill.GetProperty("startedAt").GetInt64(), kill.GetProperty("endedAt").GetInt64(),
            kill.GetProperty("durationSec").GetDouble(), kill.GetProperty("bossHp").GetInt64()));
        Assert.Equal(
            ["alt1 Chrono ShadowHunter/WardenAttacker kill 6.9,91", "alt2 Lord of Order/BerserkHealer kill ", "alt3 Legion Revenant/BerserkTaunter kill 100.5"],
            kill.GetProperty("accounts").EnumerateArray().Select(Summary));
        JsonElement alt1 = kill.GetProperty("accounts")[0];
        Assert.Equal((103.0, 1, "Ultrasv3/UltraWarden.cs"), (alt1.GetProperty("durationSec").GetDouble(), alt1.GetProperty("run").GetInt32(), Text(alt1, "script")));
        Assert.Equal("helm: Lucky, not Vainglory", alt1.GetProperty("loadout").GetProperty("notes")[0].GetString());
        // ISO with an offset, and epoch milliseconds.
        Assert.Equal(1791208801500, kill.GetProperty("accounts")[1].GetProperty("startedAt").GetInt64());
        Assert.Equal(1791208803000, kill.GetProperty("accounts")[2].GetProperty("startedAt").GetInt64());

        JsonElement wipe = attempts[1];
        Assert.Equal(("wipe", 410000L, 1000000L, 90.5), (Text(wipe, "outcome"), wipe.GetProperty("bossHp").GetInt64(), wipe.GetProperty("bossMaxHp").GetInt64(),
            wipe.GetProperty("durationSec").GetDouble()));
        Assert.Equal(
            ["alt1 Chrono ShadowHunter/WardenAttacker wipe 40.2,85", "alt2 Lord of Order/BerserkHealer wipe 80", "alt3 Legion Revenant/BerserkTaunter stopped "],
            wipe.GetProperty("accounts").EnumerateArray().Select(Summary));
    }

    [Fact]
    public async Task Attempts_reads_the_newest_log_of_each_named_Engine_and_prints_a_line_per_Attempt()
    {
        await using EngineSandbox sandbox = new();
        foreach (string engine in (string[])["alt1", "alt2", "alt3"])
            Install(sandbox, engine);

        ProcessResult result = await sandbox.RunCliAsync("--engine", "alt1,alt2,alt3", "logs", "attempts");

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(
            [
                "2 Attempts in the logs of alt1, alt2, alt3:",
                "2026-10-05 14:00:00Z  UltraWarden  loo-lr-sc-csh  kill  3:54"
                    + "  | alt1 Chrono ShadowHunter/WardenAttacker 1:43, died at 6.9 s, 91 s"
                    + "  | alt2 Lord of Order/BerserkHealer, no deaths"
                    + "  | alt3 Legion Revenant/BerserkTaunter, died at 100.5 s",
                "2026-10-05 14:10:00Z  UltraWarden  loo-lr-sc-csh  wipe (boss 41%)  1:30"
                    + "  | alt1 Chrono ShadowHunter/WardenAttacker, died at 40.2 s, 85 s"
                    + "  | alt2 Lord of Order/BerserkHealer, died at 80 s"
                    + "  | alt3 Legion Revenant/BerserkTaunter stopped, no deaths",
                "1 report couldn't be read: data cut to 64 KB, or no start time.",
            ],
            result.Stdout.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public async Task All_engines_reads_every_Engine_with_session_logs()
    {
        await using EngineSandbox sandbox = new();
        Install(sandbox, "alt1");
        Install(sandbox, "alt3");

        ProcessResult result = await sandbox.RunCliAsync("logs", "attempts", "--all-engines", "--json");

        Assert.True(result.ExitCode == 0, result.Stderr);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.All(json.RootElement.GetProperty("attempts").EnumerateArray(),
            attempt => Assert.Equal(["alt1", "alt3"], attempt.GetProperty("accounts").EnumerateArray().Select(a => Text(a, "engine"))));
    }

    [Fact]
    public async Task An_Engines_second_report_or_another_Comp_is_another_Attempt()
    {
        await using EngineSandbox sandbox = new();
        string log = Path.Combine(sandbox.SkuaDir, "alt4.jsonl");
        File.WriteAllLines(log,
        [
            Report(1, "alt4", "ke-lr-ap-loo", 1_000, 41_000, "stopped"),
            // Restarted at once: the same Engine, so another Attempt.
            Report(2, "alt4", "ke-lr-ap-loo", 5_000, 46_000, "kill"),
            Report(3, "alt4", "ke-lr-sc-loo", 5_000, 46_000, "kill"),
            // No start time: it can't join an Attempt.
            Report(4, "alt4", "ke-lr-sc-loo", null, 46_000, "kill"),
        ]);

        ProcessResult result = await sandbox.RunCliAsync("logs", "attempts", "--file", log, "--json");

        Assert.True(result.ExitCode == 0, result.Stderr);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(["ke-lr-ap-loo stopped", "ke-lr-ap-loo kill", "ke-lr-sc-loo kill"],
            json.RootElement.GetProperty("attempts").EnumerateArray().Select(a => $"{Text(a, "comp")} {Text(a, "outcome")}"));
        Assert.Equal(1, json.RootElement.GetProperty("unreadable").GetInt32());
        // A log without an engine.started, outside an Engine's folder, is its file's.
        Assert.Equal("alt4", Text(json.RootElement.GetProperty("logs")[0], "engine"));
    }

    [Fact]
    public async Task A_log_in_an_Engines_session_log_folder_is_that_Engines()
    {
        await using EngineSandbox sandbox = new();
        // alt1's log, as the Engine named alt5 would hold it after a rename: the folder wins over its engine.started.
        string folder = EngineEndpoint.Resolve("alt5", sandbox.SkuaDir).LogFilesDir;
        Directory.CreateDirectory(folder);
        string log = Path.Combine(folder, "2026-10-05T13-58-20.000Z.jsonl");
        File.Copy(Fixture("alt1"), log);

        ProcessResult result = await sandbox.RunCliAsync("logs", "attempts", "--file", log, "--json");

        Assert.True(result.ExitCode == 0, result.Stderr);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.Equal("alt5", Text(json.RootElement.GetProperty("logs")[0], "engine"));
        Assert.All(json.RootElement.GetProperty("attempts").EnumerateArray(), a => Assert.Equal("alt5", Text(a.GetProperty("accounts")[0], "engine")));
    }

    [Fact]
    public async Task Attempts_of_an_Engine_without_logs_or_with_two_sources_fails()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult noLogs = await sandbox.RunCliAsync("--engine", "alt1", "logs", "attempts");
        ProcessResult noneAtAll = await sandbox.RunCliAsync("logs", "attempts", "--all-engines");
        ProcessResult twoSources = await sandbox.RunCliAsync("--engine", "alt1", "logs", "attempts", "--file", Fixture("alt1"));
        ProcessResult gains = await sandbox.RunCliAsync("--engine", "alt1,alt2", "logs", "gains");

        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), noLogs.ExitCode);
        Assert.Contains("Engine 'alt1' has no session logs", noLogs.Stderr);
        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), noneAtAll.ExitCode);
        Assert.Equal((1, true), (twoSources.ExitCode, twoSources.Stderr.Contains("one of --file, --engine and --all-engines")));
        Assert.Equal((1, true), (gains.ExitCode, gains.Stderr.Contains("several Engines")));
    }

    /// <summary>Puts the Engine's fixture in its session log folder as its newest log, after an older empty one.</summary>
    private static void Install(EngineSandbox sandbox, string engine)
    {
        EngineEndpoint endpoint = EngineEndpoint.Resolve(engine, sandbox.SkuaDir);
        Directory.CreateDirectory(endpoint.LogFilesDir);
        File.WriteAllText(Path.Combine(endpoint.LogFilesDir, "2026-10-01T00-00-00.000Z.jsonl"), "");
        File.Copy(Fixture(engine), Path.Combine(endpoint.LogFilesDir, "2026-10-05T13-58-20.000Z.jsonl"));
    }

    private static string Report(long seq, string engine, string comp, long? startedAt, long endedAt, string outcome) =>
        JsonSerializer.Serialize(new
        {
            seq, ts = endedAt, kind = "events", run = 1, type = "script.report",
            data = new
            {
                run = 1, script = "Ultrasv3/UltraEngineer.cs", name = "ultra.attempt",
                data = new { boss = "UltraEngineer", comp, @class = "King's Echo", role = "DroneKiller", startedAt, endedAt, outcome, deaths = Array.Empty<object>() },
            },
        }, ControlJson.Options);

    private static string? Text(JsonElement o, string property) => o.GetProperty(property).GetString();

    private static string Summary(JsonElement account) =>
        $"{Text(account, "engine")} {Text(account, "class")}/{Text(account, "role")} {Text(account, "outcome")} "
        + string.Join(",", account.GetProperty("deaths").EnumerateArray().Select(d => d.GetProperty("atSec").GetDouble()));
}
