using System.Text.Json;
using System.Text.RegularExpressions;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>
/// <c>skua logs gains</c> on a trimmed session log of an Engine named alt1: two Nation farming runs. The expected numbers are the ones the Python
/// prototype of the attribution printed for this file.
/// </summary>
public class LogGainsTests
{
    private static string Fixture => Path.Combine(EngineSandbox.BinDir, "fixtures", "session-gains.jsonl");

    [Fact]
    public async Task Gains_attributes_each_gain_to_the_turn_in_that_paid_it_or_to_monster_drops()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("logs", "gains", "--run", "2", "--file", Fixture, "--json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        JsonElement gains = json.RootElement;
        Assert.Equal((2, "Nation/Materials/DiamondofNulgath.cs", 1946.365), (gains.GetProperty("run").GetInt32(), gains.GetProperty("script").GetString(), gains.GetProperty("durationSec").GetDouble()));
        Assert.Equal(
            ["Bamboozle vs Drudgen 3", "Supplies to Spin The Wheel of Chance 3", "Swindle's Bonus Deal 2", "The Assistant 1", "Swindle's Return Policy 1"],
            gains.GetProperty("sources").EnumerateArray().Select(s => $"{s.GetProperty("name")} {s.GetProperty("turnIns")}"));

        JsonElement assistant = Source(gains, "The Assistant");
        Assert.Equal(2859, assistant.GetProperty("questId").GetInt32());
        // The Assistant also offers rewards as drops during its turn-in; the five the Script picked up after it are its too.
        Assert.Equal("4771:333, 4723:250, 4770:69, 6136:54, 4762:3, 4718:1, 4747:1, 4750:1, 4751:1, 4858:1", Counts(assistant, "gained"));
        Assert.Equal("52824:250", Counts(assistant, "spent"));
        Assert.Equal("4723:3, 4770:3, 4771:3, 4861:1, 6136:1", Counts(Source(gains, "Supplies to Spin The Wheel of Chance"), "gained"));
        Assert.Equal("67266:3", Counts(Source(gains, "Supplies to Spin The Wheel of Chance"), "spent"));
        Assert.Equal("", Counts(Source(gains, "Bamboozle vs Drudgen"), "gained"));
        Assert.Equal("4771:140, 57446:6", Counts(Source(gains, "Swindle's Bonus Deal"), "gained"));
        Assert.Equal("4747:2, 4748:2, 4750:2, 4751:2, 4858:2", Counts(Source(gains, "Swindle's Bonus Deal"), "spent"));
        Assert.Equal("4771:70, 57446:3", Counts(Source(gains, "Swindle's Return Policy"), "gained"));
        Assert.Equal("Unidentified 13", Item(assistant.GetProperty("gained"), 4762).GetProperty("name").GetString());

        // The prototype's turn-ins/h and pays per hour.
        foreach ((string quest, double turnIns, (int Item, double PerHour)[] pays) in new (string, double, (int, double)[])[]
        {
            ("Bamboozle vs Drudgen", 6, []),
            ("Supplies to Spin The Wheel of Chance", 6, [(4723, 6), (4771, 6), (4770, 6), (6136, 2)]),
            ("Swindle's Bonus Deal", 4, [(4771, 259), (57446, 11)]),
            ("The Assistant", 2, [(4771, 616), (4723, 462), (4770, 128), (6136, 100), (4762, 6)]),
            ("Swindle's Return Policy", 2, [(4771, 129), (57446, 6)]),
        })
        {
            JsonElement source = Source(gains, quest);
            AssertRate(turnIns, source.GetProperty("turnInsPerHour"));
            foreach ((int item, double perHour) in pays)
                AssertRate(perHour, Item(source.GetProperty("gained"), item).GetProperty("perHour"));
        }

        // The prototype's net per hour: gains less what the turn-ins took.
        JsonElement items = gains.GetProperty("items");
        foreach ((int item, long net, double perHour) in new[] { (4771, 546L, 1010.0), (4770, 72, 133), (6136, 55, 102), (4723, 253, 468), (4762, 3, 6), (57446, 9, 17) })
        {
            Assert.Equal(net, Item(items, item).GetProperty("net").GetInt64());
            AssertRate(perHour, Item(items, item).GetProperty("netPerHour"));
        }
        JsonElement unidentified32 = Item(items, 4858);
        Assert.Equal((1L, 2L, -1L), (unidentified32.GetProperty("gained").GetInt64(), unidentified32.GetProperty("spent").GetInt64(), unidentified32.GetProperty("net").GetInt64()));
    }

    [Fact]
    public async Task Gains_skips_temporary_items_and_a_refused_turn_in_and_subtracts_what_turn_ins_took()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("logs", "gains", "--run", "1", "--file", Fixture, "--json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        JsonElement gains = json.RootElement;
        Assert.Equal(
            ["Contract Exchange 3", "Diamond Exchange 3", "Crag's Thirst 3", "Monster drops "],
            gains.GetProperty("sources").EnumerateArray().Select(s => $"{s.GetProperty("name")} {s.GetProperty("turnIns")}"));
        Assert.Equal("22332:3", Counts(Source(gains, "Contract Exchange"), "gained"));
        Assert.Equal("4762:3", Counts(Source(gains, "Diamond Exchange"), "gained"));
        Assert.Equal("4771:45, 15397:3", Counts(Source(gains, "Diamond Exchange"), "spent"));
        Assert.Equal("4771:150", Counts(Source(gains, "Crag's Thirst"), "gained"));
        // Blade Master Rune, a temporary item, arrives as a drop and is no gain.
        Assert.Equal("17603:1", Counts(Source(gains, "Monster drops"), "gained"));
        foreach (string quest in new[] { "Contract Exchange", "Diamond Exchange", "Crag's Thirst" })
            AssertRate(10, Source(gains, quest).GetProperty("turnInsPerHour"));
        AssertRate(484, Item(Source(gains, "Crag's Thirst").GetProperty("gained"), 4771).GetProperty("perHour"));

        JsonElement items = gains.GetProperty("items");
        foreach ((int item, long net, double perHour) in new[] { (4771, 105L, 339.0), (22332, 3, 10), (4723, -300, -968), (4762, 0, 0) })
        {
            Assert.Equal(net, Item(items, item).GetProperty("net").GetInt64());
            AssertRate(perHour, Item(items, item).GetProperty("netPerHour"));
        }
    }

    [Fact]
    public async Task Gains_reads_the_last_run_of_the_Engines_newest_log_file_by_default()
    {
        await using EngineSandbox sandbox = new();
        EngineEndpoint alt1 = EngineEndpoint.Resolve("alt1", sandbox.SkuaDir);
        Directory.CreateDirectory(alt1.LogFilesDir);
        File.WriteAllText(Path.Combine(alt1.LogFilesDir, "2026-10-01T00-00-00.000Z.jsonl"), "");
        File.Copy(Fixture, Path.Combine(alt1.LogFilesDir, "2026-10-06T03-55-43.410Z.jsonl"));

        ProcessResult result = await sandbox.RunCliAsync("--engine", "alt1", "logs", "gains");

        Assert.Equal(0, result.ExitCode);
        string[] lines = result.Stdout.Split('\n');
        Assert.Equal($"Run 2, Nation/Materials/DiamondofNulgath.cs: 32:26 in {Path.Combine(alt1.LogFilesDir, "2026-10-06T03-55-43.410Z.jsonl")}", lines[0]);
        Assert.Contains("The Assistant (quest 2859): 1 turn-in, 2/h", lines);
        Assert.Contains(lines, line => Regex.IsMatch(line, @"^\s+\+3\s+\+6/h  Unidentified 13$"));
        Assert.Contains(lines, line => Regex.IsMatch(line, @"^\s+-250\s+-462/h  item 52824$"));
        // 129.47 an hour, as the prototype printed it.
        Assert.Contains(lines, line => Regex.IsMatch(line, @"^\s+\+70\s+\+129/h  item 4771$"));
        int net = Array.IndexOf(lines, "Net");
        Assert.True(net > 0);
        Assert.Matches(@"^\s+\+546\s+\+1010/h  item 4771$", lines[net + 1]);
    }

    [Fact]
    public async Task Gains_names_a_reward_from_what_the_player_held_as_the_run_started()
    {
        await using EngineSandbox sandbox = new();
        string log = Path.Combine(sandbox.SkuaDir, "held.jsonl");
        static string Entry(object fields) => JsonSerializer.Serialize(fields, ControlJson.Options);
        static string Received(long seq, long ts, string o) =>
            Entry(new { seq, ts, kind = "flash", run = 1, text = $$$"""[Net] [ RECEIVED ]: {"t":"xt","b":{"r":-1,"o":{{{o}}}}}, (len: 0)""" });
        File.WriteAllLines(log,
        [
            Entry(new { seq = 1, ts = 0, kind = "events", run = 1, type = "script.started",
                data = new { run = 1, script = "Tests/Farm.cs", restart = false, inventory = new[] { new { id = 4771, name = "Diamond of Nulgath", qty = 10 } }, temp = Array.Empty<object>(), bank = (object?)null } }),
            Received(2, 1000, """{"cmd":"turnIn","sItems":"20:1"}"""),
            Received(3, 1001, """{"cmd":"addItems","items":{"4771":{"iQty":5,"iQtyNow":15}}}"""),
            Received(4, 1002, """{"cmd":"ccqr","bSuccess":1,"QuestID":1,"sName":"Test Quest"}"""),
            Entry(new { seq = 5, ts = 3_600_000, kind = "events", run = 1, type = "script.stopped", data = new { run = 1 } }),
        ]);

        ProcessResult result = await sandbox.RunCliAsync("logs", "gains", "--file", log);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Test Quest (quest 1): 1 turn-in, 1/h", result.Stdout);
        Assert.Matches(@"\n\s+\+5\s+\+5/h  Diamond of Nulgath\n", result.Stdout);
    }

    [Fact]
    public async Task Gains_credits_a_quest_that_takes_nothing_from_its_turn_in_and_names_the_reward_from_the_quest()
    {
        await using EngineSandbox sandbox = new();
        // Free Daily Boost takes no items, so no turnIn packet comes: the turn-in starts with what Skua sent. The reward is a boost
        // the player already held, whose addItems has no name; the quest's rewards name it.
        string log = WriteLog(sandbox,
        [
            Started(1, 0),
            Received(2, 1000, """{"cmd":"getQuests","quests":{"4069":{"QuestID":4069,"sName":"Free Daily Boost","oRewards":{"itemsC":{"0":{"ItemID":27555,"sName":"Class Boost! (60 Min)","iQty":1}}}}}}"""),
            Sent(3, 2000, "%xt%zm%tryQuestComplete%1%4069%27555%false%1%wvz%"),
            Received(4, 2200, """{"cmd":"addItems","items":{"27555":{"iQty":1,"iQtyNow":3,"CharItemID":1,"bBank":0}}}"""),
            Received(5, 2400, """{"cmd":"ccqr","bSuccess":1,"QuestID":4069,"sName":"Free Daily Boost"}"""),
            Stopped(6, 3_600_000),
        ]);

        using JsonDocument json = await GainsAsync(sandbox, log);

        JsonElement gains = json.RootElement;
        Assert.Equal(["Free Daily Boost 1"], gains.GetProperty("sources").EnumerateArray().Select(s => $"{s.GetProperty("name")} {s.GetProperty("turnIns")}"));
        JsonElement boost = Item(Source(gains, "Free Daily Boost").GetProperty("gained"), 27555);
        Assert.Equal((1L, "Class Boost! (60 Min)"), (boost.GetProperty("qty").GetInt64(), boost.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Gains_credits_a_reward_the_game_drops_to_its_quest_and_other_drops_to_monsters()
    {
        await using EngineSandbox sandbox = new();
        // Odd's 'n' Ends pays its Crypto Token as a drop: offered (dropItem) during the turn-in, picked up (getDrop) after its ccqr.
        string log = WriteLog(sandbox,
        [
            Started(1, 0),
            Sent(2, 1000, "%xt%zm%tryQuestComplete%1%6187%-1%false%1%wvz%"),
            Received(3, 1200, """{"cmd":"turnIn","sItems":"42658:1"}"""),
            Received(4, 1300, """{"cmd":"dropItem","items":{"42657":{"ItemID":42657,"sName":"Crypto Token","iQty":1}}}"""),
            Received(5, 1400, """{"cmd":"ccqr","bSuccess":1,"QuestID":6187,"sName":"Odd's 'n' Ends"}"""),
            Received(6, 1600, """{"cmd":"getDrop","ItemID":42657,"bSuccess":1,"bBank":1,"iQty":1,"iQtyNow":33}"""),
            Received(7, 5000, """{"cmd":"dropItem","items":{"5574":{"ItemID":5574,"sName":"Slain Gorillaphant","iQty":1}}}"""),
            Received(8, 5200, """{"cmd":"getDrop","ItemID":5574,"bSuccess":1,"iQty":1,"iQtyNow":1}"""),
            // A turn-in the server never answers is over a few seconds later: what comes after is a drop again.
            Sent(9, 6000, "%xt%zm%tryQuestComplete%1%802%-1%false%1%wvz%"),
            Received(10, 40_000, """{"cmd":"addItems","items":{"5574":{"ItemID":5574,"sName":"Slain Gorillaphant","iQty":1}}}"""),
            Stopped(11, 3_600_000),
        ]);

        using JsonDocument json = await GainsAsync(sandbox, log);

        JsonElement gains = json.RootElement;
        Assert.Equal(["Odd's 'n' Ends 1", "Monster drops "], gains.GetProperty("sources").EnumerateArray().Select(s => $"{s.GetProperty("name")} {s.GetProperty("turnIns")}"));
        Assert.Equal("42657:1", Counts(Source(gains, "Odd's 'n' Ends"), "gained"));
        Assert.Equal("42658:1", Counts(Source(gains, "Odd's 'n' Ends"), "spent"));
        Assert.Equal("5574:2", Counts(Source(gains, "Monster drops"), "gained"));
        Assert.Equal("Crypto Token", Item(Source(gains, "Odd's 'n' Ends").GetProperty("gained"), 42657).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Gains_credits_the_Wheel_of_Doom_spin_to_its_quest()
    {
        await using EngineSandbox sandbox = new();
        // The Wheel of Doom pays with one Wheel packet: Treasure Potions (dropQty of them), a Daily XP Boost, and a prize (Item) at times.
        string log = WriteLog(sandbox,
        [
            Started(1, 0),
            Sent(2, 1000, "%xt%zm%tryQuestComplete%1%3075%-1%false%1%wvz%"),
            Received(3, 1200, """{"cmd":"Wheel","dropItems":{"18927":{"ItemID":18927,"sName":"Treasure Potion"},"19189":{"ItemID":19189,"sName":"Daily XP Boost! (1 hr)"}},"charItem1":1,"charItem2":2,"dropQty":2,"iQty1":737,"iQty2":1,"Item":{"ItemID":5000,"sName":"Prize","bTemp":false}}"""),
            Received(4, 1400, """{"cmd":"ccqr","bSuccess":1,"QuestID":3075,"sName":"Doom Member Free Spin"}"""),
            Stopped(5, 3_600_000),
        ]);

        using JsonDocument json = await GainsAsync(sandbox, log);

        JsonElement spin = Source(json.RootElement, "Doom Member Free Spin");
        Assert.Equal("18927:2, 5000:1, 19189:1", Counts(spin, "gained"));
        Assert.Equal("Daily XP Boost! (1 hr)", Item(spin.GetProperty("gained"), 19189).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Gains_names_an_item_from_the_inventory_the_game_loads()
    {
        await using EngineSandbox sandbox = new();
        string log = WriteLog(sandbox,
        [
            Received(1, 0, """{"cmd":"loadInventoryBig","items":[{"ItemID":4771,"sName":"Diamond of Nulgath","iQty":10}]}""", run: null),
            Started(2, 0),
            Received(3, 1000, """{"cmd":"addItems","items":{"4771":{"iQty":5,"iQtyNow":15}}}"""),
            Stopped(4, 3_600_000),
        ]);

        using JsonDocument json = await GainsAsync(sandbox, log);

        Assert.Equal("Diamond of Nulgath", Item(Source(json.RootElement, "Monster drops").GetProperty("gained"), 4771).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Gains_of_a_run_the_log_lacks_names_the_runs_it_has()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("logs", "gains", "--run", "7", "--file", Fixture);

        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), result.ExitCode);
        Assert.Equal($"skua: Run 7 isn't in {Fixture}; it has runs 1 and 2.", result.Stderr.Trim());
    }

    private static string Entry(object fields) => JsonSerializer.Serialize(fields, ControlJson.Options);

    private static string Started(long seq, long ts) =>
        Entry(new { seq, ts, kind = "events", run = 1, type = "script.started",
            data = new { run = 1, script = "Tests/Dailies.cs", restart = false, inventory = Array.Empty<object>(), temp = Array.Empty<object>(), bank = (object?)null } });

    private static string Stopped(long seq, long ts) => Entry(new { seq, ts, kind = "events", run = 1, type = "script.stopped", data = new { run = 1 } });

    private static string Received(long seq, long ts, string o, int? run = 1) =>
        Entry(new { seq, ts, kind = "flash", run, text = $$$"""[Net] [ RECEIVED ]: {"t":"xt","b":{"r":-1,"o":{{{o}}}}}, (len: 0)""" });

    private static string Sent(long seq, long ts, string packet) => Entry(new { seq, ts, kind = "flash", run = 1, text = $"[Net] [Sending - STR]: {packet}\n" });

    private static string WriteLog(EngineSandbox sandbox, string[] entries)
    {
        string log = Path.Combine(sandbox.SkuaDir, "session.jsonl");
        File.WriteAllLines(log, entries);
        return log;
    }

    private static async Task<JsonDocument> GainsAsync(EngineSandbox sandbox, string log)
    {
        ProcessResult result = await sandbox.RunCliAsync("logs", "gains", "--file", log, "--json");
        Assert.True(result.ExitCode == 0, result.Stderr);
        return JsonDocument.Parse(result.Stdout);
    }

    private static JsonElement Source(JsonElement gains, string name) =>
        gains.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);

    private static JsonElement Item(JsonElement items, int id) => items.EnumerateArray().Single(i => i.GetProperty("itemId").GetInt32() == id);

    private static string Counts(JsonElement source, string list) =>
        string.Join(", ", source.GetProperty(list).EnumerateArray().Select(i => $"{i.GetProperty("itemId")}:{i.GetProperty("qty")}"));

    /// <summary>A rate rounds to the whole number the prototype printed.</summary>
    private static void AssertRate(double expected, JsonElement perHour) => Assert.Equal(expected, Math.Round(perHour.GetDouble()));
}
