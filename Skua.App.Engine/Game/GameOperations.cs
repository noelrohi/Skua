using System.Diagnostics;
using System.Security;
using System.Text.Encodings.Web;
using System.Text.Json;
using Skua.App.Engine.Logging;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models.Servers;

namespace Skua.App.Engine.Game;

/// <summary><c>servers</c>, <c>login</c> and <c>logout</c>: the Test Account's login, through Core's own login cycle.</summary>
internal sealed class GameOperations
{
    public static readonly TimeSpan DefaultLoginTimeout = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan LoadWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitStep = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Connection messages, besides a lost connection, that mean the game refused the login, as opposed to its progress messages
    /// ("Connecting to game server…", "Loading Map… 15%").
    /// </summary>
    private static readonly string[] RefusalMessages = ["full", "try another", "failed", "invalid", "banned"];

    private readonly IScriptServers _servers;
    private readonly IFlashUtil _flash;
    private readonly ISettingsService _settings;
    private readonly EngineLogs _logs;
    private readonly GameStateTracker _tracker;
    private readonly GameActionSlot _slot;
    private TestAccount? _account;

    public GameOperations(IScriptServers servers, IFlashUtil flash, ISettingsService settings, EngineLogs logs, GameStateTracker tracker, GameActionSlot slot)
    {
        _servers = servers;
        _flash = flash;
        _settings = settings;
        _logs = logs;
        _tracker = tracker;
        _slot = slot;
    }

    public async Task<ServersResult> ServersAsync()
    {
        List<Server> servers = await FetchServersAsync();
        return new ServersResult(servers.Select(ToDto).ToList());
    }

    public async Task<LoginResult> LoginAsync(string? serverName, int? timeoutSec, CancellationToken cancellationToken)
    {
        if (timeoutSec < 1)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"timeoutSec must be at least 1, not {timeoutSec}.");
        TimeSpan timeout = timeoutSec is { } seconds ? TimeSpan.FromSeconds(seconds) : DefaultLoginTimeout;

        // Straight after an Engine start, the Game Client may still be loading.
        await _tracker.WaitReadyAsync(LoadWait, cancellationToken);
        using IDisposable lease = await _slot.BeginAsync("log in");
        if (serverName is null && _tracker is { State: GameState.Playing, Server: { } current })
            return new LoginResult(current, AlreadyLoggedIn: true);

        Server server = Choose(await FetchServersAsync(), serverName);
        if (_tracker is { State: GameState.Playing, Server: { } playing } && string.Equals(playing, server.Name, StringComparison.OrdinalIgnoreCase))
            return new LoginResult(playing, AlreadyLoggedIn: true);

        TestAccount account = await ReadAccountAsync(cancellationToken);
        _tracker.LoginStarted();
        try
        {
            return await LogInAsync(account, server, timeout, cancellationToken);
        }
        finally
        {
            _tracker.LoginFinished();
        }
    }

    public async Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken)
    {
        using IDisposable lease = await _slot.BeginAsync("log out");
        GameState state = _tracker.State;
        if (state == GameState.LoginScreen)
            return new LogoutResult(WasLoggedIn: false);

        _tracker.LogoutStarted();
        try
        {
            await Task.Run(_servers.Logout, cancellationToken);
            Stopwatch waited = Stopwatch.StartNew();
            while (_servers.IsConnected && waited.Elapsed < LogoutTimeout)
                await Task.Delay(WaitStep, cancellationToken);
        }
        finally
        {
            _tracker.LogoutFinished();
        }
        return new LogoutResult(WasLoggedIn: state is GameState.Playing or GameState.LoggingIn);
    }

    /// <summary>
    /// Runs Core's <c>Relogin(name)</c>, which sends the whole server to the game (its IP-only path lands on the wrong server),
    /// then waits itself, since Core waits only 3 s for the world.
    /// </summary>
    private async Task<LoginResult> LogInAsync(TestAccount account, Server server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string? before = _tracker.ConnectionMessage();
        _servers.SetLoginInfo(account.Username, account.Password);
        Task relogin = Task.Factory.StartNew(() => _servers.Relogin(server.Name), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            return await WaitForWorldAsync(server, relogin, before, timeout, cancellationToken);
        }
        finally
        {
            // Core's relogin ends within seconds of connecting; the next login or logout mustn't overlap it.
            try
            {
                await relogin;
            }
            catch (Exception e)
            {
                EngineLog.Write($"Core's relogin failed: {e}");
            }
        }
    }

    private async Task<LoginResult> WaitForWorldAsync(Server server, Task relogin, string? before, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Stopwatch waited = Stopwatch.StartNew();
        // A relogin from another server is playing until Core has logged out.
        bool left = false;
        while (true)
        {
            bool playing = _tracker.IsPlaying();
            if (playing && (left || relogin.IsCompleted))
                break;
            left |= !playing;
            if (!_tracker.Ready)
                throw RpcErrors.Of(ErrorCode.GameHostDown, "The Game Host went down during the login.");
            if (relogin.IsFaulted)
                throw RpcErrors.Of(ErrorCode.LoginFailed, $"The login on {server.Name} failed: {relogin.Exception!.InnerException!.Message}");
            if (_tracker.TakeRefusal() is { } refusal)
                throw RpcErrors.Of(ErrorCode.LoginFailed, $"The game refused the login on {server.Name}: {refusal}");
            // Until Core's relogin has logged out and connected, the message may be the previous login's.
            string? message = _tracker.ConnectionMessage();
            if (relogin.IsCompleted && message is not null && message != before && IsRefusal(message))
                throw RpcErrors.Of(ErrorCode.LoginFailed, $"The login on {server.Name} failed: {message}");
            if (waited.Elapsed > timeout)
                throw RpcErrors.Of(ErrorCode.Timeout,
                    $"The Test Account wasn't playing on {server.Name} after {timeout.TotalSeconds:0} s; the game shows {(message is null ? "no connection message" : $"'{message}'")}.");
            await Task.Delay(WaitStep, cancellationToken);
        }
        string actual = !_flash.IsNull("objServerInfo") && _flash.GetGameObject<string>("objServerInfo.sName") is { Length: > 0 } name ? name : server.Name;
        return new LoginResult(actual, AlreadyLoggedIn: false);
    }

    /// <summary>The named server, or else an online, non-member server with room, the emptiest first.</summary>
    private static Server Choose(List<Server> servers, string? name)
    {
        if (name is null)
        {
            return servers
                .Where(s => s.Online && !s.Upgrade && !IsFull(s) && !IsTest(s))
                .OrderByDescending(s => s.MaxPlayers - s.PlayerCount)
                .FirstOrDefault()
                ?? throw RpcErrors.Of(ErrorCode.LoginFailed, "No online, non-member server has room; see 'skua servers'.");
        }

        Server server = servers.Find(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw RpcErrors.Of(ErrorCode.InvalidArgument, $"No server is named '{name}'; 'skua servers' lists them.");
        if (!server.Online)
            throw RpcErrors.Of(ErrorCode.LoginFailed, $"{server.Name} is offline; pick another server.");
        if (IsFull(server))
            throw RpcErrors.Of(ErrorCode.LoginFailed, $"{server.Name} is full ({server.PlayerCount}/{server.MaxPlayers}); pick another server.");
        if (IsTest(server))
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"{server.Name} is a test server, which Core never logs in to; pick another server.");
        return server;
    }

    private static bool IsFull(Server server) => server.MaxPlayers > 0 && server.PlayerCount >= server.MaxPlayers;

    // Core's relogin skips these, and would swap in another server.
    private static bool IsTest(Server server) => server.Name.Contains("Test", StringComparison.OrdinalIgnoreCase);

    private static bool IsRefusal(string message) =>
        GameStateTracker.IsConnectionLost(message) || RefusalMessages.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the Test Account from Keychain once per Engine, and registers its password as a secret before anything can log it,
    /// also as it appears in Bridge XML and in JSON.
    /// </summary>
    private async Task<TestAccount> ReadAccountAsync(CancellationToken cancellationToken)
    {
        if (_account is not null)
            return _account;
        string service = _settings.Get<string>(TestAccount.ServiceSetting)!;
        TestAccount account = await TestAccount.ReadAsync(service, cancellationToken);
        _logs.AddSecret(account.Password);
        _logs.AddSecret(SecurityElement.Escape(account.Password));
        _logs.AddSecret(JsonEncodedText.Encode(account.Password, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString());
        _logs.AddSecret(JsonEncodedText.Encode(account.Password).ToString());
        return _account = account;
    }

    /// <exception cref="StreamJsonRpc.LocalRpcException"><see cref="ErrorCode.ServersUnavailable"/>.</exception>
    private async Task<List<Server>> FetchServersAsync()
    {
        // Core swallows the failure and returns no servers; the game always has some.
        List<Server> servers = await _servers.GetServers(forceUpdate: true);
        if (servers.Count == 0)
            throw RpcErrors.Of(ErrorCode.ServersUnavailable, "The game's servers API couldn't be reached or listed no servers; try again shortly.");
        return servers;
    }

    private static ServerDto ToDto(Server server) =>
        new(server.Name, server.Online, server.PlayerCount, server.MaxPlayers, server.Upgrade, server.Lang);
}
