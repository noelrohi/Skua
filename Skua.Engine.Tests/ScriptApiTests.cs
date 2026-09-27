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
