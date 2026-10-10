using System.Diagnostics;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>Core's Script API, through <c>eval</c>, against the fake Game Host's simulated game (see FakeGame.cs), which answers as AQW's current client does.</summary>
public class ScriptApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bank_Load_fills_the_bank_through_getBank()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        // Straight after the login, before the game has the inventory that getBank needs.
        EvalResult bank = await session.Connection.EvalAsync("Bot.Bank.Load(); return Bot.Bank.Items.Select(i => $\"{i.Name} x{i.Quantity}\");", cancellationToken: Ct);

        Assert.Null(bank.Error);
        Assert.Equal(["Bank Relic x2"], bank.Value!.Value.EnumerateArray().Select(e => e.GetString()));
        // The game server no longer answers loadBank.
        string[] calls = await session.GameHost.CallsAsync();
        Assert.Single(calls, c => c == "getBank");
        Assert.DoesNotContain("loadBank", calls);
    }

    [Fact]
    public async Task A_bank_that_has_arrived_is_not_asked_for_again_by_Bank_Load_or_the_Engine()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        EvalResult loads = await session.Connection.EvalAsync("Bot.Bank.Load(); Bot.Bank.Load(); return Bot.Bank.Items.Count;", cancellationToken: Ct);
        InventoryResult bank = await session.Connection.InventoryAsync(InventoryKind.Bank, Ct);

        Assert.Null(loads.Error);
        Assert.Equal(1, loads.Value!.Value.GetInt32());
        Assert.Equal(["Bank Relic"], bank.Items.Select(i => i.Name));
        // A second getBank could answer after a transfer, with the bank from before it.
        Assert.Single(await session.GameHost.CallsAsync(), c => c == "getBank");
    }

    [Fact]
    public async Task Combat_Target_targets_the_monster_without_attacking_it_and_Attack_still_attacks()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.Connection.JumpAsync("r2", cancellationToken: Ct);

        EvalResult byId = await session.Connection.EvalAsync("return $\"{Bot.Combat.Target(1)} {Bot.Player.Target?.MapID}\";", cancellationToken: Ct);
        await session.GameHost.DoAsync("target none");
        // Frogzard 2 is dead, so the living Frogzard 1 is the one targeted, as skua.swf picks it.
        EvalResult byName = await session.Connection.EvalAsync("return $\"{Bot.Combat.Target(\"frogzard\")} {Bot.Player.Target?.MapID} {Bot.Combat.Target(\"Nulgath\")}\";", cancellationToken: Ct);
        string[] targetCalls = await session.GameHost.CallsAsync();
        EvalResult attacked = await session.Connection.EvalAsync("return Bot.Combat.Attack(\"Frogzard\");", cancellationToken: Ct);
        string[] calls = await session.GameHost.CallsAsync();

        Assert.Equal("True 1", byId.Value!.Value.GetString());
        Assert.Equal("True 1 False", byName.Value!.Value.GetString());
        Assert.Equal(["target 1", "target frogzard", "target Nulgath"], targetCalls.Where(c => c.StartsWith("target ", StringComparison.Ordinal)));
        Assert.DoesNotContain(targetCalls, c => c.StartsWith("attack ", StringComparison.Ordinal));
        Assert.Null(attacked.Error);
        Assert.Contains("attack Frogzard", calls);
    }

    [Fact]
    public async Task Inventory_EquipItem_equips_the_item_and_logs_nothing()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("own 4 Class Rogue");

        EvalResult equip = await session.Connection.EvalAsync("Bot.Inventory.EquipItem(4); return Bot.Player.CurrentClass?.Name;", cancellationToken: Ct);

        Assert.Null(equip.Error);
        Assert.Equal("Rogue", equip.Value!.Value.GetString());
        Assert.Empty(equip.Logs);
    }

    [Fact]
    public async Task Inventory_EquipItem_the_game_server_equips_late_returns_after_its_usual_wait_and_warns_of_nothing()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("own 4 Class Rogue");
        // Also in a house, where the game server equips too (#198 said it never does).
        await session.Connection.JoinAsync("house", cancellationToken: Ct);
        await session.GameHost.DoAsync("equip-delay 6000");

        Stopwatch waited = Stopwatch.StartNew();
        // Timed inside the snippet, so compiling it doesn't count.
        EvalResult equip = await session.Connection.EvalAsync(
            "var sw = System.Diagnostics.Stopwatch.StartNew(); Bot.Inventory.EquipItem(4); long ms = sw.ElapsedMilliseconds; return Bot.Player.CurrentClass?.Name + \"|\" + ms;",
            cancellationToken: Ct);
        EvalResult landed = await session.Connection.EvalAsync("Bot.Wait.ForItemEquip(4, 100); return Bot.Player.CurrentClass?.Name;", cancellationToken: Ct);
        // Past the time EquipItem gives the game server before it warns.
        TimeSpan rest = TimeSpan.FromSeconds(11) - waited.Elapsed;
        if (rest > TimeSpan.Zero)
            await Task.Delay(rest, Ct);
        LogPage logs = await session.Connection.LogsAsync(LogKind.Script, null, 1000, Ct);

        Assert.Null(equip.Error);
        string[] returned = equip.Value!.Value.GetString()!.Split('|');
        Assert.Equal("Healer", returned[0]);
        // Well before the game server equips it at 6 s; the equip cooldown and the ~1 s wait take up to ~3 s on a slow runner.
        Assert.InRange(TimeSpan.FromMilliseconds(long.Parse(returned[1])), TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Empty(equip.Logs);
        Assert.Equal("Rogue", landed.Value!.Value.GetString());
        Assert.DoesNotContain(logs.Entries, e => e.Text!.StartsWith("Equipping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inventory_EquipItem_the_game_server_never_equips_warns_once_after_the_Script_goes_on()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("own 4 Class Rogue");
        await session.GameHost.DoAsync("equip-delay never");

        EvalResult equip = await session.Connection.EvalAsync("Bot.Inventory.EquipItem(4); return Bot.Player.CurrentClass?.Name;", cancellationToken: Ct);
        List<LogEntryDto> warnings = await session.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text!.StartsWith("Equipping", StringComparison.Ordinal));
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        LogPage logs = await session.Connection.LogsAsync(LogKind.Script, null, 1000, Ct);

        Assert.Equal(1, (await session.GameHost.CallsAsync()).Count(c => c == "equipItem 4"));
        Assert.Null(equip.Error);
        Assert.Equal("Healer", equip.Value!.Value.GetString());
        Assert.Empty(equip.Logs);
        Assert.Equal(["Equipping Rogue failed: it still isn't equipped 10 s after the request (map battleon)."], warnings.Select(w => w.Text));
        Assert.Single(logs.Entries, e => e.Text!.StartsWith("Equipping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Map_Players_lists_the_rooms_players()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        string me = session.Keychain.Username.ToLowerInvariant();

        EvalResult players = await session.Connection.EvalAsync("Bot.Map.Players.Select(p => $\"{p.Name} {p.Level} {p.Cell}\")", cancellationToken: Ct);
        EvalResult artixFan = await session.Connection.EvalAsync("""Bot.Map.GetPlayer("ArtixFan")?.Level""", cancellationToken: Ct);

        Assert.Null(players.Error);
        Assert.Equal(new[] { $"{me} 10 Enter", "artixfan 42 r3" }.Order(), players.Value!.Value.EnumerateArray().Select(e => e.GetString()).Order());
        Assert.Equal(42, artixFan.Value!.Value.GetInt32());
    }

    /// <summary>
    /// Bot.Inventory asks the game's own rules (client 5.0's InvCat) which Space an item fills and whether it fits (#249): a full Bag Space
    /// refuses a pet and a consumable, a full Misc Space a new misc item, while a class always fits and a held stack can be topped up.
    /// </summary>
    [Fact]
    public async Task Inventory_tells_Bag_Space_from_Misc_Space_and_what_fits_in_each()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        // Default Sword fills the one bag slot and Treasure Chest (5 of 1000) the one misc slot.
        await session.GameHost.DoAsync("bag-slots 1");
        await session.GameHost.DoAsync("misc-slots 1");

        const string Fits = """
            Skua.Core.Models.Items.ItemBase Item(int id, string category, string meta = null) => new() { ID = id, Name = $"Item {id}", CategoryString = category, Meta = meta, Quantity = 1, MaxStack = 10 };
            var items = new[] { Item(60, "Pet"), Item(61, "Resource"), Item(3, "Item"), Item(62, "Class"), Item(63, "Item", "5"), Item(64, "Floor Item") };
            var inv = Bot.Inventory;
            return $"{inv.UsedSlots}/{inv.Slots} {inv.MiscUsedSlots}/{inv.MiscSlots} " + string.Join(" ", items.Select(i => $"{inv.GetPool(i)}:{inv.HasSpaceFor(i)}"));
            """;
        EvalResult full = await session.Connection.EvalAsync(Fits, cancellationToken: Ct);
        await session.GameHost.DoAsync("misc-slots 2");
        EvalResult roomier = await session.Connection.EvalAsync(Fits, cancellationToken: Ct);

        Assert.Null(full.Error);
        Assert.Equal("1/1 1/1 bag:False misc:False misc:True class:True bag:False house:True", full.Value!.Value.GetString());
        Assert.Equal("1/1 1/2 bag:False misc:True misc:True class:True bag:False house:True", roomier.Value!.Value.GetString());
    }

    /// <summary>
    /// A Favorite is read from the game's own store (#249), and Bot.Shops refuses to sell one while ProtectFavorites is on, as the game's shop does.
    /// </summary>
    [Fact]
    public async Task Shops_SellItem_refuses_a_Favorite_unless_ProtectFavorites_is_off()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("favorite 3");

        EvalResult protectedSale = await session.Connection.EvalAsync(
            """var favorites = $"{Bot.Inventory.IsFavorited(3)} {Bot.Inventory.IsFavorited(1)}"; Bot.Shops.SellItem("Treasure Chest"); return favorites;""",
            cancellationToken: Ct);
        string[] protectedCalls = await session.GameHost.CallsAsync();
        EvalResult sale = await session.Connection.EvalAsync("""Bot.Shops.ProtectFavorites = false; Bot.Shops.SellItem("Treasure Chest");""", cancellationToken: Ct);
        string[] calls = await session.GameHost.CallsAsync();

        Assert.Null(protectedSale.Error);
        Assert.Equal("True False", protectedSale.Value!.Value.GetString());
        Assert.DoesNotContain(protectedCalls, c => c.Contains("%sellItem%", StringComparison.Ordinal));
        Assert.Null(sale.Error);
        Assert.Single(calls, c => c.StartsWith("send %xt%zm%sellItem%", StringComparison.Ordinal) && c.EndsWith("%3%5%103%", StringComparison.Ordinal));
    }

    /// <summary>
    /// Stack counts come from the game's HUD auras, the player's own and the target's (#249); without a target there are none.
    /// </summary>
    [Fact]
    public async Task Self_and_Target_read_aura_stacks_from_the_HUD()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("hud-aura self Fury 3 10");
        await session.GameHost.DoAsync("hud-aura 1 Shielded 2 0");
        await session.GameHost.DoAsync("target 1");

        EvalResult targeted = await session.Connection.EvalAsync(
            """
            return $"{Bot.Self.GetAuraStacks("fury")} {Bot.Self.GetAuraStacks("Shielded")} {Bot.Target.GetAuraStacks("Shielded")} {Bot.Self.GetAuraSnapshot("Fury")?.Duration} "
                + string.Join(",", Bot.Self.Snapshots.Select(a => a.Name));
            """, cancellationToken: Ct);
        await session.GameHost.DoAsync("target none");
        EvalResult untargeted = await session.Connection.EvalAsync("return Bot.Target.Snapshots.Count;", cancellationToken: Ct);

        Assert.Null(targeted.Error);
        Assert.Equal("3 0 2 10 Fury", targeted.Value!.Value.GetString());
        Assert.Equal(0, untargeted.Value!.Value.GetInt32());
    }

    /// <summary>
    /// AuraStackChanged reports the HUD stack counts that GetAuraStacks reads (#252), the player's and the target's, from 0 when an aura comes
    /// and to 0 when it goes; losing some stacks reports the stacks left.
    /// </summary>
    [Fact]
    public async Task AuraStackChanged_reports_the_HUD_stack_counts()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Stacks.cs", TestScripts.Main("""
            bot.AuraMonitor.AuraStackChanged += (name, from, to, subject) => bot.Log($"stacks {subject} {name} {from} {to}");
            bot.AuraMonitor.EnsureMonitoring(20);
            bot.Log("monitoring");
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));
        await session.Connection.ScriptStartAsync("Tests/Stacks.cs", cancellationToken: Ct);
        await session.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "monitoring");

        await StacksAsync(session, "hud-aura self Fury 1 10", "stacks Self Fury 0 1");
        await StacksAsync(session, "hud-aura self Fury 3 10", "stacks Self Fury 1 3");
        await StacksAsync(session, "hud-aura self Fury 1 10", "stacks Self Fury 3 1");
        await session.GameHost.DoAsync("target 1");
        await StacksAsync(session, "hud-aura 1 Shielded 2 0", "stacks Target Shielded 0 2");
        await StacksAsync(session, "hud-aura self Fury 0 0", "stacks Self Fury 1 0");

        List<LogEntryDto> stacks = await session.Connection.WaitForLogsAsync(LogKind.Script, 5, e => e.Text!.StartsWith("stacks ", StringComparison.Ordinal));
        Assert.Equal(
            ["stacks Self Fury 0 1", "stacks Self Fury 1 3", "stacks Self Fury 3 1", "stacks Target Shielded 0 2", "stacks Self Fury 1 0"],
            stacks.Select(e => e.Text));
    }

    /// <summary>
    /// An aura's effect value is no stack count (#252): changing it raises no AuraStackChanged. And an aura that loses some stacks, which the game
    /// takes out of its auras while the HUD keeps it, isn't reported gone until its last stack goes.
    /// </summary>
    [Fact]
    public async Task An_aura_with_stacks_left_is_not_gone_and_its_effect_value_is_no_stack_count()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Stacks.cs", TestScripts.Main("""
            bot.AuraMonitor.AuraStackChanged += (name, from, to, subject) => bot.Log($"stacks {subject} {name} {from} {to}");
            bot.AuraMonitor.AuraDeactivated += (name, subject) => bot.Log($"gone {subject} {name}");
            bot.AuraMonitor.EnsureMonitoring(20);
            bot.Log("monitoring");
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));
        await session.Connection.ScriptStartAsync("Tests/Stacks.cs", cancellationToken: Ct);
        await session.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "monitoring");

        await session.GameHost.DoAsync("aura self Fury 1");
        await StacksAsync(session, "hud-aura self Fury 3 10", "stacks Self Fury 0 3");
        await session.GameHost.DoAsync("aura self Fury 250");
        await StacksAsync(session, "hud-aura self Haste 1 0", "stacks Self Haste 0 1");
        await session.GameHost.DoAsync("aura-off self Fury");
        await StacksAsync(session, "hud-aura self Fury 1 10", "stacks Self Fury 3 1");
        await StacksAsync(session, "hud-aura self Fury 0 0", "gone Self Fury");

        List<LogEntryDto> lines = await session.Connection.WaitForLogsAsync(LogKind.Script, 5,
            e => e.Text!.StartsWith("stacks ", StringComparison.Ordinal) || e.Text!.StartsWith("gone ", StringComparison.Ordinal));
        Assert.Equal(
            ["stacks Self Fury 0 3", "stacks Self Haste 0 1", "stacks Self Fury 3 1", "stacks Self Fury 1 0", "gone Self Fury"],
            lines.Select(e => e.Text));
    }

    /// <summary>Runs the fake game's <paramref name="directive"/> and waits for the Script's <paramref name="line"/>.</summary>
    private static async Task StacksAsync(GameFixture session, string directive, string line)
    {
        await session.GameHost.DoAsync(directive);
        await session.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == line);
    }
}
