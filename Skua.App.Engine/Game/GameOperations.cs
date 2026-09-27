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

    /// <summary>The Keychain service of the account the game last logged in with, and its username.</summary>
    private string? _loggedInService;

    private string? _loggedInUsername;

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

    /// <param name="asAgent">Whether an agent asks, whose login uses the Test Account unless the active account allows agents.</param>
    public async Task<LoginResult> LoginAsync(string? serverName, int? timeoutSec, bool asAgent, CancellationToken cancellationToken)
    {
        if (timeoutSec < 1)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"timeoutSec must be at least 1, not {timeoutSec}.");
        TimeSpan timeout = timeoutSec is { } seconds ? TimeSpan.FromSeconds(seconds) : DefaultLoginTimeout;

        // Straight after an Engine start, the Game Client may still be loading.
        await _tracker.WaitReadyAsync(LoadWait, cancellationToken);
        using IDisposable lease = await _slot.BeginAsync("log in");
        // Playing with another account than the one to use relogs, since 'skua account' may have switched it.
        string service = await ServiceAsync(asAgent, cancellationToken);
        bool sameAccount = service == _loggedInService;
        if (serverName is null && sameAccount && _tracker is { State: GameState.Playing, Server: { } current })
            return new LoginResult(current, AlreadyLoggedIn: true, _loggedInUsername!);

        Server server = Choose(await FetchServersAsync(), serverName);
        if (sameAccount && _tracker is { State: GameState.Playing, Server: { } playing } && string.Equals(playing, server.Name, StringComparison.OrdinalIgnoreCase))
            return new LoginResult(playing, AlreadyLoggedIn: true, _loggedInUsername!);

        TestAccount account = await ReadAccountAsync(service, cancellationToken);
        _tracker.LoginStarted();
        try
        {
            LoginResult result = await LogInAsync(account, server, timeout, cancellationToken);
            (_loggedInService, _loggedInUsername) = (service, account.Username);
            return result;
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
            return new LoginResult(await WaitForWorldAsync(server, relogin, before, timeout, cancellationToken), AlreadyLoggedIn: false, account.Username);
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

    /// <returns>The server the game plays on.</returns>
    private async Task<string> WaitForWorldAsync(Server server, Task relogin, string? before, TimeSpan timeout, CancellationToken cancellationToken)
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
                    $"The account wasn't playing on {server.Name} after {timeout.TotalSeconds:0} s; the game shows {(message is null ? "no connection message" : $"'{message}'")}.");
            await Task.Delay(WaitStep, cancellationToken);
        }
        // The inventory arrives a moment after the world; until then the game has no items and refuses map transfers
        // ("Character Inventory is being loaded").
        while (!_flash.GetGameObject<bool>("world.myAvatar.invLoaded"))
        {
            if (!_tracker.IsPlaying())
                throw RpcErrors.Of(ErrorCode.LoginFailed, $"The account stopped playing on {server.Name} before its inventory loaded.");
            if (waited.Elapsed > timeout)
                throw RpcErrors.Of(ErrorCode.Timeout, $"The account was playing on {server.Name}, but its inventory hadn't loaded after {timeout.TotalSeconds:0} s.");
            await Task.Delay(WaitStep, cancellationToken);
        }
        string actual = !_flash.IsNull("objServerInfo") && _flash.GetGameObject<string>("objServerInfo.sName") is { Length: > 0 } name ? name : server.Name;
        return actual;
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
    /// The Keychain service of the account to log in: the active one, except that an agent gets the Test Account unless the active account's
    /// Keychain item carries <see cref="AccountSetting.AllowAgentsComment"/>. The comment is read without the password, so without asking macOS.
    /// </summary>
    private async Task<string> ServiceAsync(bool asAgent, CancellationToken cancellationToken)
    {
        string active = _settings.Get<string>(TestAccount.ServiceSetting)!;
        if (!asAgent || active == AccountSetting.DefaultService)
            return active;
        KeychainAttributes? attributes;
        try
        {
            attributes = await Keychain.FindAsync(active, cancellationToken);
        }
        catch (ControlException e) when (e.Code == ErrorCode.KeychainFailed)
        {
            EngineLog.Write($"Couldn't read whether the active account allows agents, so an agent's login uses the Test Account: {e.Message}");
            return AccountSetting.DefaultService;
        }
        return attributes?.Comment == AccountSetting.AllowAgentsComment ? active : AccountSetting.DefaultService;
    }

    /// <summary>
    /// Reads the account from Keychain at every login, since 'skua account' may have changed it, and registers its password as a secret before
    /// anything can log it, also as it appears in Bridge XML and in JSON.
    /// </summary>
    private async Task<TestAccount> ReadAccountAsync(string service, CancellationToken cancellationToken)
    {
        TestAccount account = await TestAccount.ReadAsync(service, cancellationToken);
        _logs.AddSecret(account.Password);
        _logs.AddSecret(SecurityElement.Escape(account.Password));
        _logs.AddSecret(JsonEncodedText.Encode(account.Password, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString());
        _logs.AddSecret(JsonEncodedText.Encode(account.Password).ToString());
        return account;
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
