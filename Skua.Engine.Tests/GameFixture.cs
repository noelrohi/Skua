using System.Diagnostics;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>An Engine whose simulated game has loaded, with the servers API and a Test Account in the fake Keychain.</summary>
public sealed class GameFixture : IAsyncDisposable
{
    private readonly FakeAqApi _api;

    private GameFixture(FakeAqApi api, FakeKeychain keychain, FakeGameHost gameHost, Process engine, EngineConnection connection)
    {
        _api = api;
        Keychain = keychain;
        GameHost = gameHost;
        Engine = engine;
        Connection = connection;
    }

    /// <summary>The servers both the servers API and the game list.</summary>
    public static readonly FakeServer[] Servers =
    [
        new("Artix", Count: 1500, Max: 1500),
        new("Galanoth", Count: 300),
        new("Yorumi", Count: 10, Member: true),
        new("Twig", Count: 50, Online: false),
        new("TestServer", Count: 0),
        new("Sir Ver", Count: 200),
    ];

    public FakeKeychain Keychain { get; }

    public FakeGameHost GameHost { get; }

    public Process Engine { get; }

    public EngineConnection Connection { get; }

    /// <param name="environment">More of the Engine's environment, e.g. a <see cref="FakeGitHub"/>'s.</param>
    public static async Task<GameFixture> StartAsync(
        EngineSandbox sandbox, Func<FakeGameHost, FakeGameHost>? configure = null, IDictionary<string, string>? environment = null)
    {
        FakeAqApi api = new(Servers);
        FakeKeychain keychain = new(sandbox);
        FakeGameHost gameHost = new FakeGameHost(sandbox).Game(keychain, Servers).LogCalls();
        gameHost = configure?.Invoke(gameHost) ?? gameHost;
        Dictionary<string, string> variables = Environment(gameHost, api, keychain);
        foreach ((string key, string value) in environment ?? new Dictionary<string, string>())
            variables[key] = value;
        (Process engine, EngineConnection connection) = await sandbox.StartEngineAsync(variables);
        await connection.WaitForEventAsync(EventTypes.GameState, e => GameEvents.To(e) == "loginScreen");
        return new GameFixture(api, keychain, gameHost, engine, connection);
    }

    public async ValueTask DisposeAsync()
    {
        Connection.Dispose();
        await _api.DisposeAsync();
    }

    /// <summary>The environment that points an Engine at all three fakes.</summary>
    public static Dictionary<string, string> Environment(FakeGameHost gameHost, FakeAqApi api, FakeKeychain keychain) =>
        new[] { gameHost.Environment(), api.Environment(), keychain.Environment() }.SelectMany(e => e).ToDictionary();
}

/// <summary>Reads the <c>game.*</c> events.</summary>
public static class GameEvents
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

public static string? To(LogEntryDto entry) => entry.Data!.Value.GetProperty("to").GetString();

/// <summary>The <c>game.*</c> events so far, as their type or, for <c>game.state</c>, <c>from→to</c>.</summary>
public static async Task<List<LogEntryDto>> AllAsync(EngineConnection connection)
{
    List<LogEntryDto> events = [];
    string? cursor = null;
    while (true)
    {
        LogPage page = await connection.LogsAsync(LogKind.Events, cursor, 1000, Ct);
        events.AddRange(page.Entries.Where(e => e.Type!.StartsWith("game.", StringComparison.Ordinal)));
        cursor = page.Next;
        if (page.Entries.Count == 0)
            return events;
    }
}

public static string Describe(LogEntryDto entry) => entry.Type switch
{
    EventTypes.GameState => $"{entry.Data!.Value.GetProperty("from").GetString()}→{To(entry)}",
    EventTypes.GameDisconnected => $"{entry.Type} {entry.Data!.Value.GetProperty("reason").GetString()}",
    _ => entry.Type!,
};
}
