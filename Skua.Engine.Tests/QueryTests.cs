using System.Text.Json;
using System.Text.RegularExpressions;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>The typed state queries and the <c>status</c> player summary, against the fake Game Host's simulated game (see FakeGame.cs).</summary>
public class QueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Property names the game uses on the wire, which the DTOs must never carry.</summary>
    private static readonly Regex WireName = new("\"(ItemID|sName|iQty|iStk|sType|bEquip|EnhLvl|QuestID|oItems|oRewards|turnin|strMapName|curRoom|uoName|intHP|intHPMax|strFrame|strPad|MonID|MonMapID|strMonName|intState)\"");

    [Fact]
    public async Task Inventory_lists_each_item_store_with_its_slots()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        // Straight after the login, before the game has the inventory and the bank count.
        InventoryResult bank = await session.Connection.InventoryAsync(InventoryKind.Bank, Ct);
        InventoryResult inventory = await session.Connection.InventoryAsync(cancellationToken: Ct);
        InventoryResult bankAgain = await session.Connection.InventoryAsync(InventoryKind.Bank, Ct);
        InventoryResult temp = await session.Connection.InventoryAsync(InventoryKind.Temp, Ct);
        InventoryResult house = await session.Connection.InventoryAsync(InventoryKind.House, Ct);

        Assert.Equal((InventoryKind.Inventory, 3, (int?)40), (inventory.Kind, inventory.UsedSlots, inventory.TotalSlots));
        Assert.Equal(
            [
                new ItemDto(1, "Default Sword", 1, 1, "Sword", true, 1),
                new ItemDto(2, "Healer", 1, 1, "Class", true, 0),
                new ItemDto(3, "Treasure Chest", 5, 1000, "Item", false, 0),
            ],
            inventory.Items);
        Assert.Equal((1, (int?)10), (bank.UsedSlots, bank.TotalSlots));
        Assert.Equal([new ItemDto(10, "Bank Relic", 2, 10, "Item", false, 0)], bank.Items);
        Assert.Equal(bank.Items, bankAgain.Items);
        Assert.Equal((1, (int?)null), (temp.UsedSlots, temp.TotalSlots));
        Assert.Equal([new ItemDto(20, "Slime Sample", 3, 10, "Quest Item", false, null)], temp.Items);
        Assert.Equal((1, (int?)20), (house.UsedSlots, house.TotalSlots));
        Assert.Equal([new ItemDto(30, "Wooden Chair", 1, 1, "Floor Item", false, 0)], house.Items);
        // The bank loads once per login, as the game loads it: over HTTP, since the game server no longer answers loadBank.
        string[] calls = await session.GameHost.CallsAsync();
        Assert.Single(calls, c => c == "getBank");
        Assert.DoesNotContain("loadBank", calls);
    }

    [Fact]
    public async Task Quests_lists_the_loaded_or_active_quests_with_requirements_and_rewards()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        QuestsResult loaded = await session.Connection.QuestsAsync(cancellationToken: Ct);
        QuestsResult active = await session.Connection.QuestsAsync(QuestFilter.Active, Ct);

        QuestDto slimes = new(1001, "Slime Time", QuestStatus.InProgress, false, 100, 50,
            [new QuestRequirementDto(20, "Slime Sample", 5, 3, true), new QuestRequirementDto(21, "Slime Crown", 1, 0, true)],
            [new QuestRewardDto(3, "Treasure Chest", 1)]);
        Assert.Equal(QuestFilter.Loaded, loaded.Filter);
        Assert.Equal([1001, 1002, 1003], loaded.Quests.Select(q => q.Id));
        AssertQuest(slimes, loaded.Quests[0]);
        AssertQuest(new QuestDto(1002, "Chest Hoarder", QuestStatus.Completable, true, 0, 0,
            [new QuestRequirementDto(3, "Treasure Chest", 5, 5, false)], []), loaded.Quests[1]);
        Assert.Equal(QuestStatus.NotAccepted, loaded.Quests[2].Status);
        // Flash's order, which Scripts pair their monsters with (#152).
        Assert.Equal([93556, 93555], loaded.Quests[2].Requirements.Select(r => r.ItemId));
        Assert.Equal([1001, 1002], active.Quests.Select(q => q.Id));
    }

    [Fact]
    public async Task Map_lists_the_cells_players_and_monsters()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        MapDto map = await session.Connection.MapAsync(Ct);

        Assert.Equal("battleon", map.Name);
        Assert.True(map.RoomId > 1000);
        Assert.Equal(["Enter", "r2", "r3"], map.Cells);
        Assert.Equal(
            [new MapPlayerDto(session.Keychain.Username.ToLowerInvariant(), 10, "Enter", "Spawn", 1000, 1000, false), new MapPlayerDto("artixfan", 42, "r3", "Left", 800, 2000, true)],
            map.Players.OrderBy(p => p.Name != session.Keychain.Username.ToLowerInvariant()));
        Assert.Equal([new MonsterDto(7, 1, "Frogzard", "r2", 500, 500, true), new MonsterDto(7, 2, "Frogzard", "r2", 0, 500, false)], map.Monsters);
    }

    [Fact]
    public async Task Drops_lists_the_items_dropped_since_the_login_and_not_yet_picked_up()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        DropsResult none = await session.Connection.DropsAsync(Ct);
        await session.GameHost.DoAsync("drop 40 2 Frogzard Scale");
        await session.GameHost.DoAsync("drop 40 1 Frogzard Scale");
        await session.GameHost.DoAsync("drop 41 1 Dragon Egg");
        DropsResult drops = await WaitForAsync(() => session.Connection.DropsAsync(Ct), d => d.Drops.Count == 2 && d.Drops[0].Qty == 3);
        await session.GameHost.DoAsync("pickup 40");
        DropsResult picked = await WaitForAsync(() => session.Connection.DropsAsync(Ct), d => d.Drops.Count == 1);
        await session.Connection.LoginAsync("Sir Ver", cancellationToken: Ct);
        DropsResult relogged = await session.Connection.DropsAsync(Ct);

        Assert.Empty(none.Drops);
        Assert.Equal([new DropDto(40, "Frogzard Scale", 3), new DropDto(41, "Dragon Egg", 1)], drops.Drops);
        Assert.Equal([new DropDto(41, "Dragon Egg", 1)], picked.Drops);
        Assert.Empty(relogged.Drops);
    }

    [Fact]
    public async Task No_query_carries_a_game_wire_name()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("drop 40 1 Frogzard Scale");

        List<object> replies = [await session.Connection.StatusAsync(Ct), await session.Connection.MapAsync(Ct), await session.Connection.QuestsAsync(cancellationToken: Ct),
            await session.Connection.DropsAsync(Ct)];
        foreach (InventoryKind kind in Enum.GetValues<InventoryKind>())
            replies.Add(await session.Connection.InventoryAsync(kind, Ct));

        foreach (object reply in replies)
        {
            string json = JsonSerializer.Serialize(reply, reply.GetType(), ControlJson.Options);
            Assert.DoesNotMatch(WireName, json);
        }
    }

    [Fact]
    public async Task Every_query_fails_with_NotLoggedIn_before_a_login()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        EngineConnection connection = session.Connection;

        Func<Task>[] queries = [() => connection.InventoryAsync(cancellationToken: Ct), () => connection.QuestsAsync(cancellationToken: Ct), () => connection.MapAsync(Ct), () => connection.DropsAsync(Ct)];
        foreach (Func<Task> query in queries)
            Assert.Equal(ErrorCode.NotLoggedIn, (await Assert.ThrowsAsync<ControlException>(query)).Code);
    }

    [Fact]
    public async Task Status_summarises_the_player_while_playing_and_not_at_the_login_screen()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);

        StatusDto before = await session.Connection.StatusAsync(Ct);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.Connection.JumpAsync("r2", "Right", cancellationToken: Ct);
        StatusDto playing = await session.Connection.StatusAsync(Ct);
        await session.Connection.LogoutAsync(Ct);
        StatusDto after = await session.Connection.StatusAsync(Ct);

        Assert.Null(before.Game.Player);
        Assert.Equal(
            new PlayerDto(session.Keychain.Username, 10, "Healer", 1000, 1000, 80, 100, 5000, "battleon", "r2", "Right", Alive: true, InCombat: false,
                Xp: 1500, RequiredXp: 4000, XpPercent: 37.5),
            playing.Game.Player);
        Assert.Null(after.Game.Player);
    }

    [Fact]
    public async Task Status_keeps_the_player_unknown_or_stale_with_its_age_when_the_game_doesnt_answer_in_time()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        try
        {
            await session.GameHost.DoAsync("delay getGameObject 6000");
            GameStatusDto unknown = (await session.Connection.StatusAsync(Ct)).Game;
            await session.GameHost.DoAsync("delay getGameObject 0");
            GameStatusDto fresh = await FreshStatusAsync(session.Connection);
            await session.GameHost.DoAsync("delay getGameObject 6000");
            GameStatusDto stale = (await session.Connection.StatusAsync(Ct)).Game;

            Assert.Equal((GameState.Playing, null, null), (unknown.State, unknown.Player, unknown.PlayerAgeSec));
            Assert.Equal(fresh.Player, stale.Player);
            Assert.InRange(stale.PlayerAgeSec!.Value, 3, 10);
        }
        finally
        {
            await session.GameHost.DoAsync("delay getGameObject 0");
        }
    }

    [Fact]
    public async Task Status_reports_death_and_combat()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        await session.GameHost.DoAsync("combat");
        PlayerDto fighting = (await session.Connection.StatusAsync(Ct)).Game.Player!;
        await session.GameHost.DoAsync("die");
        PlayerDto dead = (await session.Connection.StatusAsync(Ct)).Game.Player!;

        Assert.Equal((true, true), (fighting.Alive, fighting.InCombat));
        Assert.Equal((false, false, 0), (dead.Alive, dead.InCombat, dead.Hp));
    }

    [Fact]
    public async Task Status_reports_the_XP_and_gold_as_the_game_changes_them()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        await session.GameHost.DoAsync("gain 250 1200");
        PlayerDto gained = (await session.Connection.StatusAsync(Ct)).Game.Player!;
        await session.GameHost.DoAsync("gain 2250 0");
        PlayerDto levelled = (await session.Connection.StatusAsync(Ct)).Game.Player!;

        Assert.Equal((1750, 4000, 43.8, 6200), (gained.Xp, gained.RequiredXp, gained.XpPercent, gained.Gold));
        // Reaching the required XP levels up and starts the next level's XP from zero.
        Assert.Equal((11, 0, 0.0), (levelled.Level, levelled.Xp, levelled.XpPercent));
    }

    /// <summary>Waits out a player read that answers late, until <c>status</c> has a fresh reading.</summary>
    internal static async Task<GameStatusDto> FreshStatusAsync(EngineConnection connection)
    {
        for (int i = 0; i < 10; i++)
        {
            GameStatusDto game = (await connection.StatusAsync(Ct)).Game;
            if (game is { Player: not null, PlayerAgeSec: null })
                return game;
        }
        throw new TimeoutException("status never had a fresh player reading.");
    }

    private static void AssertQuest(QuestDto expected, QuestDto actual)
    {
        Assert.Equal(expected with { Requirements = [], Rewards = [] }, actual with { Requirements = [], Rewards = [] });
        // How long each requirement has gone without a rise is the clock's; QuestProgressTests checks it.
        Assert.Equal(expected.Requirements, actual.Requirements.Select(r => r with { IdleSec = null, GainPerHour = null }));
        Assert.Equal(expected.Rewards, actual.Rewards);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        T value = await read();
        for (int i = 0; i < 40 && !done(value); i++)
        {
            await Task.Delay(50, Ct);
            value = await read();
        }
        return value;
    }
}
