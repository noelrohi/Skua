using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        Assert.Equal("engine", engine.GetProperty("host").GetString());
        Assert.True(json.RootElement.GetProperty("game").GetProperty("gameHostUp").GetBoolean());
        Assert.Equal("notStarted", json.RootElement.GetProperty("game").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Status_prints_human_text_by_default()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Engine  default (pid ", result.Stdout);
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
        using (JsonDocument json = JsonDocument.Parse(running.Stdout))
            Assert.Equal("engine", json.RootElement.GetProperty("host").GetString());
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
        ProcessResult same = await sandbox.RunCliAsync(environment, "login");
        ProcessResult logout = await sandbox.RunCliAsync(environment, "logout");
        ProcessResult unknown = await sandbox.RunCliAsync(environment, "login", "Nowhere", "--timeout", "5");

        Assert.Equal(0, servers.ExitCode);
        Assert.Contains("Galanoth", servers.Stdout);
        Assert.Matches(@"Artix\s+1500/1500\s+full", servers.Stdout);
        Assert.Equal((0, "Logged in as SkuaTester (the Test Account) on Sir Ver."), (login.ExitCode, login.Stdout.Trim()));
        Assert.Contains("playing on Sir Ver", status.Stdout);
        Assert.Equal(0, again.ExitCode);
        using (JsonDocument json = JsonDocument.Parse(again.Stdout))
            Assert.True(json.RootElement.GetProperty("alreadyLoggedIn").GetBoolean());
        Assert.Equal((0, "Already playing as SkuaTester (the Test Account) on Sir Ver."), (same.ExitCode, same.Stdout.Trim()));
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

        await sandbox.RunCliAsync(environment, "login", "Galanoth");
        ProcessResult join = await sandbox.RunCliAsync(environment, "join", "yulgar", "Upstairs", "Left");
        ProcessResult jump = await sandbox.RunCliAsync(environment, "jump", "upstairs", "--json");
        ProcessResult status = await sandbox.RunCliAsync(environment, "status");
        ProcessResult bank = await sandbox.RunCliAsync(environment, "inventory", "bank");
        ProcessResult quests = await sandbox.RunCliAsync(environment, "quests", "active");
        ProcessResult map = await sandbox.RunCliAsync(environment, "map");
        ProcessResult drops = await sandbox.RunCliAsync(environment, "drops", "--json");
        await sandbox.RunCliAsync(environment, "logout");
        ProcessResult notLoggedIn = await sandbox.RunCliAsync(environment, "join", "yulgar");

        Assert.Equal(ExitCodes.For(ErrorCode.NotLoggedIn), notLoggedIn.ExitCode);
        Assert.Contains("isn't playing", notLoggedIn.Stderr);
        Assert.Equal((0, "Now on yulgar in Upstairs (Left)."), (join.ExitCode, join.Stdout.Trim()));
        using (JsonDocument location = JsonDocument.Parse(jump.Stdout))
            Assert.True(location.RootElement.GetProperty("alreadyThere").GetBoolean());
        Assert.Contains("Player  SkuaTester, level 10 Healer, XP 1500/4000 (37.5%), HP 1000/1000, MP 80/100, 5000 gold, on yulgar in Upstairs (Left)", status.Stdout);
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
    public async Task Status_always_has_a_Player_line_while_playing_unknown_or_stale_with_its_age_when_the_game_doesnt_answer_in_time()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            await session.GameHost.DoAsync("delay getGameObject 6000");
            ProcessResult unknown = await sandbox.RunCliAsync("status");
            await session.GameHost.DoAsync("delay getGameObject 0");
            Assert.Null((await QueryTests.FreshStatusAsync(session.Connection)).PlayerAgeSec);
            await session.GameHost.DoAsync("delay getGameObject 6000");
            ProcessResult stale = await sandbox.RunCliAsync("status");

            Assert.Contains("Player  unknown: the game didn't answer in time", unknown.Stdout);
            Assert.Matches(@"Player  SkuaTester, level 10 Healer, .*, on battleon in Enter \(Spawn\) \(stale, read \d+ s ago\)", stale.Stdout);
        }
        finally
        {
            await session.GameHost.DoAsync("delay getGameObject 0");
        }
    }

    [Fact]
    public async Task A_protocol_mismatch_with_an_Engine_it_cant_replace_exits_with_its_code_and_a_stop_hint()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox, predatesShutdownIfIdle: true);

        ProcessResult human = await sandbox.RunCliAsync("status");
        ProcessResult json = await sandbox.RunCliAsync("status", "--json");

        Assert.Equal(ExitCodes.For(ErrorCode.ProtocolMismatch), human.ExitCode);
        Assert.Contains("Run 'skua engine stop'", human.Stderr);
        Assert.Equal(ExitCodes.For(ErrorCode.ProtocolMismatch), json.ExitCode);
        using JsonDocument error = JsonDocument.Parse(json.Stdout);
        Assert.Equal("protocolMismatch", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(other.ShutdownRequested);
    }

    [Fact]
    public async Task Engine_stop_against_the_Skua_apps_Engine_exits_with_the_EngineOwnedByApp_code_and_leaves_it_running()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine app = new(sandbox, ControlProtocol.Version, host: EngineHost.App);

        ProcessResult human = await sandbox.RunCliAsync("engine", "stop");
        ProcessResult json = await sandbox.RunCliAsync("engine", "stop", "--json");

        Assert.Equal(ExitCodes.For(ErrorCode.EngineOwnedByApp), human.ExitCode);
        Assert.Equal("skua: The Skua app owns Engine 'default'; quit the app to stop it.", human.Stderr.Trim());
        Assert.Equal(ExitCodes.For(ErrorCode.EngineOwnedByApp), json.ExitCode);
        using JsonDocument error = JsonDocument.Parse(json.Stdout);
        Assert.Equal("engineOwnedByApp", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(app.ShutdownCalled);
        Assert.False(app.ShutdownRequested);
        Assert.True(EngineLock.IsHeld(sandbox.Endpoint.LockPath));
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
    public async Task Logs_tail_prints_the_newest_entries_and_takes_neither_max_nor_follow()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Repeat(30, "send F trace {i}");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            await connection.WaitForLogsAsync(LogKind.Flash, 30);

            ProcessResult tail = await sandbox.RunCliAsync("logs", "flash", "--tail", "2", "--json");
            ProcessResult withMax = await sandbox.RunCliAsync("logs", "--tail", "2", "--max", "5");
            ProcessResult withFollow = await sandbox.RunCliAsync("logs", "-f", "--tail", "2");

            Assert.Equal(0, tail.ExitCode);
            using (JsonDocument json = JsonDocument.Parse(tail.Stdout))
                Assert.Equal(["trace 28", "trace 29"], json.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("text").GetString()));
            Assert.Equal(1, withMax.ExitCode);
            Assert.Contains("--tail", withMax.Stderr);
            Assert.Equal(1, withFollow.ExitCode);
            Assert.Contains("--tail", withFollow.Stderr);
        }
    }

    [Fact]
    public async Task The_engine_option_talks_to_the_named_Engine_and_wins_over_SKUA_ENGINE_SOCKET()
    {
        await using EngineSandbox sandbox = new();
        EngineEndpoint named = EngineEndpoint.Resolve("farm-1", sandbox.SkuaDir);
        string otherSocket = Path.Combine(sandbox.SkuaDir, "other.sock");
        try
        {
            ProcessResult status = await sandbox.RunCliAsync(
                new Dictionary<string, string> { [EngineEndpoint.SocketVariable] = otherSocket }, "--engine", "farm-1", "status", "--json");
            ProcessResult engineStatus = await sandbox.RunCliAsync("engine", "status", "--engine", "farm-1", "--json");
            ProcessResult invalid = await sandbox.RunCliAsync("status", "--engine", "Not Valid");

            Assert.Equal(0, status.ExitCode);
            using (JsonDocument json = JsonDocument.Parse(status.Stdout))
                Assert.Equal("farm-1", json.RootElement.GetProperty("engine").GetProperty("name").GetString());
            Assert.True(File.Exists(named.SocketPath));
            Assert.False(File.Exists(otherSocket));
            Assert.False(File.Exists(sandbox.Endpoint.SocketPath));
            Assert.Equal("running", State(engineStatus));
            using (JsonDocument json = JsonDocument.Parse(engineStatus.Stdout))
                Assert.Equal("farm-1", json.RootElement.GetProperty("name").GetString());
            Assert.Equal(1, invalid.ExitCode);
            Assert.Contains("[a-z0-9-]{1,16}", invalid.Stderr);
        }
        finally
        {
            await EngineClient.StopAsync(named, EngineSandbox.StopTimeout);
        }
    }

    [Fact]
    public async Task Without_the_engine_option_an_auto_started_Engine_is_named_after_SKUA_ENGINE_SOCKETs_file()
    {
        await using EngineSandbox sandbox = new();
        string socket = Path.Combine(sandbox.SkuaDir, "supermovie1.sock");
        EngineEndpoint named = EngineEndpoint.Resolve("supermovie1", sandbox.SkuaDir, socket);
        try
        {
            ProcessResult status = await sandbox.RunCliAsync(
                new Dictionary<string, string> { [EngineEndpoint.SocketVariable] = socket }, "status", "--json");

            Assert.Equal(0, status.ExitCode);
            using (JsonDocument json = JsonDocument.Parse(status.Stdout))
                Assert.Equal("supermovie1", json.RootElement.GetProperty("engine").GetProperty("name").GetString());
            Assert.True(EngineLock.IsHeld(named.LockPath));
            Assert.False(File.Exists(sandbox.Endpoint.LockPath));
        }
        finally
        {
            await EngineClient.StopAsync(named, EngineSandbox.StopTimeout);
        }
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
    public async Task Script_start_follow_asks_a_Question_in_the_terminal_and_answers_with_the_developers_choice()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        Dictionary<string, string> environment = GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain);
        TestScripts.Write(sandbox, "Tests/Ask.cs", AskScript);

        Process follow = sandbox.StartCliInTerminal(environment, "script", "start", "Tests/Ask.cs", "--follow");
        OutputReader output = new(follow.StandardOutput);
        await output.WaitForAsync("Answer Question 1, 1) Yes, 2) No: ");
        await follow.StandardInput.WriteAsync("maybe\n");
        await output.WaitForAsync("'maybe' isn't a choice of Question 1.");
        await follow.StandardInput.WriteAsync("2\n");
        await follow.WaitForExitAsync(Timeout());
        string text = await output.EndAsync();

        Assert.Equal(0, follow.ExitCode);
        Assert.Contains("Started run 1 (Tests/Ask.cs).", text);
        Assert.Contains("before the Question", text);
        Assert.Contains("Question 1 'Confirm': Buy it? (Yes / No)", text);
        Assert.Contains("Question 1 answered by agent: No", text);
        Assert.Contains("answer False", text);
        Assert.Contains("Run 1 completed after", text);
    }

    [Fact]
    public async Task Script_start_follow_without_a_terminal_tells_how_to_answer_and_skua_dialogs_answers()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        Dictionary<string, string> environment = GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain);
        TestScripts.Write(sandbox, "Tests/Ask.cs", AskScript);

        Process follow = sandbox.StartCli(environment, "script", "start", "Tests/Ask.cs", "--follow");
        OutputReader output = new(follow.StandardOutput);
        await output.WaitForAsync("answer it with 'skua dialogs answer 1 <choice>'");
        ProcessResult status = await sandbox.RunCliAsync(environment, "status");
        ProcessResult dialogs = await sandbox.RunCliAsync(environment, "dialogs");
        ProcessResult unknown = await sandbox.RunCliAsync(environment, "dialogs", "answer", "1", "Maybe");
        ProcessResult answer = await sandbox.RunCliAsync(environment, "dialogs", "answer", "1", "yes");
        ProcessResult again = await sandbox.RunCliAsync(environment, "dialogs", "answer", "1", "no", "--json");
        await follow.WaitForExitAsync(Timeout());
        string text = await output.EndAsync();
        ProcessResult none = await sandbox.RunCliAsync(environment, "dialogs");

        Assert.Contains("Dialogs 1 Question pending; see 'skua dialogs'", status.Stdout);
        Assert.Equal(0, dialogs.ExitCode);
        Assert.Contains("Question 1 'Confirm' (Yes / No), from Tests/Ask.cs on Script Thread,", dialogs.Stdout);
        Assert.Contains("  Buy it?", dialogs.Stdout);
        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), unknown.ExitCode);
        Assert.Equal((0, "Answered Question 1: Yes."), (answer.ExitCode, answer.Stdout.Trim()));
        Assert.Equal(ExitCodes.For(ErrorCode.DialogNotPending), again.ExitCode);
        Assert.Equal("dialogNotPending", JsonDocument.Parse(again.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, follow.ExitCode);
        Assert.Contains("Question 1 answered by agent: Yes", text);
        Assert.Contains("answer True", text);
        Assert.Equal("No Questions are pending.", none.Stdout.Trim());
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

    private static string AskScript { get; } = TestScripts.Main("""
        bot.Log("before the Question");
        bool? answer = bot.ShowMessageBox("Buy it?", "Confirm", true);
        bot.Log($"answer {(answer is null ? "null" : answer.ToString())}");
        """);

    private static CancellationToken Timeout()
    {
        CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        return timeout.Token;
    }
}

/// <summary>Collects a process's output as it arrives, so a test can wait for some of it.</summary>
public sealed class OutputReader
{
    private readonly StringBuilder _text = new();
    private readonly Task _pump;

    public OutputReader(StreamReader reader)
    {
        _pump = Task.Run(async () =>
        {
            char[] buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                lock (_text)
                    _text.Append(buffer, 0, read);
            }
        });
    }

    public string Text
    {
        get
        {
            lock (_text)
                return _text.ToString();
        }
    }

    /// <summary>Waits until the output contains <paramref name="expected"/>.</summary>
    public async Task WaitForAsync(string expected)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!Text.Contains(expected, StringComparison.Ordinal))
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException($"The output never contained \"{expected}\"; it was:\n{Text}");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Waits until the output from <paramref name="from"/> on matches <paramref name="expected"/>, and returns that output.</summary>
    public async Task<string> WaitForAsync(Regex expected, int from)
    {
        Stopwatch waited = Stopwatch.StartNew();
        string text;
        while (!expected.IsMatch(text = Text[from..]))
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException($"The output never matched \"{expected}\" after {from} characters; it was:\n{Text}");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        return text;
    }

    /// <summary>Waits for the output to end, and returns all of it.</summary>
    public async Task<string> EndAsync()
    {
        await _pump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return Text;
    }
}
