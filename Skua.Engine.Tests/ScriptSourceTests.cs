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

    private static readonly FakeScript LevelingWithCoreBots = new("Farm/Leveling.cs", """
        //cs_include Scripts/CoreBots.cs
        using Skua.Core.Interfaces;

        public class Leveling
        {
            public void ScriptMain(IScriptInterface bot) => new CoreBots().Ask();
        }
        """, "Leveling", "Levels you to 100.");

    /// <summary>Like upstream's CoreBots.cs: it uses Windows Forms, which macOS lacks.</summary>
    private static readonly FakeScript UpstreamCoreBots = new("CoreBots.cs", """
        using System.Windows.Forms;

        public class CoreBots
        {
            public void Ask() => MessageBox.Show("Continue?");
        }
        """);

    /// <summary>Like the fork's patched CoreBots.cs: the Windows Forms use is behind <c>#if !MACOS</c>.</summary>
    private static readonly FakeScript MacCoreBots = new("CoreBots.cs", """
        #if !MACOS
        using System.Windows.Forms;
        #endif

        public class CoreBots
        {
        #if !MACOS
            public void Ask() => MessageBox.Show("Continue?");
        #else
            public void Ask() { }
        #endif
        }
        """);

    private static readonly FakeScript Doomwood = new("Story/Doomwood.cs", "// story", "Doomwood Story", "Completes the Doomwood saga.");

    /// <summary>Older than the window a full download reads the Script Source's history over, so that history has nothing from it.</summary>
    private static DateTimeOffset MonthAgo => DateTimeOffset.UtcNow.AddDays(-30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_update_downloads_the_full_tree_from_the_default_Script_Source()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, Gold, CoreBots);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);

        ScriptsUpdateResult result = await connection.ScriptsUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ScriptSourceDto("noelrohi", "Scripts", "Skua"), result.Source);
        Assert.Equal(ScriptsUpdateMode.Full, result.Mode);
        Assert.Equal(3, result.Downloaded);
        Assert.Empty(result.Failed);
        Assert.Equal("// leveling v1", await ReadScriptAsync(sandbox, "Farm/Leveling.cs"));
        Assert.Equal("// core v1", await ReadScriptAsync(sandbox, "CoreBots.cs"));
    }

    [Fact]
    public async Task With_no_settings_the_Engine_syncs_the_Mac_ready_fork_so_Leveling_compiles()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", LevelingWithCoreBots, UpstreamCoreBots);
        github.Commit("noelrohi", "Scripts", "Skua", LevelingWithCoreBots, MacCoreBots);
        await using GameFixture game = await GameFixture.StartAsync(sandbox, environment: github.Environment());

        ScriptsUpdateResult update = await game.Connection.ScriptsUpdateAsync(Ct);
        ScriptOptionsResult options = await game.Connection.ScriptOptionsAsync("Farm/Leveling.cs", Ct);

        Assert.Equal(new ScriptSourceDto("noelrohi", "Scripts", "Skua"), update.Source);
        Assert.DoesNotContain(github.Requests, r => r.Contains("/auqw/", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(options.Options);
    }

    [Fact]
    public async Task An_explicit_Script_Source_setting_wins_over_the_default()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("auqw", "Scripts", "Skua", LevelingWithCoreBots, UpstreamCoreBots);
        github.Commit("noelrohi", "Scripts", "Skua", LevelingWithCoreBots, MacCoreBots);
        SetScriptSource(sandbox, "auqw", "Scripts", "Skua");
        await using GameFixture game = await GameFixture.StartAsync(sandbox, environment: github.Environment());

        ScriptsUpdateResult update = await game.Connection.ScriptsUpdateAsync(Ct);
        ControlException compile = await Assert.ThrowsAsync<ControlException>(() => game.Connection.ScriptOptionsAsync("Farm/Leveling.cs", Ct));

        Assert.Equal(new ScriptSourceDto("auqw", "Scripts", "Skua"), update.Source);
        Assert.DoesNotContain(github.Requests, r => r.Contains("/noelrohi/", StringComparison.OrdinalIgnoreCase));
        // The fake stands in for upstream's CoreBots, whose Windows Forms don't compile on macOS.
        Assert.Equal(ErrorCode.CompileFailed, compile.Code);
        Assert.Contains(compile.Diagnostics!, d => d.Contains("System.Windows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Later_updates_download_only_the_changed_Scripts()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, Gold, CoreBots);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);
        await connection.ScriptsUpdateAsync(TestContext.Current.CancellationToken);

        github.Commit("noelrohi", "Scripts", "Skua", Gold with { Content = "// gold v2" });
        github.ClearRequests();
        ScriptsUpdateResult incremental = await connection.ScriptsUpdateAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<string> downloaded = github.ScriptDownloads("noelrohi", "Scripts", "Skua");
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
    public async Task Changing_the_Script_Source_takes_effect_without_a_restart_and_makes_the_next_update_a_full_download_from_it()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, CoreBots with { Content = "// core fork" });
        github.Commit("auqw", "Scripts", "Skua", Leveling, CoreBots);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);
        await connection.ScriptsUpdateAsync(Ct);

        ScriptSourceResult set = await connection.ScriptsSourceSetAsync("auqw/Scripts@Skua", Ct);
        ScriptsUpdateResult upstream = await connection.ScriptsUpdateAsync(Ct);
        string upstreamCore = await ReadScriptAsync(sandbox, "CoreBots.cs");
        ScriptSourceResult reset = await connection.ScriptsSourceSetAsync(null, Ct);
        ScriptsUpdateResult fork = await connection.ScriptsUpdateAsync(Ct);

        Assert.Equal(new ScriptSourceResult(new("auqw", "Scripts", "Skua"), IsDefault: false, ScriptSourceSetting.Default), set);
        Assert.Equal((ScriptsUpdateMode.Full, new ScriptSourceDto("auqw", "Scripts", "Skua")), (upstream.Mode, upstream.Source));
        Assert.Equal("// core v1", upstreamCore);
        Assert.Equal(new ScriptSourceResult(ScriptSourceSetting.Default, IsDefault: true, ScriptSourceSetting.Default), reset);
        Assert.Equal((ScriptsUpdateMode.Full, ScriptSourceSetting.Default), (fork.Mode, fork.Source));
        Assert.Equal("// core fork", await ReadScriptAsync(sandbox, "CoreBots.cs"));
    }

    [Fact]
    public async Task A_headless_Engine_runs_none_of_the_Mac_Apps_start_up_checks()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("noelrohi", "Scripts", "Skua", Leveling)
            .Put("noelrohi", "Scripts", "Skua", "Skills/AdvancedSkills.json", "{}")
            .Put("noelrohi", "Scripts", "Skua", "JunkItems.json", "[]")
            .Put("noelrohi", "Scripts", "Skua", "QuestData.json", """[{"ID":1,"Name":"Not for a headless Engine"}]""");
        string settings = Path.Combine(sandbox.SkuaDir, "Skua.settings.json");
        File.WriteAllText(settings, """
            {"shared":{"CheckBotScriptsUpdates":true},
             "client":{"AutoUpdateBotScripts":true,"CheckAdvanceSkillSetsUpdates":true,"AutoUpdateAdvanceSkillSetsUpdates":true,
                       "CheckJunkItemsUpdates":true,"AutoUpdateJunkItems":true},
             "manager":{"ChangeLogActivated":false}}
            """);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);
        await connection.StatusAsync(Ct);

        // The app starts its checks once its window shows; give a headless Engine the same time to not start them.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        Assert.Empty(github.Requests);
        Assert.False(File.Exists(Path.Combine(sandbox.SkuaDir, "Scripts", "Farm", "Leveling.cs")));
        // Core creates an empty one at start.
        Assert.DoesNotContain("Not for a headless Engine", File.ReadAllText(Path.Combine(sandbox.SkuaDir, "QuestData.json")));
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.False(file.RootElement.GetProperty("manager").GetProperty("ChangeLogActivated").GetBoolean());
    }

    [Fact]
    public async Task The_CLI_shows_sets_and_resets_the_Script_Source_leaving_the_other_settings_alone()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        string settings = Path.Combine(sandbox.SkuaDir, "Skua.settings.json");
        File.WriteAllText(settings, """{"shared":{"CheckBotScriptsUpdates":false},"client":{"TestAccountService":"skua-account-main","AnimationFrameRate":24}}""");

        ProcessResult shown = await sandbox.RunCliAsync(github.Environment(), "scripts", "source");
        ProcessResult set = await sandbox.RunCliAsync(github.Environment(), "scripts", "source", "auqw/Scripts@Skua", "--json");
        ProcessResult shownSet = await sandbox.RunCliAsync(github.Environment(), "scripts", "source");
        string afterSet = File.ReadAllText(settings);
        ProcessResult reset = await sandbox.RunCliAsync(github.Environment(), "scripts", "source", "--default");
        ProcessResult shownReset = await sandbox.RunCliAsync(github.Environment(), "scripts", "source", "--json");
        ProcessResult invalid = await sandbox.RunCliAsync(github.Environment(), "scripts", "source", "not a source");
        ProcessResult both = await sandbox.RunCliAsync(github.Environment(), "scripts", "source", "auqw/Scripts@Skua", "--default");

        Assert.True(shown.ExitCode == 0, shown.Stderr);
        Assert.Equal("noelrohi/Scripts@Skua (the default)", shown.Stdout.Trim());
        Assert.True(set.ExitCode == 0, set.Stderr);
        using (JsonDocument json = JsonDocument.Parse(set.Stdout))
        {
            Assert.Equal("auqw", json.RootElement.GetProperty("source").GetProperty("owner").GetString());
            Assert.False(json.RootElement.GetProperty("isDefault").GetBoolean());
            Assert.Equal("noelrohi", json.RootElement.GetProperty("default").GetProperty("owner").GetString());
        }
        Assert.StartsWith("auqw/Scripts@Skua (set in Skua.settings.json; the default is noelrohi/Scripts@Skua", shownSet.Stdout);
        using (JsonDocument file = JsonDocument.Parse(afterSet))
        {
            Assert.Equal("auqw", file.RootElement.GetProperty("shared").GetProperty("ScriptSource").GetProperty("Owner").GetString());
            Assert.False(file.RootElement.GetProperty("shared").GetProperty("CheckBotScriptsUpdates").GetBoolean());
            Assert.Equal("skua-account-main", file.RootElement.GetProperty("client").GetProperty("TestAccountService").GetString());
            Assert.Equal(24, file.RootElement.GetProperty("client").GetProperty("AnimationFrameRate").GetInt32());
        }
        Assert.True(reset.ExitCode == 0, reset.Stderr);
        Assert.Contains("Now fetching Scripts from noelrohi/Scripts@Skua (the default)", reset.Stdout);
        using (JsonDocument json = JsonDocument.Parse(shownReset.Stdout))
            Assert.True(json.RootElement.GetProperty("isDefault").GetBoolean());
        using (JsonDocument file = JsonDocument.Parse(File.ReadAllText(settings)))
        {
            Assert.False(file.RootElement.GetProperty("shared").TryGetProperty("ScriptSource", out _));
            Assert.False(file.RootElement.GetProperty("shared").GetProperty("CheckBotScriptsUpdates").GetBoolean());
            Assert.Equal("skua-account-main", file.RootElement.GetProperty("client").GetProperty("TestAccountService").GetString());
            Assert.Equal(24, file.RootElement.GetProperty("client").GetProperty("AnimationFrameRate").GetInt32());
        }
        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), invalid.ExitCode);
        Assert.Contains("owner/repo@branch", invalid.Stderr);
        Assert.NotEqual(0, both.ExitCode);
    }

    [Fact]
    public async Task Changing_the_Script_Source_is_refused_while_a_Script_runs()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);
        await game.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);

        ControlException refused = await Assert.ThrowsAsync<ControlException>(() => game.Connection.ScriptsSourceSetAsync("auqw/Scripts@Skua", Ct));
        ScriptSourceResult source = await game.Connection.ScriptsSourceAsync(Ct);
        await game.Connection.ScriptStopAsync(Ct);

        Assert.Equal(ErrorCode.ScriptRunning, refused.Code);
        Assert.True(source.IsDefault);
    }

    [Fact]
    public async Task Search_finds_Scripts_by_name_description_tag_and_path()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, Gold, CoreBots, new FakeScript("Story/Doomwood.cs", "// story", "Doomwood Story", "Completes the Doomwood saga.", "story"));
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
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, CoreBots);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);

        ScriptsSearchResult result = await connection.ScriptsSearchAsync("core", null, TestContext.Current.CancellationToken);
        ScriptDto leveling = (await connection.ScriptsSearchAsync("leveling", null, TestContext.Current.CancellationToken)).Scripts.Single();

        ScriptDto core = Assert.Single(result.Scripts);
        Assert.Equal(new ScriptSourceDto("noelrohi", "Scripts", "Skua"), result.Source);
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
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, Gold);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);
        CancellationToken ct = TestContext.Current.CancellationToken;

        ScriptDto before = (await connection.ScriptsSearchAsync("leveling", null, ct)).Scripts.Single();
        await connection.ScriptsUpdateAsync(ct);
        ScriptDto synced = (await connection.ScriptsSearchAsync("leveling", null, ct)).Scripts.Single();
        github.Commit("noelrohi", "Scripts", "Skua", Leveling with { Content = "// leveling v2" });
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
        github.Commit("noelrohi", "Scripts", "Skua", many);
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
        github.Commit("noelrohi", "Scripts", "Skua", Leveling);
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
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, Gold);

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
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, Gold);
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
    public async Task Skua_mcp_shows_the_Script_Source_read_only_and_cannot_change_it()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        await using McpClient client = await McpTests.ConnectAsync(sandbox, github.Environment());
        CancellationToken ct = TestContext.Current.CancellationToken;

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: ct);
        CallToolResult source = await client.CallToolAsync("scripts_source", cancellationToken: ct);

        Assert.True(tools.Single(t => t.Name == "scripts_source").ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.DoesNotContain(tools, t => t.Name.StartsWith("scripts_source_", StringComparison.Ordinal));
        Assert.NotEqual(true, source.IsError);
        Assert.Equal("noelrohi", source.StructuredContent!.Value.GetProperty("source").GetProperty("owner").GetString());
        Assert.True(source.StructuredContent!.Value.GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task An_update_reports_the_Scripts_it_added_and_changed_and_scripts_new_lists_exactly_those()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.CommitAt(MonthAgo, "noelrohi", "Scripts", "Skua", [Leveling, Gold, CoreBots]);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);
        CancellationToken ct = TestContext.Current.CancellationToken;
        ScriptsUpdateResult full = await connection.ScriptsUpdateAsync(ct);
        ScriptsNewResult afterFull = await connection.ScriptsNewAsync(null, ct);

        github.Commit("noelrohi", "Scripts", "Skua", Gold with { Content = "// gold v2" }, Doomwood);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        ScriptsUpdateResult incremental = await connection.ScriptsUpdateAsync(ct);
        ScriptsNewResult news = await connection.ScriptsNewAsync(null, ct);
        ScriptsNewResult sinceFull = await connection.ScriptsNewAsync(full.Commit[..7], ct);
        ScriptsNewResult sinceLatest = await connection.ScriptsNewAsync(incremental.Commit, ct);
        // The Engine reads a date as local time, as the user who typed it means it.
        ScriptsNewResult sinceTomorrow = await connection.ScriptsNewAsync(DateTime.Now.AddDays(1).ToString("yyyy-MM-dd"), ct);
        ControlException unknown = await Assert.ThrowsAsync<ControlException>(() => connection.ScriptsNewAsync("not-a-date-or-commit", ct));

        Assert.Equal((3, 0), (full.Added.Count, full.Changed.Count));
        // A full download is the starting point, not news, and the Script Source's history had nothing in the window.
        Assert.Empty(afterFull.Scripts);
        Assert.Equal(0, afterFull.Commits);
        Assert.True(afterFull.HistoryFrom <= afterFull.Since.AddMinutes(1), $"{afterFull.HistoryFrom} is well after {afterFull.Since}");
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
        github.CommitAt(MonthAgo, "noelrohi", "Scripts", "Skua", [Leveling, Gold]);
        await sandbox.RunCliAsync(github.Environment(), "scripts", "update");
        github.Commit("noelrohi", "Scripts", "Skua", Leveling with { Content = "// leveling v2" }, CoreBots);

        ProcessResult update = await sandbox.RunCliAsync(github.Environment(), "scripts", "update");
        ProcessResult news = await sandbox.RunCliAsync(github.Environment(), "scripts", "new");
        ProcessResult json = await sandbox.RunCliAsync(github.Environment(), "scripts", "new", "--json");
        ProcessResult none = await sandbox.RunCliAsync(github.Environment(), "scripts", "new", "--since", "2999-01-01");

        Assert.True(update.ExitCode == 0, update.Stderr);
        Assert.Contains("1 new, 1 changed; see 'skua scripts new'", update.Stdout);
        Assert.True(news.ExitCode == 0, news.Stderr);
        Assert.StartsWith("1 new, 1 changed from noelrohi/Scripts@Skua since ", news.Stdout);
        Assert.Contains(", by 1 update:", news.Stdout);
        Assert.Matches(@"CoreBots\.cs\s+new\s+", news.Stdout);
        Assert.Matches(@"Farm/Leveling\.cs\s+changed\s+", news.Stdout);
        using JsonDocument newsJson = JsonDocument.Parse(json.Stdout);
        Assert.Equal(["added", "changed"], newsJson.RootElement.GetProperty("scripts").EnumerateArray().Select(s => s.GetProperty("change").GetString()));
        Assert.Equal(0, none.ExitCode);
        Assert.Contains("No Scripts were added or changed", none.Stdout);
    }

    [Fact]
    public async Task A_first_full_download_records_the_Script_Sources_recent_commits_and_scripts_new_lists_exactly_those()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        DateTimeOffset fiveDaysAgo = Second(DateTimeOffset.UtcNow.AddDays(-5));
        DateTimeOffset threeDaysAgo = Second(DateTimeOffset.UtcNow.AddDays(-3));
        DateTimeOffset twoDaysAgo = Second(DateTimeOffset.UtcNow.AddDays(-2));
        DateTimeOffset dayAgo = Second(DateTimeOffset.UtcNow.AddDays(-1));
        github.CommitAt(MonthAgo, "noelrohi", "Scripts", "Skua", [Leveling, Gold, CoreBots]);
        github.CommitAt(fiveDaysAgo, "noelrohi", "Scripts", "Skua", [new FakeScript("Story/Old.cs", "// old")]);
        github.CommitAt(threeDaysAgo, "noelrohi", "Scripts", "Skua", [Gold with { Content = "// gold v2" }, Doomwood]);
        string threeDaysAgoSha = github.Head("noelrohi", "Scripts", "Skua");
        // A Script deleted since isn't news; nor is a file that isn't a Script.
        github.CommitAt(twoDaysAgo, "noelrohi", "Scripts", "Skua", [new FakeScript("README.md", "# Scripts")], "Story/Old.cs");
        github.CommitAt(dayAgo, "noelrohi", "Scripts", "Skua", [Leveling with { Content = "// leveling v2" }]);
        string dayAgoSha = github.Head("noelrohi", "Scripts", "Skua");

        ProcessResult update = await sandbox.RunCliAsync(github.Environment(), "scripts", "update");
        IReadOnlyList<string> api = github.Requests.Where(r => r.StartsWith("/api/", StringComparison.Ordinal)).ToList();
        ProcessResult news = await sandbox.RunCliAsync(github.Environment(), "scripts", "new");
        ProcessResult json = await sandbox.RunCliAsync(github.Environment(), "scripts", "new", "--json");
        ProcessResult sinceCommit = await sandbox.RunCliAsync(github.Environment(), "scripts", "new", "--since", threeDaysAgoSha[..7], "--json");

        Assert.True(update.ExitCode == 0, update.Stderr);
        Assert.Contains("(full download)", update.Stdout);
        // The head, then the Script Source's history: the list of commits and each of the four in the window, two of which touch Scripts still there.
        Assert.Equal(1 + 1 + 4, api.Count);
        Assert.True(news.ExitCode == 0, news.Stderr);
        Assert.StartsWith("1 new, 2 changed from noelrohi/Scripts@Skua since ", news.Stdout);
        Assert.Contains(", by 2 commits from its history:", news.Stdout);
        Assert.DoesNotContain("No history", news.Stdout);
        ScriptsNewResult result = JsonSerializer.Deserialize<ScriptsNewResult>(json.Stdout, ControlJson.Options)!;
        Assert.Equal(
            [
                ("Farm/Leveling.cs", (string?)"Leveling", ScriptChange.Changed, dayAgo, dayAgoSha),
                ("Farm/Gold.cs", "Gold Farm", ScriptChange.Changed, threeDaysAgo, threeDaysAgoSha),
                ("Story/Doomwood.cs", "Doomwood Story", ScriptChange.Added, threeDaysAgo, threeDaysAgoSha),
            ],
            result.Scripts.Select(s => (s.Path, s.Name, s.Change, s.At, s.Commit)));
        Assert.Equal((0, 2), (result.Updates, result.Commits));
        Assert.True(result.HistoryFrom <= result.Since.AddMinutes(1), $"{result.HistoryFrom} is well after {result.Since}");
        Assert.Equal(["Farm/Leveling.cs"], JsonSerializer.Deserialize<ScriptsNewResult>(sinceCommit.Stdout, ControlJson.Options)!.Scripts.Select(s => s.Path));
    }

    [Fact]
    public async Task A_full_download_reads_at_most_21_API_requests_of_history_and_scripts_new_says_where_it_starts()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.CommitAt(MonthAgo, "noelrohi", "Scripts", "Skua", [Leveling]);
        DateTimeOffset start = Second(DateTimeOffset.UtcNow.AddDays(-6));
        for (int i = 0; i < 25; i++)
            github.CommitAt(start.AddHours(i), "noelrohi", "Scripts", "Skua", [new FakeScript($"Farm/Farm{i:D2}.cs", $"// farm {i}")]);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);

        ScriptsUpdateResult update = await connection.ScriptsUpdateAsync(Ct);
        int history = github.Requests.Count(r => r.StartsWith("/api/", StringComparison.Ordinal) && !r.EndsWith("/commits/Skua", StringComparison.Ordinal));
        ScriptsNewResult news = await connection.ScriptsNewAsync(null, Ct);
        ProcessResult text = await sandbox.RunCliAsync(github.Environment(), "scripts", "new");

        Assert.Equal(26, update.Downloaded);
        Assert.Equal(21, history);
        // The 20 newest commits, whose history is complete back to the oldest of them.
        Assert.Equal(Enumerable.Range(5, 20).Reverse().Select(i => $"Farm/Farm{i:D2}.cs"), news.Scripts.Select(s => s.Path));
        Assert.Equal(20, news.Commits);
        Assert.Equal(start.AddHours(5), news.HistoryFrom);
        Assert.EndsWith($"No history of noelrohi/Scripts@Skua yet from before {start.AddHours(5).ToLocalTime():yyyy-MM-dd HH:mm}.", text.Stdout.Trim());
    }

    [Fact]
    public async Task With_the_commits_API_failing_the_full_download_succeeds_and_scripts_new_says_there_is_no_history_yet()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.CommitAt(DateTimeOffset.UtcNow.AddDays(-2), "noelrohi", "Scripts", "Skua", [Leveling, Gold]);
        github.HistoryDown = true;

        ProcessResult neverUpdated = await sandbox.RunCliAsync(github.Environment(), "scripts", "new");
        DateTimeOffset before = DateTimeOffset.UtcNow;
        ProcessResult update = await sandbox.RunCliAsync(github.Environment(), "scripts", "update", "--json");
        IReadOnlyList<string> api = github.Requests.Where(r => r.StartsWith("/api/", StringComparison.Ordinal)).ToList();
        ProcessResult news = await sandbox.RunCliAsync(github.Environment(), "scripts", "new");
        ProcessResult json = await sandbox.RunCliAsync(github.Environment(), "scripts", "new", "--json");

        Assert.Equal((0, "No history of noelrohi/Scripts@Skua yet; 'skua scripts update' starts it."), (neverUpdated.ExitCode, neverUpdated.Stdout.Trim()));
        Assert.True(update.ExitCode == 0, update.Stderr);
        Assert.Equal(2, JsonDocument.Parse(update.Stdout).RootElement.GetProperty("downloaded").GetInt32());
        // The head, then one refused list of commits, not retried.
        Assert.Equal(["/api/repos/noelrohi/Scripts/commits/Skua", "/api/repos/noelrohi/Scripts/commits"], api);
        Assert.True(news.ExitCode == 0, news.Stderr);
        Assert.StartsWith("No history of noelrohi/Scripts@Skua yet from before ", news.Stdout);
        Assert.EndsWith("; no Scripts were added or changed after.", news.Stdout.Trim());
        ScriptsNewResult result = JsonSerializer.Deserialize<ScriptsNewResult>(json.Stdout, ControlJson.Options)!;
        Assert.Empty(result.Scripts);
        Assert.InRange(result.HistoryFrom!.Value, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_full_download_retried_after_failures_reads_only_the_commits_since_the_last_read()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.CommitAt(DateTimeOffset.UtcNow.AddDays(-2), "noelrohi", "Scripts", "Skua", [Leveling, Gold]);
        using EngineConnection connection = await StartEngineAsync(sandbox, github);
        ScriptsUpdateResult first = await connection.ScriptsUpdateAsync(Ct);
        // A Script that fails to download leaves the commit unrecorded, so the next update is a full download again.
        github.Commit("noelrohi", "Scripts", "Skua", Doomwood);
        File.Delete(Path.Combine(sandbox.SkuaDir, "scripts-commit.txt"));
        github.ClearRequests();

        ScriptsUpdateResult retried = await connection.ScriptsUpdateAsync(Ct);
        int history = github.Requests.Count(r => r.StartsWith("/api/", StringComparison.Ordinal) && !r.EndsWith("/commits/Skua", StringComparison.Ordinal));
        ScriptsNewResult news = await connection.ScriptsNewAsync(null, Ct);

        Assert.Equal((ScriptsUpdateMode.Full, ScriptsUpdateMode.Full), (first.Mode, retried.Mode));
        // The list, and only the one commit made since the first read.
        Assert.Equal(2, history);
        Assert.Equal([("Story/Doomwood.cs", ScriptChange.Added), ("Farm/Gold.cs", ScriptChange.Added), ("Farm/Leveling.cs", ScriptChange.Added)],
            news.Scripts.Select(s => (s.Path, s.Change)));
        Assert.Equal(2, news.Commits);
    }

    private static DateTimeOffset Second(DateTimeOffset time) => DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds());

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
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, Gold, voidScript, CoreBots);
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
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, Gold, new FakeScript("Farm/Special/Void.cs", "// void"), CoreBots);

        ProcessResult farm = await sandbox.RunCliAsync(github.Environment(), "scripts", "list", "Farm");
        ProcessResult json = await sandbox.RunCliAsync(github.Environment(), "scripts", "list", "--json");

        Assert.True(farm.ExitCode == 0, farm.Stderr);
        Assert.Equal(
            """
            Farm/ in noelrohi/Scripts@Skua: 2 Scripts, 1 folder
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
        github.Commit("noelrohi", "Scripts", "Skua", Leveling, Gold);
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
        github.Commit("noelrohi", "Scripts", "Skua", Hello("v1"), Leveling);

        ProcessResult first = await sandbox.RunCliAsync(environment, "script", "start", "Tests/Hello.cs", "--follow");
        github.Commit("noelrohi", "Scripts", "Skua", Hello("v2"));
        github.ClearRequests();
        ProcessResult changed = await sandbox.RunCliAsync(environment, "script", "start", "Tests/Hello.cs", "--follow");
        IReadOnlyList<string> requests = github.Requests;
        ProcessResult upToDate = await sandbox.RunCliAsync(environment, "script", "start", "Tests/Hello.cs", "--follow");
        github.Commit("noelrohi", "Scripts", "Skua", Hello("v3"));
        ProcessResult noUpdate = await sandbox.RunCliAsync(environment, "script", "start", "Tests/Hello.cs", "--follow", "--no-update");
        github.Down = true;
        ProcessResult offline = await sandbox.RunCliAsync(environment, "script", "start", "Tests/Hello.cs", "--follow");

        Assert.True(first.ExitCode == 0, first.Stderr);
        Assert.Contains("Downloaded 2 Scripts from noelrohi/Scripts@Skua", first.Stdout);
        Assert.Contains("hello v1", first.Stdout);
        Assert.True(changed.ExitCode == 0, changed.Stderr);
        Assert.Contains("Updated the Scripts from noelrohi/Scripts@Skua: 0 new, 1 changed; see 'skua scripts new'.", changed.Stdout);
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
        github.Commit("noelrohi", "Scripts", "Skua", Hello("v1"));

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
