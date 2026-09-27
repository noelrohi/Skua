using System.Diagnostics;
using System.Text.Json;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

public class CliTests
{
    [Fact]
    public async Task Status_json_prints_the_status_DTO()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("status", "--json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        JsonElement engine = json.RootElement.GetProperty("engine");
        Assert.Equal("default", engine.GetProperty("name").GetString());
        Assert.Equal(ControlProtocol.Version, engine.GetProperty("protocol").GetInt32());
        Assert.True(json.RootElement.GetProperty("game").GetProperty("gameHostUp").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("game").GetProperty("state").ValueKind);
    }

    [Fact]
    public async Task Status_prints_human_text_by_default()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Engine  default", result.Stdout);
        Assert.Contains("Game Host up", result.Stdout);
    }

    [Fact]
    public async Task Engine_start_status_and_stop_control_the_Engine_lifetime()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult before = await sandbox.RunCliAsync("engine", "status", "--json");
        ProcessResult start = await sandbox.RunCliAsync("engine", "start");
        ProcessResult running = await sandbox.RunCliAsync("engine", "status", "--json");
        ProcessResult stop = await sandbox.RunCliAsync("engine", "stop");
        ProcessResult after = await sandbox.RunCliAsync("engine", "status");

        Assert.Equal("stopped", State(before));
        Assert.Equal(0, start.ExitCode);
        Assert.Contains("is running", start.Stdout);
        Assert.Equal("running", State(running));
        Assert.Equal(0, stop.ExitCode);
        Assert.False(File.Exists(sandbox.Endpoint.SocketPath));
        Assert.False(EngineLock.IsHeld(sandbox.Endpoint.LockPath));
        Assert.Contains("is stopped", after.Stdout);
    }

    [Fact]
    public async Task A_protocol_mismatch_exits_with_its_code_and_a_stop_hint()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox);

        ProcessResult human = await sandbox.RunCliAsync("status");
        ProcessResult json = await sandbox.RunCliAsync("status", "--json");

        Assert.Equal(ExitCodes.For(ErrorCode.ProtocolMismatch), human.ExitCode);
        Assert.Contains("Run 'skua engine stop'", human.Stderr);
        Assert.Equal(ExitCodes.For(ErrorCode.ProtocolMismatch), json.ExitCode);
        using JsonDocument error = JsonDocument.Parse(json.Stdout);
        Assert.Equal("protocolMismatch", error.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_over_long_socket_path_exits_with_the_invalid_argument_code()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult longPath = await sandbox.RunCliAsync(
            new Dictionary<string, string> { [EngineEndpoint.SkuaDirVariable] = "/tmp/" + new string('x', 100) }, "status", "--json");

        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), longPath.ExitCode);
        using JsonDocument error = JsonDocument.Parse(longPath.Stdout);
        Assert.Equal("invalidArgument", error.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Logs_json_prints_a_page_with_its_next_cursor()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("logs", "events", "--max", "1", "--json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        JsonElement entry = json.RootElement.GetProperty("entries").EnumerateArray().Single();
        Assert.Equal("engine.started", entry.GetProperty("type").GetString());
        Assert.Equal("events", entry.GetProperty("kind").GetString());
        Assert.False(entry.TryGetProperty("text", out _));
        Assert.False(json.RootElement.GetProperty("gap").GetBoolean());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("next").GetString()));
    }

    [Fact]
    public async Task Logs_prints_one_line_per_entry_by_default()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("logs");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("engine.started", result.Stdout);
        Assert.Contains("next ", result.Stdout);
    }

    [Fact]
    public async Task Logs_follow_streams_entries_after_the_cursor_as_JSON_lines()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Send('F', "before").Sleep(1500).Send('F', "live");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            await connection.WaitForLogsAsync(LogKind.Flash, 1);
            LogPage pulled = await connection.LogsAsync(LogKind.Flash, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("before", Assert.Single(pulled.Entries).Text);

            Process follow = sandbox.StartCli("logs", "flash", "-f", "--after", pulled.Next, "--json");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            string line = (await follow.StandardOutput.ReadLineAsync(timeout.Token))!;

            using JsonDocument entry = JsonDocument.Parse(line);
            Assert.Equal("live", entry.RootElement.GetProperty("text").GetString());
            Assert.Equal("flash", entry.RootElement.GetProperty("kind").GetString());
            Assert.False(follow.HasExited);
        }
    }

    [Fact]
    public async Task Logs_follow_takes_several_kinds_but_a_page_takes_one()
    {
        await using EngineSandbox sandbox = new();

        Process follow = sandbox.StartCli("logs", "events", "debug", "-f");
        ProcessResult page = await sandbox.RunCliAsync("logs", "events", "debug");
        ProcessResult maxWithFollow = await sandbox.RunCliAsync("logs", "-f", "--max", "5");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        HashSet<string> kinds = [];
        while (kinds.Count < 2)
            kinds.Add((await follow.StandardOutput.ReadLineAsync(timeout.Token))!.Split(' ')[2]);
        Assert.Equal(["events", "debug"], kinds.Order().Reverse());
        Assert.Equal(1, page.ExitCode);
        Assert.Contains("one kind", page.Stderr);
        Assert.Equal(1, maxWithFollow.ExitCode);
        Assert.Contains("--max", maxWithFollow.Stderr);
    }

    [Fact]
    public void Every_error_code_has_its_own_nonzero_exit_code()
    {
        int[] codes = Enum.GetValues<ErrorCode>().Select(ExitCodes.For).ToArray();

        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.DoesNotContain(codes, code => code is 0 or 1 or 2 or > 125);
    }

    private static string? State(ProcessResult result) =>
        JsonDocument.Parse(result.Stdout).RootElement.GetProperty("state").GetString();
}
