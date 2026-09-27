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

    [Fact]
    public async Task An_update_reports_the_Scripts_it_added_and_changed_and_scripts_new_lists_exactly_those()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, Gold, CoreBots);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);
        CancellationToken ct = TestContext.Current.CancellationToken;
        ScriptsUpdateResult full = await connection.ScriptsUpdateAsync(ct);
        ScriptsNewResult afterFull = await connection.ScriptsNewAsync(null, ct);

        github.Commit("auqw", "Scripts", "Skua", Gold with { Content = "// gold v2" }, new FakeScript("Story/Doomwood.cs", "// story", "Doomwood Story", "Completes the Doomwood saga."));
        DateTimeOffset before = DateTimeOffset.UtcNow;
        ScriptsUpdateResult incremental = await connection.ScriptsUpdateAsync(ct);
        ScriptsNewResult news = await connection.ScriptsNewAsync(null, ct);
        ScriptsNewResult sinceFull = await connection.ScriptsNewAsync(full.Commit[..7], ct);
        ScriptsNewResult sinceLatest = await connection.ScriptsNewAsync(incremental.Commit, ct);
        // The Engine reads a date as local time, as the user who typed it means it.
        ScriptsNewResult sinceTomorrow = await connection.ScriptsNewAsync(DateTime.Now.AddDays(1).ToString("yyyy-MM-dd"), ct);
        ControlException unknown = await Assert.ThrowsAsync<ControlException>(() => connection.ScriptsNewAsync("not-a-date-or-commit", ct));

        Assert.Equal((3, 0), (full.Added.Count, full.Changed.Count));
        // A full download is the starting point, not news.
        Assert.Empty(afterFull.Scripts);
        Assert.Equal(["Story/Doomwood.cs"], incremental.Added);
        Assert.Equal(["Farm/Gold.cs"], incremental.Changed);
        Assert.Equal(
            [("Farm/Gold.cs", (string?)"Gold Farm", ScriptChange.Changed, incremental.Commit), ("Story/Doomwood.cs", "Doomwood Story", ScriptChange.Added, incremental.Commit)],
            news.Scripts.Select(s => (s.Path, s.Name, s.Change, s.Commit)));
        Assert.All(news.Scripts, s => Assert.InRange(s.At, before, DateTimeOffset.UtcNow));
        Assert.Equal(news.Scripts, sinceFull.Scripts);
        Assert.Empty(sinceLatest.Scripts);
        Assert.Empty(sinceTomorrow.Scripts);
        Assert.Equal(ErrorCode.InvalidArgument, unknown.Code);
        Assert.True(File.Exists(Path.Combine(sandbox.SkuaDir, "scripts-history.json")));
    }

    [Fact]
    public async Task The_CLI_update_prints_the_counts_and_scripts_new_lists_them()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, Gold);
        await sandbox.RunCliAsync(github.Environment(), "scripts", "update");
        github.Commit("auqw", "Scripts", "Skua", Leveling with { Content = "// leveling v2" }, CoreBots);

        ProcessResult update = await sandbox.RunCliAsync(github.Environment(), "scripts", "update");
        ProcessResult news = await sandbox.RunCliAsync(github.Environment(), "scripts", "new");
        ProcessResult json = await sandbox.RunCliAsync(github.Environment(), "scripts", "new", "--json");
        ProcessResult none = await sandbox.RunCliAsync(github.Environment(), "scripts", "new", "--since", "2999-01-01");

        Assert.True(update.ExitCode == 0, update.Stderr);
        Assert.Contains("1 new, 1 changed; see 'skua scripts new'", update.Stdout);
        Assert.True(news.ExitCode == 0, news.Stderr);
        Assert.Matches(@"CoreBots\.cs\s+new\s+", news.Stdout);
        Assert.Matches(@"Farm/Leveling\.cs\s+changed\s+", news.Stdout);
        using JsonDocument newsJson = JsonDocument.Parse(json.Stdout);
        Assert.Equal(["added", "changed"], newsJson.RootElement.GetProperty("scripts").EnumerateArray().Select(s => s.GetProperty("change").GetString()));
        Assert.Equal(0, none.ExitCode);
        Assert.Contains("No Scripts were added or changed", none.Stdout);
    }

    [Theory]
    [InlineData("Pacific/Kiritimati", "2026-08-31T10:00:00Z")]
    [InlineData("Pacific/Pago_Pago", "2026-09-01T11:00:00Z")]
    public async Task Scripts_new_since_a_date_starts_at_local_midnight_in_the_time_zone_the_CLI_runs_in(string timeZone, string midnight)
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        Dictionary<string, string> environment = new(github.Environment()) { ["TZ"] = timeZone };

        ProcessResult date = await sandbox.RunCliAsync(environment, "scripts", "new", "--since", "2026-09-01", "--json");
        ProcessResult utc = await sandbox.RunCliAsync(environment, "scripts", "new", "--since", "2026-09-01T00:00Z", "--json");

        Assert.True(date.ExitCode == 0, date.Stderr);
        Assert.True(utc.ExitCode == 0, utc.Stderr);
        Assert.Equal(DateTimeOffset.Parse(midnight), Since(date));
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T00:00:00Z"), Since(utc));

        static DateTimeOffset Since(ProcessResult result)
        {
            using JsonDocument json = JsonDocument.Parse(result.Stdout);
            return json.RootElement.GetProperty("since").GetDateTimeOffset();
        }
    }

    [Fact]
    public async Task Scripts_list_browses_a_folder_of_the_Script_Source_with_its_subfolders_and_descriptions()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        FakeScript voidScript = new("Farm/Special/Void.cs", "// void", "Void", "Farms the Void.");
        github.Commit("auqw", "Scripts", "Skua", Leveling, Gold, voidScript, CoreBots);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);
        CancellationToken ct = TestContext.Current.CancellationToken;

        ScriptsListResult farm = await connection.ScriptsListAsync("farm/", ct);
        ScriptsListResult root = await connection.ScriptsListAsync(null, ct);
        ControlException missing = await Assert.ThrowsAsync<ControlException>(() => connection.ScriptsListAsync("Nope", ct));

        Assert.Equal("Farm", farm.Folder);
        Assert.Equal([new ScriptFolderDto("Farm/Special", 1)], farm.Folders);
        Assert.Equal(["Farm/Gold.cs", "Farm/Leveling.cs"], farm.Scripts.Select(s => s.Path));
        Assert.Equal("Farms gold.", farm.Scripts[0].Description);
        Assert.Equal("", root.Folder);
        Assert.Equal([new ScriptFolderDto("Farm", 3)], root.Folders);
        Assert.Equal(["CoreBots.cs"], root.Scripts.Select(s => s.Path));
        Assert.Equal(ErrorCode.InvalidArgument, missing.Code);
        Assert.Contains("'Nope'", missing.Message);
    }

    [Fact]
    public async Task The_CLI_lists_a_folder_as_a_tree()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, Gold, new FakeScript("Farm/Special/Void.cs", "// void"), CoreBots);

        ProcessResult farm = await sandbox.RunCliAsync(github.Environment(), "scripts", "list", "Farm");
        ProcessResult json = await sandbox.RunCliAsync(github.Environment(), "scripts", "list", "--json");

        Assert.True(farm.ExitCode == 0, farm.Stderr);
        Assert.Equal(
            """
            Farm/ in auqw/Scripts@Skua: 2 Scripts, 1 folder
            ├── Special/  1 Script
            ├── Gold.cs  Farms gold.
            └── Leveling.cs  Levels you to 100.
            """,
            farm.Stdout.TrimEnd().ReplaceLineEndings("\n"));
        using JsonDocument root = JsonDocument.Parse(json.Stdout);
        Assert.Equal("Farm", root.RootElement.GetProperty("folders").EnumerateArray().Single().GetProperty("path").GetString());
        Assert.Equal("CoreBots.cs", root.RootElement.GetProperty("scripts").EnumerateArray().Single().GetProperty("path").GetString());
    }

    [Fact]
    public async Task Skua_mcp_exposes_scripts_list_and_scripts_new()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", Leveling, Gold);
        await using McpClient client = await McpTests.ConnectAsync(sandbox, github.Environment());
        CancellationToken ct = TestContext.Current.CancellationToken;

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: ct);
        CallToolResult list = await client.CallToolAsync("scripts_list", new Dictionary<string, object?> { ["folder"] = "Farm" }, cancellationToken: ct);
        CallToolResult news = await client.CallToolAsync("scripts_new", cancellationToken: ct);

        Assert.Equal(2, tools.Count(t => t.Name is "scripts_list" or "scripts_new" && t.ProtocolTool.Annotations?.ReadOnlyHint == true));
        Assert.NotEqual(true, list.IsError);
        Assert.Equal(2, list.StructuredContent!.Value.GetProperty("scripts").GetArrayLength());
        Assert.NotEqual(true, news.IsError);
        Assert.Equal(0, news.StructuredContent!.Value.GetProperty("scripts").GetArrayLength());
    }

    [Fact]
    public async Task Script_start_updates_the_Scripts_first_unless_told_not_to_and_starts_the_local_copy_when_offline()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        await using FakeGitHub github = new();
        Dictionary<string, string> environment = GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain);
        foreach ((string key, string value) in github.Environment())
            environment[key] = value;
        github.Commit("auqw", "Scripts", "Skua", Hello("v1"), Leveling);

        ProcessResult first = await sandbox.RunCliAsync(environment, "script", "start", "Tests/Hello.cs", "--follow");
        github.Commit("auqw", "Scripts", "Skua", Hello("v2"));
        github.ClearRequests();
        ProcessResult changed = await sandbox.RunCliAsync(environment, "script", "start", "Tests/Hello.cs", "--follow");
        IReadOnlyList<string> requests = github.Requests;
        ProcessResult upToDate = await sandbox.RunCliAsync(environment, "script", "start", "Tests/Hello.cs", "--follow");
        github.Commit("auqw", "Scripts", "Skua", Hello("v3"));
        ProcessResult noUpdate = await sandbox.RunCliAsync(environment, "script", "start", "Tests/Hello.cs", "--follow", "--no-update");
        github.Down = true;
        ProcessResult offline = await sandbox.RunCliAsync(environment, "script", "start", "Tests/Hello.cs", "--follow");

        Assert.True(first.ExitCode == 0, first.Stderr);
        Assert.Contains("Downloaded 2 Scripts from auqw/Scripts@Skua", first.Stdout);
        Assert.Contains("hello v1", first.Stdout);
        Assert.True(changed.ExitCode == 0, changed.Stderr);
        Assert.Contains("Updated the Scripts from auqw/Scripts@Skua: 0 new, 1 changed; see 'skua scripts new'.", changed.Stdout);
        Assert.Contains("hello v2", changed.Stdout);
        // One head check, the compare, then only the changed Script.
        Assert.Equal(1, requests.Count(r => r.EndsWith("/commits/Skua", StringComparison.Ordinal)));
        Assert.Equal(["Tests/Hello.cs"], requests.Where(r => r.EndsWith(".cs", StringComparison.Ordinal)).Select(r => r.Split("/Skua/")[^1]));
        Assert.DoesNotContain("Updated", upToDate.Stdout);
        Assert.DoesNotContain("Downloaded", upToDate.Stdout);
        Assert.Contains("hello v2", noUpdate.Stdout);
        Assert.Equal(0, offline.ExitCode);
        Assert.Contains("hello v2", offline.Stdout);
        Assert.Contains("skua: couldn't update the Scripts", offline.Stderr);
        Assert.Contains("starting the local copy", offline.Stderr);
    }

    [Fact]
    public async Task Script_start_json_keeps_stdout_the_start_result_and_reports_the_update_on_stderr()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        await using FakeGitHub github = new();
        FakeKeychain keychain = new(sandbox);
        Dictionary<string, string> environment = GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain);
        foreach ((string key, string value) in github.Environment())
            environment[key] = value;
        github.Commit("auqw", "Scripts", "Skua", Hello("v1"));

        ProcessResult start = await sandbox.RunCliAsync(environment, "script", "start", "Tests/Hello.cs", "--json");

        Assert.True(start.ExitCode == 0, start.Stderr);
        using JsonDocument json = JsonDocument.Parse(start.Stdout);
        Assert.Equal(1, json.RootElement.GetProperty("run").GetInt32());
        Assert.Contains("Downloaded 1 Scripts", start.Stderr);
    }

    private static FakeScript Hello(string version) => new("Tests/Hello.cs", TestScripts.Main($"bot.Log(\"hello {version}\");"), "Hello", "Says hello.");

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
