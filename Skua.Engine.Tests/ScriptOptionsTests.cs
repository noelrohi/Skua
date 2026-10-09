using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary><c>script_options</c> and the options <c>script_start</c> stores, against the fake Game Host.</summary>
public class ScriptOptionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A Script with options in the main list and in a group, which logs the values it runs with.</summary>
    private static readonly string Farm = TestScripts.Main(
        """
        bot.Log($"count={bot.Config.Get<int>("count")} mode={bot.Config.Get<Mode>("mode")} flag={bot.Config.Get<bool>("flag")} name={bot.Config.Get<string>("Extra", "name")}");
        """,
        """
        public enum Mode { Fast_Farm, Slow }

        public string OptionsStorage = "TestFarm";

        public List<IOption> Options = new()
        {
            new Option<int>("count", "Count", "How many to farm.", 5),
            new Option<Mode>("mode", "Mode", "", Mode.Fast_Farm),
            new Option<bool>("flag", "Flag", "A switch.", false),
            new Option<int>("once", "Once", "Resets every start.", 1, transient: true),
        };

        public string[] MultiOptions = { "Extra" };

        public List<IOption> Extra = new()
        {
            new Option<string>("name", "Name", "Who.", "nobody"),
        };
        """);

    [Fact]
    public async Task Script_options_lists_each_option_with_its_key_type_value_default_and_choices()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Farm.cs", Farm);

        ScriptOptionsResult result = await game.Connection.ScriptOptionsAsync("Tests/Farm.cs", Ct);

        Assert.Equal(("Tests/Farm.cs", "TestFarm"), (result.Script, result.Storage));
        Assert.Equal(
            [
                new ScriptOptionDto("count", "Options", "count", "Count", "How many to farm.", "int", "5", "5", null, false),
                new ScriptOptionDto("mode", "Options", "mode", "Mode", null, "enum", "Fast Farm", "Fast Farm", ["Fast Farm", "Slow"], false),
                new ScriptOptionDto("flag", "Options", "flag", "Flag", "A switch.", "bool", "False", "False", null, false),
                new ScriptOptionDto("once", "Options", "once", "Once", "Resets every start.", "int", "1", "1", null, true),
                new ScriptOptionDto("Extra:name", "Extra", "name", "Name", "Who.", "string", "nobody", "nobody", null, false),
            ],
            result.Options.Select(o => o with { Choices = o.Choices?.ToList() }),
            new OptionComparer());
    }

    [Fact]
    public async Task Script_start_stores_the_given_options_and_the_Script_runs_with_them_and_the_stored_rest()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Farm.cs", Farm);
        await game.Connection.ScriptStartAsync("Tests/Farm.cs", new Dictionary<string, string> { ["flag"] = "true" }, cancellationToken: Ct);
        await game.Connection.ScriptWaitAsync(60, Ct);

        await game.Connection.ScriptStartAsync("Tests/Farm.cs",
            new Dictionary<string, string> { ["count"] = "7", ["mode"] = "slow", ["Extra:name"] = "Artix" }, cancellationToken: Ct);
        await game.Connection.ScriptWaitAsync(60, Ct);
        ScriptOptionsResult after = await game.Connection.ScriptOptionsAsync("Tests/Farm.cs", Ct);

        List<LogEntryDto> lines = await game.Connection.WaitForLogsAsync(LogKind.Script, 2, e => e.Text!.StartsWith("count=", StringComparison.Ordinal));
        Assert.Equal("count=5 mode=Fast_Farm flag=True name=nobody", lines[0].Text);
        Assert.Equal("count=7 mode=Slow flag=True name=Artix", lines[1].Text);
        Assert.Equal(["7", "Slow", "True", "1", "Artix"], after.Options.Select(o => o.Value));
        Assert.Contains("Options:count=7", await File.ReadAllLinesAsync(Path.Combine(sandbox.SkuaDir, "options", "TestFarm.cfg"), Ct));
    }

    [Theory]
    [InlineData("nope", "1", "has no option 'nope'")]
    [InlineData("count", "many", "isn't a valid int")]
    [InlineData("mode", "Medium", "Fast Farm, Slow")]
    [InlineData("once", "2", "transient")]
    public async Task Script_start_refuses_an_unknown_option_or_a_bad_value_with_InvalidArgument_and_stores_nothing(string key, string value, string message)
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Farm.cs", Farm);

        ControlException e = await Assert.ThrowsAsync<ControlException>(() => game.Connection.ScriptStartAsync("Tests/Farm.cs",
            new Dictionary<string, string> { ["count"] = "9", [key] = value }, cancellationToken: Ct));
        ScriptStatusDto status = await game.Connection.ScriptStatusAsync(Ct);

        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains(message, e.Message);
        Assert.Equal(ScriptState.Idle, status.State);
        Assert.False(File.Exists(Path.Combine(sandbox.SkuaDir, "options", "TestFarm.cfg")));
    }

    /// <summary>A merge shop's options: a group whose text entries all have a blank name, and two options that share a name.</summary>
    private static readonly string Merge = TestScripts.Main(
        """
        bot.Log($"mode={bot.Config.Get<string>("Generic", "mode")} id={bot.Config.Get<bool>("Select", "79817")}");
        """,
        """
        public string OptionsStorage = "TestMerge";

        public string[] MultiOptions = { "Generic", "Select" };

        public List<IOption> Generic = new()
        {
            new Option<string>("mode", "Mode", "", "all"),
            new Option<string>(" ", "Mode Explanation [all]", "You get all the items.", "click here"),
            new Option<string>(" ", "Mode Explanation [select]", "You pick the items.", "click here"),
        };

        public List<IOption> Select = new()
        {
            new Option<bool>("79817", "Some Item", "", false),
        };

        public List<IOption> Options = new()
        {
            new Option<int>("dup", "First", "", 1),
            new Option<int>("dup", "Second", "", 2),
        };
        """);

    [Fact]
    public async Task Script_start_sets_options_of_a_Script_whose_other_options_share_a_name()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Merge.cs", Merge);

        await game.Connection.ScriptStartAsync("Tests/Merge.cs",
            new Dictionary<string, string> { ["Generic:mode"] = "select", ["Select:79817"] = "true" }, cancellationToken: Ct);
        await game.Connection.ScriptWaitAsync(60, Ct);

        LogEntryDto line = (await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text!.StartsWith("mode=", StringComparison.Ordinal)))[0];
        Assert.Equal("mode=select id=True", line.Text);
    }

    [Theory]
    [InlineData("dup", "has 2 options named 'dup'")]
    [InlineData("Generic: ", "is text Tests/Merge.cs shows among its options")]
    public async Task Script_start_refuses_an_option_name_two_options_share_or_a_text_entry_with_InvalidArgument(string key, string message)
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Merge.cs", Merge);

        ControlException e = await Assert.ThrowsAsync<ControlException>(() => game.Connection.ScriptStartAsync("Tests/Merge.cs",
            new Dictionary<string, string> { [key] = "3" }, cancellationToken: Ct));

        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains(message, e.Message);
        Assert.False(File.Exists(Path.Combine(sandbox.SkuaDir, "options", "TestMerge.cfg")));
    }

    [Fact]
    public async Task Script_options_marks_the_text_entries_as_text()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Merge.cs", Merge);

        ScriptOptionsResult result = await game.Connection.ScriptOptionsAsync("Tests/Merge.cs", Ct);

        Assert.Equal(
            ["Mode Explanation [all]", "Mode Explanation [select]"],
            result.Options.Where(o => o.Text).Select(o => o.DisplayName));
    }

    [Fact]
    public async Task The_options_window_Core_opens_at_a_first_start_is_a_no_op_so_the_Script_only_ever_sees_its_stored_values()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Watch.cs", TestScripts.Main(
            """
            var seen = new HashSet<string>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 1000)
            {
                try { seen.Add(bot.Config.Get<string>("name") ?? "(none)"); } catch (System.Exception e) { seen.Add(e.GetType().Name); }
            }
            bot.Log("seen " + string.Join(",", seen));
            """,
            """
            public string OptionsStorage = "TestWatch";

            public List<IOption> Options = new() { new Option<string>("name", "Name", "", "default") };
            """));

        await game.Connection.ScriptStartAsync("Tests/Watch.cs", new Dictionary<string, string> { ["name"] = "stored" }, cancellationToken: Ct);
        await game.Connection.ScriptWaitAsync(60, Ct);

        LogEntryDto line = (await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text!.StartsWith("seen ", StringComparison.Ordinal)))[0];
        Assert.Equal("seen stored", line.Text);
    }

    [Fact]
    public async Task An_option_stored_as_empty_reads_back_as_empty_and_one_never_stored_as_its_default()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Classes.cs", TestScripts.Main(
            """
            bot.Log($"Class1=[{bot.Config.Get<string>("Class1") ?? "(null)"}] Class2=[{bot.Config.Get<string>("Class2") ?? "(null)"}]");
            """,
            """
            public bool DontPreconfigure = true;

            public string OptionsStorage = "TestClasses";

            public List<IOption> Options = new()
            {
                new Option<string>("Class1", "Class 1", "", "ArchPaladin"),
                new Option<string>("Class2", "Class 2", "", "StoneCrusher"),
            };
            """));

        await game.Connection.ScriptStartAsync("Tests/Classes.cs", new Dictionary<string, string> { ["Class1"] = "" }, cancellationToken: Ct);
        await game.Connection.ScriptWaitAsync(60, Ct);

        LogEntryDto line = (await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text!.StartsWith("Class1=", StringComparison.Ordinal)))[0];
        Assert.Equal("Class1=[] Class2=[StoneCrusher]", line.Text);
        Assert.Contains("Options:Class1=", await File.ReadAllLinesAsync(Path.Combine(sandbox.SkuaDir, "options", "TestClasses.cfg"), Ct));
    }

    [Fact]
    public async Task Script_options_shows_an_option_stored_as_empty_as_empty_beside_its_default()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Farm.cs", Farm);
        Directory.CreateDirectory(Path.Combine(sandbox.SkuaDir, "options"));
        await File.WriteAllLinesAsync(Path.Combine(sandbox.SkuaDir, "options", "TestFarm.cfg"), ["Extra:name="], Ct);

        ScriptOptionsResult result = await game.Connection.ScriptOptionsAsync("Tests/Farm.cs", Ct);

        ScriptOptionDto name = Assert.Single(result.Options, o => o.Key == "Extra:name");
        Assert.Equal(("", "nobody"), (name.Value, name.Default));
    }

    private sealed class OptionComparer : IEqualityComparer<ScriptOptionDto>
    {
        public bool Equals(ScriptOptionDto? x, ScriptOptionDto? y) =>
            x! with { Choices = null } == y! with { Choices = null } && (x.Choices ?? []).SequenceEqual(y.Choices ?? []);

        public int GetHashCode(ScriptOptionDto obj) => obj.Key.GetHashCode();
    }
}
