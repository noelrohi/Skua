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
}
