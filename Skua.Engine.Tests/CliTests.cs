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
        Assert.Equal("notStarted", json.RootElement.GetProperty("game").GetProperty("state").GetString());
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
    public async Task Skua_login_logs_the_Test_Account_in_on_a_server_it_picks_with_no_credentials_passed()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        Dictionary<string, string> environment = GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain);

        ProcessResult servers = await sandbox.RunCliAsync(environment, "servers");
        ProcessResult login = await sandbox.RunCliAsync(environment, "login");
        ProcessResult status = await sandbox.RunCliAsync(environment, "status");
        ProcessResult again = await sandbox.RunCliAsync(environment, "login", "Sir Ver", "--json");
        ProcessResult logout = await sandbox.RunCliAsync(environment, "logout");
        ProcessResult unknown = await sandbox.RunCliAsync(environment, "login", "Nowhere", "--timeout", "5");

        Assert.Equal(0, servers.ExitCode);
        Assert.Contains("Galanoth", servers.Stdout);
        Assert.Matches(@"Artix\s+1500/1500\s+full", servers.Stdout);
        Assert.Equal((0, "Logged in on Sir Ver."), (login.ExitCode, login.Stdout.Trim()));
        Assert.Contains("playing on Sir Ver", status.Stdout);
        Assert.Equal(0, again.ExitCode);
        using (JsonDocument json = JsonDocument.Parse(again.Stdout))
            Assert.True(json.RootElement.GetProperty("alreadyLoggedIn").GetBoolean());
        Assert.Equal((0, "Logged out."), (logout.ExitCode, logout.Stdout.Trim()));
        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), unknown.ExitCode);
        Assert.Contains("No server is named 'Nowhere'", unknown.Stderr);
    }

    [Fact]
    public async Task Skua_join_jump_and_the_queries_print_the_location_and_state()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        Dictionary<string, string> environment = GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain);

        ProcessResult notLoggedIn = await sandbox.RunCliAsync(environment, "join", "yulgar");
        await sandbox.RunCliAsync(environment, "login", "Galanoth");
        ProcessResult join = await sandbox.RunCliAsync(environment, "join", "yulgar", "Upstairs", "Left");
        ProcessResult jump = await sandbox.RunCliAsync(environment, "jump", "upstairs", "--json");
        ProcessResult status = await sandbox.RunCliAsync(environment, "status");
        ProcessResult bank = await sandbox.RunCliAsync(environment, "inventory", "bank");
        ProcessResult quests = await sandbox.RunCliAsync(environment, "quests", "active");
        ProcessResult map = await sandbox.RunCliAsync(environment, "map");
        ProcessResult drops = await sandbox.RunCliAsync(environment, "drops", "--json");

        Assert.Equal(ExitCodes.For(ErrorCode.NotLoggedIn), notLoggedIn.ExitCode);
        Assert.Contains("isn't playing", notLoggedIn.Stderr);
        Assert.Equal((0, "Now on yulgar in Upstairs (Left)."), (join.ExitCode, join.Stdout.Trim()));
        using (JsonDocument location = JsonDocument.Parse(jump.Stdout))
            Assert.True(location.RootElement.GetProperty("alreadyThere").GetBoolean());
        Assert.Contains("Player  SkuaTester, level 10 Healer, HP 1000/1000, MP 80/100, 5000 gold, on yulgar in Upstairs (Left)", status.Stdout);
        Assert.Equal(0, bank.ExitCode);
        Assert.StartsWith("bank: 1/10 slots used", bank.Stdout);
        Assert.Contains("Bank Relic  2/10  Item", bank.Stdout);
        Assert.Contains("1001 Slime Time: inProgress", quests.Stdout);
        Assert.Contains("needs Slime Sample 3/5 (temp)", quests.Stdout);
        Assert.Contains("Cells     Enter, Upstairs, Room", map.Stdout);
        using (JsonDocument none = JsonDocument.Parse(drops.Stdout))
            Assert.Equal(0, none.RootElement.GetProperty("drops").GetArrayLength());
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
    public async Task Screenshot_writes_the_PNG_to_out_and_prints_its_path()
    {
        await using EngineSandbox sandbox = new();
        string path = Path.Combine(sandbox.SkuaDir, "shots", "login.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        ProcessResult result = await sandbox.RunCliAsync("screenshot", "--out", path);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(path, result.Stdout.Trim());
        Assert.Equal((958, 550), ScreenshotTests.PngSize(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Screenshot_without_out_writes_a_new_file_in_the_working_directory()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("screenshot", "--max-width", "200", "--json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        string path = json.RootElement.GetProperty("path").GetString()!;
        Assert.EndsWith(".png", path);
        Assert.True(File.Exists(Path.Combine(sandbox.SkuaDir, Path.GetFileName(path))), path);
        Assert.Equal(200, json.RootElement.GetProperty("width").GetInt32());
        Assert.Equal((200, 115), ScreenshotTests.PngSize(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Screenshots_without_out_each_write_their_own_file()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => sandbox.RunCliAsync("screenshot")));

        Assert.All(results, r => Assert.Equal(0, r.ExitCode));
        Assert.Equal(4, results.Select(r => r.Stdout.Trim()).Distinct().Count());
        Assert.Equal(4, Directory.GetFiles(sandbox.SkuaDir, "skua-screenshot-*.png").Length);
    }

    [Fact]
    public async Task Screenshot_to_an_unwritable_path_exits_with_the_invalid_argument_code()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("screenshot", "--out", Path.Combine(sandbox.SkuaDir, "missing", "x.png"));

        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), result.ExitCode);
        Assert.Contains("missing", result.Stderr);
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
