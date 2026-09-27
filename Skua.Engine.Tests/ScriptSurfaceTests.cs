using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>The Script operations and <c>eval</c> through the <c>skua</c> CLI and <c>skua mcp</c>.</summary>
public class ScriptSurfaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Counted = TestScripts.Main(
        """bot.Log($"count={bot.Config.Get<int>("count")}");""",
        """
        public string OptionsStorage = "TestCounted";

        public List<IOption> Options = new() { new Option<int>("count", "Count", "How many.", 5) };
        """);

    [Fact]
    public async Task The_CLI_lists_options_starts_waits_for_and_reports_a_Script()
    {
        await using EngineSandbox sandbox = new();
        TestScripts.Write(sandbox, "Tests/Counted.cs", Counted);

        ProcessResult options = await sandbox.RunCliAsync("script", "options", "Tests/Counted.cs");
        ProcessResult start = await sandbox.RunCliAsync("script", "start", "Tests/Counted.cs", "--option", "count=7", "--json");
        ProcessResult wait = await sandbox.RunCliAsync("script", "wait", "--timeout", "60");
        ProcessResult status = await sandbox.RunCliAsync("script", "status", "--json");
        ProcessResult logs = await sandbox.RunCliAsync("logs", "script");

        Assert.True(options.ExitCode == 0, options.Stderr);
        Assert.Contains("count", options.Stdout);
        Assert.Contains("How many.", options.Stdout);
        Assert.True(start.ExitCode == 0, start.Stderr);
        int run = JsonDocument.Parse(start.Stdout).RootElement.GetProperty("run").GetInt32();
        Assert.True(wait.ExitCode == 0, wait.Stderr);
        Assert.Contains($"Run {run} (Tests/Counted.cs) completed", wait.Stdout);
        JsonElement lastRun = JsonDocument.Parse(status.Stdout).RootElement.GetProperty("lastRun");
        Assert.Equal("completed", lastRun.GetProperty("outcome").GetString());
        Assert.Contains("count=7", logs.Stdout);
    }

    [Fact]
    public async Task The_CLI_stops_a_Script_and_rejects_a_malformed_option()
    {
        await using EngineSandbox sandbox = new();
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);

        ProcessResult malformed = await sandbox.RunCliAsync("script", "start", "Tests/Loop.cs", "--option", "count");
        ProcessResult start = await sandbox.RunCliAsync("script", "start", "Tests/Loop.cs");
        ProcessResult status = await sandbox.RunCliAsync("status");
        ProcessResult stop = await sandbox.RunCliAsync("script", "stop");

        Assert.NotEqual(0, malformed.ExitCode);
        Assert.Contains("key=value", malformed.Stderr);
        Assert.True(start.ExitCode == 0, start.Stderr);
        Assert.Contains("Script  running Tests/Loop.cs", status.Stdout);
        Assert.True(stop.ExitCode == 0, stop.Stderr);
        Assert.Contains("Stopped run", stop.Stdout);
    }

    [Fact]
    public async Task The_CLI_exits_with_the_CompileFailed_code_and_prints_the_diagnostics()
    {
        await using EngineSandbox sandbox = new();
        TestScripts.Write(sandbox, "Tests/Broken.cs", TestScripts.Main("""bot.Log("x")"""));

        ProcessResult human = await sandbox.RunCliAsync("script", "start", "Tests/Broken.cs");
        ProcessResult json = await sandbox.RunCliAsync("script", "start", "Tests/Broken.cs", "--json");

        Assert.Equal(ExitCodes.For(ErrorCode.CompileFailed), human.ExitCode);
        Assert.Contains("CS1002", human.Stderr);
        JsonElement error = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("error");
        Assert.Equal("compileFailed", error.GetProperty("code").GetString());
        Assert.Contains(error.GetProperty("diagnostics").EnumerateArray(), d => d.GetString()!.Contains("CS1002", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_CLI_evals_a_snippet_from_an_argument()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult value = await sandbox.RunCliAsync("eval", "6 * 7");
        ProcessResult logged = await sandbox.RunCliAsync("eval", """Bot.Log("hi"); return new { a = 1 };""", "--json");

        Assert.True(value.ExitCode == 0, value.Stderr);
        Assert.Equal("42", value.Stdout.Trim());
        JsonElement result = JsonDocument.Parse(logged.Stdout).RootElement;
        Assert.Equal(1, result.GetProperty("value").GetProperty("a").GetInt32());
        Assert.Equal("hi", result.GetProperty("logs")[0].GetString());
    }

    [Fact]
    public async Task Skua_mcp_exposes_the_Script_tools_and_eval()
    {
        await using EngineSandbox sandbox = new();
        TestScripts.Write(sandbox, "Tests/Counted.cs", Counted);
        await using McpClient client = await McpTests.ConnectAsync(sandbox);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: Ct);
        CallToolResult options = await client.CallToolAsync("script_options", new Dictionary<string, object?> { ["script"] = "Tests/Counted.cs" }, cancellationToken: Ct);
        CallToolResult start = await client.CallToolAsync("script_start",
            new Dictionary<string, object?> { ["script"] = "Tests/Counted.cs", ["options"] = new Dictionary<string, string> { ["count"] = "9" }, ["dialogs"] = "cancel" },
            cancellationToken: Ct);
        CallToolResult wait = await client.CallToolAsync("script_wait", new Dictionary<string, object?> { ["timeoutSec"] = 60 }, cancellationToken: Ct);
        CallToolResult status = await client.CallToolAsync("script_status", cancellationToken: Ct);
        CallToolResult stop = await client.CallToolAsync("script_stop", cancellationToken: Ct);
        CallToolResult eval = await client.CallToolAsync("eval", new Dictionary<string, object?> { ["code"] = "6 * 7" }, cancellationToken: Ct);
        CallToolResult broken = await client.CallToolAsync("eval", new Dictionary<string, object?> { ["code"] = "Bot.Nope" }, cancellationToken: Ct);

        Assert.Superset(new HashSet<string> { "script_options", "script_start", "script_stop", "script_status", "script_wait", "eval" }, tools.Select(t => t.Name).ToHashSet());
        Assert.All(new[] { options, start, wait, status, stop, eval }, r => Assert.NotEqual(true, r.IsError));
        Assert.Equal("count", options.StructuredContent!.Value.GetProperty("options")[0].GetProperty("key").GetString());
        Assert.Equal("ended", wait.StructuredContent!.Value.GetProperty("reason").GetString());
        Assert.Equal("completed", status.StructuredContent!.Value.GetProperty("lastRun").GetProperty("outcome").GetString());
        Assert.False(stop.StructuredContent!.Value.GetProperty("wasRunning").GetBoolean());
        Assert.Equal(42, eval.StructuredContent!.Value.GetProperty("value").GetInt32());
        Assert.True(broken.IsError);
        Assert.StartsWith("CompileFailed: ", ((TextContentBlock)broken.Content.Single()).Text);
        Assert.Contains("count=9", (await sandbox.RunCliAsync("logs", "script")).Stdout);
    }
}
