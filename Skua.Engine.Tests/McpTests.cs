using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Skua.Control;

namespace Skua.Engine.Tests;

public class McpTests
{
    [Fact]
    public async Task Skua_mcp_exposes_status_as_a_tool_that_answers_for_a_running_Engine()
    {
        await using EngineSandbox sandbox = new();
        Assert.Equal(0, (await sandbox.RunCliAsync("engine", "start")).ExitCode);
        await using McpClient client = await ConnectAsync(sandbox);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        CallToolResult result = await client.CallToolAsync("status", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(tools, tool => tool.Name == "status");
        Assert.NotEqual(true, result.IsError);
        StatusDto status = JsonSerializer.Deserialize<StatusDto>(((TextContentBlock)result.Content.Single()).Text, ControlJson.Options)!;
        Assert.Equal("default", status.Engine.Name);
        Assert.Equal("default", result.StructuredContent?.GetProperty("engine").GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_tools_that_need_no_Engine_leave_the_Engine_list_unchanged()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("noelrohi", "Scripts", "Skua", new FakeScript("Farm/Good.cs", TestScripts.Main("bot.Log(\"good\");"), "Good"));
        EngineEndpoint alt1 = EngineEndpoint.Resolve("alt1", sandbox.SkuaDir);
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string Tool, Dictionary<string, object?>? Arguments)[] calls =
        [
            ("scripts_source", null), ("scripts_update", null), ("scripts_search", new() { ["query"] = "good" }), ("scripts_list", null),
            ("scripts_new", null), ("engine_list", null), ("status", null),
        ];
        try
        {
            Assert.Equal(0, (await sandbox.RunCliAsync("--engine", "alt1", "engine", "start")).ExitCode);
            await using McpClient client = await ConnectAsync(sandbox, github.Environment());

            IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: ct);
            Dictionary<string, CallToolResult> results = [];
            foreach ((string tool, Dictionary<string, object?>? arguments) in calls)
                results[tool] = await client.CallToolAsync(tool, arguments, cancellationToken: ct);

            Assert.True(tools.Single(t => t.Name == "engine_list").ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.All(results.Where(r => r.Key != "status"), r => Assert.True(r.Value.IsError != true, $"{r.Key}: {((TextContentBlock)r.Value.Content[0]).Text}"));
            Assert.True(results["status"].IsError);
            Assert.Equal("EngineUnavailable: Engine 'default' isn't running; 'skua engine start' starts it.", ((TextContentBlock)results["status"].Content.Single()).Text);
            JsonElement listed = Assert.Single(results["engine_list"].StructuredContent!.Value.GetProperty("engines").EnumerateArray());
            Assert.Equal("alt1", listed.GetProperty("engine").GetProperty("name").GetString());
            Assert.Equal("running", listed.GetProperty("engine").GetProperty("state").GetString());
            Assert.Equal("alt1", listed.GetProperty("status").GetProperty("engine").GetProperty("name").GetString());
            Assert.Equal(["alt1"], EngineEndpoint.InDataFolder(sandbox.SkuaDir).Select(e => e.Name));
            Assert.False(EngineLock.IsHeld(sandbox.Endpoint.LockPath));
        }
        finally
        {
            await EngineClient.StopAsync(alt1, EngineSandbox.StopTimeout);
        }
    }

    [Fact]
    public async Task Status_names_the_Engine_that_engine_or_SKUA_ENGINE_SOCKET_names_and_starts_none()
    {
        await using EngineSandbox sandbox = new();
        EngineEndpoint alt1 = EngineEndpoint.Resolve("alt1", sandbox.SkuaDir);
        EngineEndpoint alt2 = EngineEndpoint.Resolve("alt2", sandbox.SkuaDir);
        Dictionary<string, string> atAlt2 = new() { [EngineEndpoint.SocketVariable] = alt2.SocketPath };
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            Assert.Equal(0, (await sandbox.RunCliAsync("--engine", "alt1", "engine", "start")).ExitCode);
            Assert.Equal(0, (await sandbox.RunCliAsync("--engine", "alt2", "engine", "start")).ExitCode);

            string?[] names = new string?[3];
            string?[] errors = new string?[2];
            await using (McpClient client = await ConnectAsync(sandbox, arguments: ["--engine", "alt1"]))
                names[0] = (await client.CallToolAsync("status", cancellationToken: ct)).StructuredContent?.GetProperty("engine").GetProperty("name").GetString();
            await using (McpClient client = await ConnectAsync(sandbox, atAlt2))
                names[1] = (await client.CallToolAsync("status", cancellationToken: ct)).StructuredContent?.GetProperty("engine").GetProperty("name").GetString();
            await using (McpClient client = await ConnectAsync(sandbox, arguments: ["--engine", "alt3"]))
                errors[0] = ((TextContentBlock)(await client.CallToolAsync("status", cancellationToken: ct)).Content.Single()).Text;
            await using (McpClient client = await ConnectAsync(sandbox,
                new Dictionary<string, string> { [EngineEndpoint.SocketVariable] = EngineEndpoint.Resolve("alt4", sandbox.SkuaDir).SocketPath }))
                errors[1] = ((TextContentBlock)(await client.CallToolAsync("status", cancellationToken: ct)).Content.Single()).Text;
            ProcessResult cli = await sandbox.RunCliAsync(atAlt2, "status", "--json");
            names[2] = JsonDocument.Parse(cli.Stdout).RootElement.GetProperty("engine").GetProperty("name").GetString();

            Assert.Equal(["alt1", "alt2", "alt2"], names);
            Assert.Equal("EngineUnavailable: Engine 'alt3' isn't running; 'skua --engine alt3 engine start' starts it.", errors[0]);
            Assert.Equal("EngineUnavailable: Engine 'alt4' isn't running; 'skua --engine alt4 engine start' starts it.", errors[1]);
            Assert.Equal(["alt1", "alt2"], EngineEndpoint.InDataFolder(sandbox.SkuaDir).Select(e => e.Name).Order());
        }
        finally
        {
            await EngineClient.StopAsync(alt1, EngineSandbox.StopTimeout);
            await EngineClient.StopAsync(alt2, EngineSandbox.StopTimeout);
        }
    }

    [Fact]
    public async Task A_tool_that_drives_the_game_auto_starts_the_Engine()
    {
        await using EngineSandbox sandbox = new();
        await using McpClient client = await ConnectAsync(sandbox);

        CallToolResult result = await client.CallToolAsync("script_status", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("idle", result.StructuredContent?.GetProperty("state").GetString());
        Assert.True(EngineLock.IsHeld(sandbox.Endpoint.LockPath));
    }

    [Fact]
    public async Task Skua_mcp_exposes_logs_with_the_same_arguments_and_page()
    {
        await using EngineSandbox sandbox = new();
        await using McpClient client = await ConnectAsync(sandbox);

        CallToolResult result = await client.CallToolAsync("logs", new Dictionary<string, object?> { ["kind"] = "events", ["max"] = 1 },
            cancellationToken: TestContext.Current.CancellationToken);
        CallToolResult bad = await client.CallToolAsync("logs", new Dictionary<string, object?> { ["after"] = "nonsense" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        LogPage page = JsonSerializer.Deserialize<LogPage>(((TextContentBlock)result.Content.Single()).Text, ControlJson.Options)!;
        Assert.Equal(EventTypes.EngineStarted, Assert.Single(page.Entries).Type);
        Assert.True(bad.IsError);
        Assert.StartsWith("InvalidArgument: ", ((TextContentBlock)bad.Content.Single()).Text);
    }

    [Fact]
    public async Task A_failed_call_returns_isError_with_the_code_and_message()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox);
        await using McpClient client = await ConnectAsync(sandbox);

        CallToolResult result = await client.CallToolAsync("status", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.StartsWith("ProtocolMismatch: ", ((TextContentBlock)result.Content.Single()).Text);
    }

    [Fact]
    public async Task The_screenshot_tool_returns_an_image_block_of_the_PNG()
    {
        await using EngineSandbox sandbox = new();
        await using McpClient client = await ConnectAsync(sandbox);

        CallToolResult result = await client.CallToolAsync("screenshot", new Dictionary<string, object?> { ["maxWidth"] = 479 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        ImageContentBlock image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal((479, 275), ScreenshotTests.PngSize(image.DecodedData.ToArray()));
    }

    [Fact]
    public async Task A_failed_screenshot_returns_isError_with_its_code()
    {
        await using EngineSandbox sandbox = new();
        await using McpClient client = await ConnectAsync(sandbox);

        CallToolResult result = await client.CallToolAsync("screenshot", new Dictionary<string, object?> { ["maxWidth"] = 0 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.StartsWith("InvalidArgument: ", ((TextContentBlock)result.Content.Single()).Text);
    }

    [Fact]
    public async Task The_login_tools_log_in_and_out_with_the_same_arguments_and_DTOs()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        await using McpClient client = await ConnectAsync(sandbox, GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain));

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        CallToolResult servers = await client.CallToolAsync("servers", cancellationToken: TestContext.Current.CancellationToken);
        CallToolResult login = await client.CallToolAsync("login", new Dictionary<string, object?> { ["server"] = "Galanoth", ["timeoutSec"] = 60 },
            cancellationToken: TestContext.Current.CancellationToken);
        CallToolResult logout = await client.CallToolAsync("logout", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Subset(tools.Select(t => t.Name).ToHashSet(), new HashSet<string> { "servers", "login", "logout" });
        Assert.Equal(GameFixture.Servers.Length, servers.StructuredContent!.Value.GetProperty("servers").GetArrayLength());
        Assert.Equal(new LoginResult("Galanoth", false, "SkuaTester", IsTestAccount: true), JsonSerializer.Deserialize<LoginResult>(((TextContentBlock)login.Content[0]).Text, ControlJson.Options));
        Assert.Equal("Logged in as SkuaTester (the Test Account) on Galanoth.", ((TextContentBlock)login.Content[1]).Text);
        Assert.True(logout.StructuredContent!.Value.GetProperty("wasLoggedIn").GetBoolean());
    }

    [Fact]
    public async Task The_move_and_query_tools_take_the_same_arguments_and_return_the_same_DTOs()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        await using McpClient client = await ConnectAsync(sandbox, GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain));
        CancellationToken ct = TestContext.Current.CancellationToken;

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: ct);
        await client.CallToolAsync("login", new Dictionary<string, object?> { ["server"] = "Galanoth" }, cancellationToken: ct);
        CallToolResult join = await client.CallToolAsync("join", new Dictionary<string, object?> { ["map"] = "yulgar", ["cell"] = "Room", ["timeoutSec"] = 30 }, cancellationToken: ct);
        CallToolResult jump = await client.CallToolAsync("jump", new Dictionary<string, object?> { ["cell"] = "Enter", ["pad"] = "Right" }, cancellationToken: ct);
        CallToolResult inventory = await client.CallToolAsync("inventory", new Dictionary<string, object?> { ["kind"] = "temp" }, cancellationToken: ct);
        CallToolResult quests = await client.CallToolAsync("quests", new Dictionary<string, object?> { ["filter"] = "active" }, cancellationToken: ct);
        CallToolResult map = await client.CallToolAsync("map", cancellationToken: ct);
        CallToolResult drops = await client.CallToolAsync("drops", cancellationToken: ct);
        await client.CallToolAsync("logout", cancellationToken: ct);
        CallToolResult loggedOut = await client.CallToolAsync("map", cancellationToken: ct);

        Assert.Subset(tools.Select(t => t.Name).ToHashSet(), new HashSet<string> { "join", "jump", "inventory", "quests", "quest_complete", "map", "drops" });
        Assert.True(loggedOut.IsError);
        Assert.StartsWith("NotLoggedIn: ", ((TextContentBlock)loggedOut.Content.Single()).Text);
        Assert.Equal(new LocationResult("yulgar", "Room", "Spawn", false), JsonSerializer.Deserialize<LocationResult>(((TextContentBlock)join.Content.Single()).Text, ControlJson.Options));
        Assert.Equal("Right", jump.StructuredContent!.Value.GetProperty("pad").GetString());
        JsonElement temp = inventory.StructuredContent!.Value;
        Assert.Equal(JsonValueKind.Null, temp.GetProperty("totalSlots").ValueKind);
        Assert.Equal("Slime Sample", temp.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(2, quests.StructuredContent!.Value.GetProperty("quests").GetArrayLength());
        Assert.Equal("yulgar", map.StructuredContent!.Value.GetProperty("name").GetString());
        Assert.Equal(0, drops.StructuredContent!.Value.GetProperty("drops").GetArrayLength());
    }

    [Fact]
    public async Task The_dialog_tools_list_and_answer_a_Question_with_the_same_arguments_and_DTOs()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        await using McpClient client = await ConnectAsync(sandbox, GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain));
        CancellationToken ct = TestContext.Current.CancellationToken;
        TestScripts.Write(sandbox, "Tests/Buttons.cs", TestScripts.Main("""
            var picked = bot.ShowMessageBox("Which class?", "Class", "Warrior", "Mage");
            bot.Log($"picked {picked.Text}");
            """));

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: ct);
        await client.CallToolAsync("script_start", new Dictionary<string, object?> { ["script"] = "Tests/Buttons.cs" }, cancellationToken: ct);
        CallToolResult wait = await client.CallToolAsync("script_wait", new Dictionary<string, object?> { ["timeoutSec"] = 60 }, cancellationToken: ct);
        CallToolResult dialogs = await client.CallToolAsync("dialogs", cancellationToken: ct);
        QuestionDto question = JsonSerializer.Deserialize<DialogsResult>(((TextContentBlock)dialogs.Content.Single()).Text, ControlJson.Options)!.Questions.Single();
        CallToolResult answer = await client.CallToolAsync("dialog_answer", new Dictionary<string, object?> { ["id"] = question.Id, ["choice"] = "mage" }, cancellationToken: ct);
        CallToolResult again = await client.CallToolAsync("dialog_answer", new Dictionary<string, object?> { ["id"] = question.Id, ["choice"] = "Mage" }, cancellationToken: ct);

        Assert.Subset(tools.Select(t => t.Name).ToHashSet(), new HashSet<string> { "dialogs", "dialog_answer" });
        Assert.Equal("question", wait.StructuredContent!.Value.GetProperty("reason").GetString());
        Assert.Equal(["Warrior", "Mage"], question.Choices);
        Assert.Equal("Tests/Buttons.cs", question.Script);
        Assert.Equal(new DialogAnswerResult(question.Id, "Mage"), JsonSerializer.Deserialize<DialogAnswerResult>(((TextContentBlock)answer.Content.Single()).Text, ControlJson.Options));
        Assert.True(again.IsError);
        Assert.StartsWith("DialogNotPending: ", ((TextContentBlock)again.Content.Single()).Text);
    }

    /// <summary>Starts <c>skua mcp</c> with this sandbox's data folder, extra environment and arguments before <c>mcp</c>, and connects to it.</summary>
    internal static Task<McpClient> ConnectAsync(EngineSandbox sandbox, IDictionary<string, string>? environment = null, string[]? arguments = null)
    {
        Dictionary<string, string?> variables = new()
        {
            [EngineEndpoint.SkuaDirVariable] = sandbox.SkuaDir,
            [EngineEndpoint.SocketVariable] = null,
            [EngineClientOptions.EngineExecutableVariable] = EngineSandbox.EngineExecutable,
        };
        foreach ((string key, string value) in environment ?? new Dictionary<string, string>())
            variables[key] = value;

        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = EngineSandbox.CliExecutable,
            Arguments = [.. arguments ?? [], "mcp"],
            ShutdownTimeout = TimeSpan.FromSeconds(1),
            EnvironmentVariables = variables,
        }), cancellationToken: TestContext.Current.CancellationToken);
    }
}
