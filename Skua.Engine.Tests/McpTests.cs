using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Skua.Control;

namespace Skua.Engine.Tests;

public class McpTests
{
    [Fact]
    public async Task Skua_mcp_exposes_status_as_a_tool_that_auto_starts_the_Engine()
    {
        await using EngineSandbox sandbox = new();
        await using McpClient client = await ConnectAsync(sandbox);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        CallToolResult result = await client.CallToolAsync("status", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(tools, tool => tool.Name == "status");
        Assert.NotEqual(true, result.IsError);
        StatusDto status = JsonSerializer.Deserialize<StatusDto>(((TextContentBlock)result.Content.Single()).Text, ControlJson.Options)!;
        Assert.Equal("default", status.Engine.Name);
        Assert.Equal("default", result.StructuredContent?.GetProperty("engine").GetProperty("name").GetString());
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

    /// <summary>Starts <c>skua mcp</c> with this sandbox's data folder and extra environment, and connects to it.</summary>
    internal static Task<McpClient> ConnectAsync(EngineSandbox sandbox, IDictionary<string, string>? environment = null)
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
            Arguments = ["mcp"],
            ShutdownTimeout = TimeSpan.FromSeconds(1),
            EnvironmentVariables = variables,
        }), cancellationToken: TestContext.Current.CancellationToken);
    }
}
