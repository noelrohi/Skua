using Skua.Control;
using Skua.Core.Models.Items;

namespace Skua.Engine.Tests;

/// <summary>
/// Item names spelled with <c>&amp;amp;</c>, as the Windows client reads them and Scripts write them, against the mac's plain <c>&amp;</c>,
/// and the reverse (#214). The fake Game Host's game (see FakeGame.cs) holds one item of each spelling in every store.
/// </summary>
public class ItemNameTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Crag &amp; Bamboozle", "Crag & Bamboozle")]
    [InlineData("Crag & Bamboozle", "Crag &amp; Bamboozle")]
    [InlineData("Crag & Bamboozle", "Crag & Bamboozle")]
    [InlineData("Crag &amp; Bamboozle", "Crag &amp; Bamboozle")]
    [InlineData("A &amp; B & C", "A & B &amp; C")]
    public void An_ampersand_and_its_entity_name_the_same_item(string a, string b)
    {
        Assert.True(ItemNameComparer.Ordinal.Equals(a, b));
        Assert.Equal(ItemNameComparer.Ordinal.GetHashCode(a), ItemNameComparer.Ordinal.GetHashCode(b));
        Assert.True(ItemNameComparer.OrdinalIgnoreCase.Equals(a.ToUpperInvariant(), b));
    }

    [Theory]
    [InlineData("Crag & Bamboozle", "Crag and Bamboozle")]
    [InlineData("Crag &amp; Bamboozle", "Crag &amp;amp; Bamboozle")]
    [InlineData("Crag &amp; Bamboozle", "Crag Bamboozle")]
    [InlineData("Crag & Bamboozle", null)]
    public void Other_names_differ(string? a, string? b)
    {
        Assert.False(ItemNameComparer.Ordinal.Equals(a, b));
        Assert.False(ItemNameComparer.OrdinalIgnoreCase.Equals(a, b));
    }

    [Fact]
    public void The_exact_comparer_keeps_the_case()
    {
        Assert.False(ItemNameComparer.Ordinal.Equals("crag &amp; bamboozle", "Crag & Bamboozle"));
        Assert.True(ItemNameComparer.OrdinalIgnoreCase.Equals("crag &amp; bamboozle", "Crag & Bamboozle"));
    }

    [Fact]
    public async Task Every_item_store_finds_an_item_by_either_spelling()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("own 4845 Pet Crag & Bamboozle");
        await session.GameHost.DoAsync("own 4846 Item Cysero &amp; Alina");
        foreach (string store in new[] { "bank", "temp", "house" })
        {
            await session.GameHost.DoAsync($"stock {store} 4845 Crag & Bamboozle");
            await session.GameHost.DoAsync($"stock {store} 4846 Cysero &amp; Alina");
        }

        EvalResult found = await session.Connection.EvalAsync(
            """
            Bot.Wait.ForTrue(() => Bot.Inventory.Contains(4845), 20);
            Bot.Bank.Load();
            Bot.Wait.ForTrue(() => Bot.Bank.Contains(4845), 20);
            string a = "Crag &amp; Bamboozle", b = "Cysero & Alina";
            return new[]
            {
                $"inventory {Bot.Inventory.Contains(a)} {Bot.Inventory.Contains(b)} {Bot.Inventory.GetItem(a)?.ID} {Bot.Inventory.GetItem(b)?.ID}",
                $"bank {Bot.Bank.Contains(a)} {Bot.Bank.Contains(b)} {Bot.Bank.GetItem(a)?.ID} {Bot.Bank.GetItem(b)?.ID}",
                $"temp {Bot.TempInv.Contains(a)} {Bot.TempInv.Contains(b)} {Bot.TempInv.GetItem(a)?.ID} {Bot.TempInv.GetItem(b)?.ID}",
                $"house {Bot.House.Contains(a)} {Bot.House.Contains(b)} {Bot.House.GetItem(a)?.ID} {Bot.House.GetItem(b)?.ID}",
                $"helper {Bot.InvHelper.HasAll(new[] { a, b }, 1, false)} {Bot.InvHelper.Check(a, 1, false)} {Bot.InvHelper.Check(b, 1, false)}",
            };
            """,
            cancellationToken: Ct);

        Assert.Null(found.Error);
        Assert.Equal(
            [
                "inventory True True 4845 4846",
                "bank True True 4845 4846",
                "temp True True 4845 4846",
                "house True True 4845 4846",
                "helper True True True",
            ],
            found.Value!.Value.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Drops_find_and_pick_up_a_drop_by_either_spelling()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("drop 4845 1 Crag & Bamboozle");
        await session.GameHost.DoAsync("drop 4846 1 Cysero &amp; Alina");

        EvalResult dropped = await session.Connection.EvalAsync(
            """
            Bot.Wait.ForTrue(() => Bot.Drops.Exists(4845) && Bot.Drops.Exists(4846), 20);
            bool exists = Bot.Drops.Exists("Crag &amp; Bamboozle") && Bot.Drops.Exists("Cysero & Alina");
            Bot.Drops.RejectExcept("Crag &amp; Bamboozle", "Cysero & Alina");
            Bot.Drops.Pickup("Crag &amp; Bamboozle");
            Bot.Drops.Pickup("Cysero & Alina");
            return $"{exists} {Bot.Drops.Exists(4845)} {Bot.Drops.Exists(4846)}";
            """,
            cancellationToken: Ct);
        string[] calls = await session.GameHost.CallsAsync();

        Assert.Null(dropped.Error);
        Assert.Equal("True False False", dropped.Value!.Value.GetString());
        Assert.Contains(calls, c => c.StartsWith("send %xt%zm%getDrop%", StringComparison.Ordinal) && c.EndsWith("%4845%", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.StartsWith("send %xt%zm%getDrop%", StringComparison.Ordinal) && c.EndsWith("%4846%", StringComparison.Ordinal));
        // skua.swf keeps a drop whose lower-cased name, as the game holds it, is on the whitelist.
        string[] whitelist = Assert.Single(calls, c => c.StartsWith("rejectExcept ", StringComparison.Ordinal))["rejectExcept ".Length..].Split(',');
        Assert.Contains("crag & bamboozle", whitelist);
        Assert.Contains("cysero &amp; alina", whitelist);
    }

    [Fact]
    public async Task Shops_buy_an_item_by_either_spelling()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("shop-item 4845 Crag & Bamboozle");
        await session.GameHost.DoAsync("shop-item 4846 Cysero &amp; Alina");

        EvalResult bought = await session.Connection.EvalAsync(
            """
            Bot.Wait.ForTrue(() => Bot.Inventory.Contains(1), 20);
            Bot.Shops.Load(500);
            Bot.Shops.BuyItem("Crag &amp; Bamboozle");
            Bot.Shops.BuyItem("Cysero & Alina", 2);
            return Bot.Shops.ID;
            """,
            cancellationToken: Ct);
        string[] calls = await session.GameHost.CallsAsync();

        Assert.Null(bought.Error);
        Assert.Equal(500, bought.Value!.Value.GetInt32());
        Assert.Equal(["buy 4845 -1", "buy 4846 2"], calls.Where(c => c.StartsWith("buy ", StringComparison.Ordinal)));
    }
}
