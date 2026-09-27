using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

public class ScriptSourceTests
{
    private static readonly FakeScript Leveling = new("Farm/Leveling.cs", "// leveling v1", "Leveling", "Levels you to 100.", "farm", "xp");
    private static readonly FakeScript Gold = new("Farm/Gold.cs", "// gold v1", "Gold Farm", "Farms gold.", "farm", "gold");
    private static readonly FakeScript CoreBots = new("CoreBots.cs", "// core v1");

    [Fact]
    public async Task The_first_update_downloads_the_full_tree_from_the_default_Script_Source()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, Gold, CoreBots);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);

        ScriptsUpdateResult result = await connection.ScriptsUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ScriptSourceDto("auqw", "Scripts", "Skua"), result.Source);
        Assert.Equal(ScriptsUpdateMode.Full, result.Mode);
        Assert.Equal(3, result.Downloaded);
        Assert.Empty(result.Failed);
        Assert.Equal("// leveling v1", await ReadScriptAsync(sandbox, "Farm/Leveling.cs"));
        Assert.Equal("// core v1", await ReadScriptAsync(sandbox, "CoreBots.cs"));
    }

    [Fact]
    public async Task The_Script_Source_setting_decides_where_scripts_json_and_the_files_come_from()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, CoreBots);
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, CoreBots with { Content = "// core v1 // skua-macos: patched" });
        SetScriptSource(sandbox, "noelrohi", "Scripts", "Skua");
        using EngineConnection connection = await StartEngineAsync(sandbox, github);

        ScriptsUpdateResult result = await connection.ScriptsUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ScriptSourceDto("noelrohi", "Scripts", "Skua"), result.Source);
        Assert.Equal("// core v1 // skua-macos: patched", await ReadScriptAsync(sandbox, "CoreBots.cs"));
        Assert.DoesNotContain(github.Requests, r => r.Contains("/auqw/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Later_updates_download_only_the_changed_Scripts()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, Gold, CoreBots);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);
        await connection.ScriptsUpdateAsync(TestContext.Current.CancellationToken);

        github.Commit("auqw", "Scripts", "Skua", Gold with { Content = "// gold v2" });
        github.ClearRequests();
        ScriptsUpdateResult incremental = await connection.ScriptsUpdateAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<string> downloaded = github.ScriptDownloads("auqw", "Scripts", "Skua");
        ScriptsUpdateResult upToDate = await connection.ScriptsUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ScriptsUpdateMode.Incremental, incremental.Mode);
        Assert.Equal(1, incremental.Downloaded);
        Assert.Equal(["Farm/Gold.cs"], downloaded);
        Assert.Equal("// gold v2", await ReadScriptAsync(sandbox, "Farm/Gold.cs"));
        Assert.Equal(ScriptsUpdateMode.UpToDate, upToDate.Mode);
        Assert.Equal(0, upToDate.Downloaded);
        Assert.Equal(incremental.Commit, upToDate.Commit);
    }

    [Fact]
    public async Task Changing_the_Script_Source_makes_the_next_update_a_full_download_from_it()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, CoreBots);
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, CoreBots with { Content = "// core fork" });
        using (EngineConnection upstream = await StartEngineAsync(sandbox, github))
        {
            await upstream.ScriptsUpdateAsync(TestContext.Current.CancellationToken);
        }
        await EngineClient.StopAsync(sandbox.Endpoint, EngineSandbox.StopTimeout, TestContext.Current.CancellationToken);

        SetScriptSource(sandbox, "noelrohi", "Scripts", "Skua");
        using EngineConnection fork = await StartEngineAsync(sandbox, github);
        ScriptsUpdateResult result = await fork.ScriptsUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ScriptsUpdateMode.Full, result.Mode);
        Assert.Equal(new ScriptSourceDto("noelrohi", "Scripts", "Skua"), result.Source);
        Assert.Equal("// core fork", await ReadScriptAsync(sandbox, "CoreBots.cs"));
    }

    [Fact]
    public async Task Search_finds_Scripts_by_name_description_tag_and_path()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, Gold, CoreBots, new FakeScript("Story/Doomwood.cs", "// story", "Doomwood Story", "Completes the Doomwood saga.", "story"));
        using EngineConnection connection = await StartEngineAsync(sandbox, github);
        CancellationToken ct = TestContext.Current.CancellationToken;

        Assert.Equal(["Farm/Leveling.cs"], Paths(await connection.ScriptsSearchAsync("leveling", null, ct)));
        Assert.Equal(["Story/Doomwood.cs"], Paths(await connection.ScriptsSearchAsync("saga", null, ct)));
        Assert.Equal(["Farm/Gold.cs", "Farm/Leveling.cs"], Paths(await connection.ScriptsSearchAsync("farm/", null, ct)));
        Assert.Equal(["Farm/Gold.cs"], Paths(await connection.ScriptsSearchAsync("", "GOLD", ct)));
        Assert.Equal(["Farm/Gold.cs"], Paths(await connection.ScriptsSearchAsync("farm", "gold", ct)));
        Assert.Equal(["Farm/Gold.cs"], Paths(await connection.ScriptsSearchAsync("farm gold", null, ct)));
        Assert.Empty(Paths(await connection.ScriptsSearchAsync("nothing-like-this", null, ct)));
    }

    [Fact]
    public async Task Search_returns_null_for_missing_names_and_tags_as_a_list()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, CoreBots);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);

        ScriptsSearchResult result = await connection.ScriptsSearchAsync("core", null, TestContext.Current.CancellationToken);
        ScriptDto leveling = (await connection.ScriptsSearchAsync("leveling", null, TestContext.Current.CancellationToken)).Scripts.Single();

        ScriptDto core = Assert.Single(result.Scripts);
        Assert.Equal(new ScriptSourceDto("auqw", "Scripts", "Skua"), result.Source);
        Assert.Null(core.Name);
        Assert.Null(core.Description);
        Assert.Empty(core.Tags);
        Assert.Equal("Leveling", leveling.Name);
        Assert.Equal(["farm", "xp"], leveling.Tags);
    }

    [Fact]
    public async Task Search_reports_whether_each_Script_is_downloaded_or_outdated()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, Gold);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);
        CancellationToken ct = TestContext.Current.CancellationToken;

        ScriptDto before = (await connection.ScriptsSearchAsync("leveling", null, ct)).Scripts.Single();
        await connection.ScriptsUpdateAsync(ct);
        ScriptDto synced = (await connection.ScriptsSearchAsync("leveling", null, ct)).Scripts.Single();
        github.Commit("auqw", "Scripts", "Skua", Leveling with { Content = "// leveling v2" });
        ScriptDto upstreamChanged = (await connection.ScriptsSearchAsync("leveling", null, ct)).Scripts.Single();

        Assert.Equal((false, false), (before.Downloaded, before.Outdated));
        Assert.Equal((true, false), (synced.Downloaded, synced.Outdated));
        Assert.Equal((true, true), (upstreamChanged.Downloaded, upstreamChanged.Outdated));
    }

    [Fact]
    public async Task Search_caps_its_Scripts_and_reports_how_many_matched()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        FakeScript[] many = Enumerable.Range(0, ScriptsSearchResult.MaxScripts + 5).Select(i => new FakeScript($"Many/Script{i:000}.cs", $"// {i}")).ToArray();
        github.Commit("auqw", "Scripts", "Skua", many);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);

        ScriptsSearchResult result = await connection.ScriptsSearchAsync("many", null, TestContext.Current.CancellationToken);

        Assert.Equal(ScriptsSearchResult.MaxScripts + 5, result.Matched);
        Assert.Equal(ScriptsSearchResult.MaxScripts, result.Scripts.Count);
    }

    [Fact]
    public async Task A_second_update_while_one_runs_is_refused_as_busy()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new() { RawDelay = TimeSpan.FromMilliseconds(500) };
        github.Commit("auqw", "Scripts", "Skua", Leveling);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);

        Task<ScriptsUpdateResult> first = connection.ScriptsUpdateAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        ControlException busy = await Assert.ThrowsAsync<ControlException>(() => connection.ScriptsUpdateAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.Busy, busy.Code);
        Assert.Equal(ScriptsUpdateMode.Full, (await first).Mode);
    }

    [Fact]
    public async Task An_unreachable_Script_Source_fails_with_its_code()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        SetScriptSource(sandbox, "nobody", "Nothing", "main");
        using EngineConnection connection = await StartEngineAsync(sandbox, github);

        ControlException update = await Assert.ThrowsAsync<ControlException>(() => connection.ScriptsUpdateAsync(TestContext.Current.CancellationToken));
        ControlException search = await Assert.ThrowsAsync<ControlException>(() => connection.ScriptsSearchAsync("x", null, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.ScriptSourceUnavailable, update.Code);
        Assert.Contains("nobody/Nothing@main", update.Message);
        Assert.Equal(ErrorCode.ScriptSourceUnavailable, search.Code);
    }

    [Fact]
    public async Task The_CLI_runs_scripts_update_and_scripts_search()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, Gold);

        ProcessResult update = await sandbox.RunCliAsync(github.Environment(), "scripts", "update", "--json");
        ProcessResult search = await sandbox.RunCliAsync(github.Environment(), "scripts", "search", "farm", "--tag", "xp", "--json");
        ProcessResult human = await sandbox.RunCliAsync(github.Environment(), "scripts", "search", "gold");

        Assert.True(update.ExitCode == 0, update.Stderr);
        using JsonDocument updateJson = JsonDocument.Parse(update.Stdout);
        Assert.Equal("full", updateJson.RootElement.GetProperty("mode").GetString());
        Assert.Equal(2, updateJson.RootElement.GetProperty("downloaded").GetInt32());
        using JsonDocument searchJson = JsonDocument.Parse(search.Stdout);
        JsonElement script = searchJson.RootElement.GetProperty("scripts").EnumerateArray().Single();
        Assert.Equal("Farm/Leveling.cs", script.GetProperty("path").GetString());
        Assert.True(script.GetProperty("downloaded").GetBoolean());
        Assert.Equal(0, human.ExitCode);
        Assert.Contains("Farm/Gold.cs", human.Stdout);
    }

    [Fact]
    public async Task The_CLI_exits_with_the_Script_Source_code_when_it_is_unreachable()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();

        ProcessResult result = await sandbox.RunCliAsync(github.Environment(), "scripts", "search", "x");

        Assert.Equal(ExitCodes.For(ErrorCode.ScriptSourceUnavailable), result.ExitCode);
    }

    [Fact]
    public async Task Skua_mcp_exposes_scripts_search_and_scripts_update()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, Gold);
        await using McpClient client = await McpTests.ConnectAsync(sandbox, github.Environment());
        CancellationToken ct = TestContext.Current.CancellationToken;

        CallToolResult update = await client.CallToolAsync("scripts_update", cancellationToken: ct);
        CallToolResult search = await client.CallToolAsync("scripts_search",
            new Dictionary<string, object?> { ["query"] = "gold" }, cancellationToken: ct);

        Assert.NotEqual(true, update.IsError);
        Assert.Equal("full", update.StructuredContent?.GetProperty("mode").GetString());
        Assert.NotEqual(true, search.IsError);
        JsonElement script = search.StructuredContent!.Value.GetProperty("scripts").EnumerateArray().Single();
        Assert.Equal("Farm/Gold.cs", script.GetProperty("path").GetString());
        Assert.True(script.GetProperty("downloaded").GetBoolean());
    }

    private static async Task<EngineConnection> StartEngineAsync(EngineSandbox sandbox, FakeGitHub github)
    {
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(github.Environment());
        connection.EnsureCompatible();
        return connection;
    }

    /// <summary>Writes the Script Source setting into the sandbox's settings file, as a developer would; the Engine reads it at start.</summary>
    private static void SetScriptSource(EngineSandbox sandbox, string owner, string repo, string branch) =>
        File.WriteAllText(Path.Combine(sandbox.SkuaDir, "Skua.settings.json"),
            JsonSerializer.Serialize(new { shared = new { ScriptSource = new { Owner = owner, Repo = repo, Branch = branch } } }));

    private static Task<string> ReadScriptAsync(EngineSandbox sandbox, string path) =>
        File.ReadAllTextAsync(Path.Combine(sandbox.SkuaDir, "Scripts", path), TestContext.Current.CancellationToken);

    private static string[] Paths(ScriptsSearchResult result) => result.Scripts.Select(s => s.Path).Order(StringComparer.Ordinal).ToArray();
}
