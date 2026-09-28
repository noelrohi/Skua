using Skua.Engine.Logging;
using Skua.Control;
using Skua.Core.Interfaces;

namespace Skua.Engine.Game;

/// <summary>
/// The one source of the game state: <c>status</c> reads it and it emits the <c>game.state</c> and <c>game.disconnected</c> events,
/// so the two never disagree. It holds the power assertion while the Test Account is logged in.
/// </summary>
/// <remarks>
/// Edges (the Game Host starting and exiting, the Game Client loading, login responses, logouts and the start and end of logins,
/// logouts and relogins) apply at once; a 500 ms poll of the game fills in the rest, and a state it finds must hold for two polls,
/// so a momentary reading never flickers the state. The Bridge is only called outside the lock, and a reading taken before a
/// state change is read again.
/// </remarks>
internal sealed class GameStateTracker : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The game's connection messages once the connection has gone, as Core's timer recognises them.</summary>
    private static readonly string[] LostMessages = ["has been lost", "restart", "maintenance"];

    private const int MaxRereads = 3;

    private readonly object _lock = new();
    private readonly IFlashUtil _flash;
    private readonly EngineLogs _logs;
    private readonly PowerAssertion _power;
    private readonly CancellationTokenSource _stop = new();

    private GameState _state = GameState.NotStarted;
    private GameState? _pending;
    private string? _server;
    private bool _hostUp;
    private bool _loaded;
    private bool _loginInFlight;
    private bool _reloginInFlight;
    private bool _logoutInFlight;

    /// <summary>Whether the Test Account is logged in, so losing that is a disconnect.</summary>
    private bool _loggedIn;

    /// <summary>Whether the last login ended deliberately (or there was none yet), which makes a logged-out game the login screen.</summary>
    private bool _lastExitDeliberate = true;

    private string? _refusal;

    /// <summary>Counts commits and disconnects, so a poll that read the game before one is dropped and an edge reads again.</summary>
    private long _generation;

    public GameStateTracker(IFlashUtil flash, EngineLogs logs, PowerAssertion power)
    {
        _flash = flash;
        _logs = logs;
        _power = power;
        new Thread(Poll) { IsBackground = true, Name = "Game state poll" }.Start();
    }

    /// <summary>Raised outside the lock each time the state becomes <see cref="GameState.Playing"/>.</summary>
    public event Action? Playing;

    public GameState State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    /// <summary>The server while playing, else null.</summary>
    public string? Server
    {
        get
        {
            lock (_lock)
                return _server;
        }
    }

    /// <summary>Whether the Game Client has loaded in a running Game Host, so the game can be driven.</summary>
    public bool Ready
    {
        get
        {
            lock (_lock)
                return _hostUp && _loaded;
        }
    }

    /// <summary>Waits until <see cref="Ready"/>, while the Game Host runs and the Game Client is still loading, up to <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime until = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (_lock)
            {
                if (_hostUp && _loaded)
                    return true;
                if (!_hostUp || DateTime.UtcNow > until)
                    return false;
            }
            await Task.Delay(100, cancellationToken);
        }
    }

    public void GameHostStarted()
    {
        lock (_lock)
        {
            _hostUp = true;
            _loaded = false;
        }
    }

    /// <summary>Forces <see cref="GameState.NotStarted"/>, and a logged-in Test Account is disconnected with <c>gameHostExited</c>.</summary>
    public void GameHostExited()
    {
        lock (_lock)
        {
            _hostUp = false;
            _loaded = false;
            if (_loggedIn)
                Disconnect(DisconnectReason.GameHostExited, null);
            Commit(GameState.NotStarted);
        }
    }

    public void Loaded()
    {
        _logs.Event(EventTypes.GameLoaded, new { });
        lock (_lock)
            _loaded = true;
        Evaluate(edge: true);
    }

    /// <summary>The game server accepted the login, or refused it with a message.</summary>
    public void LoginResponse(bool accepted, string? message)
    {
        if (!accepted)
        {
            lock (_lock)
                _refusal = message is { Length: > 0 } ? message : "no reason given";
            return;
        }
        Evaluate(edge: true, loginAccepted: true);
    }

    /// <summary>The reason the game server refused a login since the last call, if it did.</summary>
    public string? TakeRefusal()
    {
        lock (_lock)
        {
            string? refusal = _refusal;
            _refusal = null;
            return refusal;
        }
    }

    /// <summary>The player logged out in the game, e.g. with the logout button.</summary>
    public void LoggedOutInGame()
    {
        lock (_lock)
        {
            if (_loggedIn && !_loginInFlight && !_reloginInFlight)
                Disconnect(DisconnectReason.Logout, null);
        }
        Evaluate(edge: true);
    }

    public void LoginStarted()
    {
        lock (_lock)
        {
            // A login replaces the one before, so its own logout isn't a disconnect; it also supersedes an auto-relogin that never finished.
            _loggedIn = false;
            _loginInFlight = true;
            _reloginInFlight = false;
            _refusal = null;
        }
        Evaluate(edge: true);
    }

    public void LoginFinished()
    {
        lock (_lock)
            _loginInFlight = false;
        Evaluate(edge: true);
    }

    public void LogoutStarted()
    {
        lock (_lock)
        {
            _logoutInFlight = true;
            _reloginInFlight = false;
        }
    }

    /// <summary>Ends the login deliberately and returns to the login screen.</summary>
    public void LogoutFinished()
    {
        lock (_lock)
        {
            _logoutInFlight = false;
            if (_loggedIn)
                Disconnect(DisconnectReason.Logout, null);
            _lastExitDeliberate = true;
            if (_hostUp && _loaded)
                Commit(GameState.LoginScreen);
        }
    }

    /// <summary>
    /// Core's auto-relogin is about to start: it runs after Core saw the connection go and before its own logout, so the game still
    /// shows why. Disconnects the Test Account, if the poll hasn't yet.
    /// </summary>
    public void ReloginTriggered()
    {
        Sample sample = Read(needServer: false);
        lock (_lock)
        {
            if (!_loggedIn)
                return;
            (DisconnectReason reason, string? detail) = Why(sample);
            Disconnect(reason, detail);
            Commit(After(reason));
        }
    }

    public void ReloginStarted()
    {
        lock (_lock)
            _reloginInFlight = true;
        Evaluate(edge: true);
    }

    public void ReloginFinished()
    {
        lock (_lock)
            _reloginInFlight = false;
        Evaluate(edge: true);
    }

    /// <summary>Whether the game is playing now: logged in with the world loaded. Reads the game, so it is current.</summary>
    public bool IsPlaying() => Read(needServer: false) is { LoggedIn: true, World: true };

    /// <summary>The game's connection message, or null when it shows none.</summary>
    public string? ConnectionMessage() => Read(needServer: false).Message;

    /// <summary>Whether a connection message says the connection has gone.</summary>
    public static bool IsConnectionLost(string? message) =>
        message is not null && LostMessages.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        _stop.Cancel();
        _power.Dispose();
    }

    private void Poll()
    {
        while (!_stop.Token.WaitHandle.WaitOne(PollInterval))
        {
            try
            {
                Evaluate(edge: false);
            }
            catch (Exception e)
            {
                EngineLog.Write($"The game state poll failed: {e}");
            }
        }
    }

    /// <summary>
    /// Reads the game, then applies what it found; a poll's finding must hold twice, an edge's applies at once. A poll whose reading
    /// an edge overtook is dropped, and an edge reads again.
    /// </summary>
    /// <param name="loginAccepted">The edge is an accepted login response, which logs the Test Account in if the game is still connected.</param>
    private void Evaluate(bool edge, bool loginAccepted = false)
    {
        bool playing = false;
        for (int attempt = 0; attempt < MaxRereads; attempt++)
        {
            long generation;
            bool ready;
            bool needServer;
            lock (_lock)
            {
                generation = _generation;
                ready = _hostUp && _loaded;
                needServer = _state != GameState.Playing;
            }
            Sample? sample = ready ? Read(needServer) : null;

            lock (_lock)
            {
                if (generation != _generation)
                {
                    if (edge)
                        continue;
                    return;
                }
                playing = Apply(sample, edge, loginAccepted);
            }
            break;
        }
        if (playing)
            Playing?.Invoke();
    }

    /// <summary>Applies a reading under the lock; returns whether the state became <see cref="GameState.Playing"/>.</summary>
    private bool Apply(Sample? sample, bool edge, bool loginAccepted)
    {
        if (!_hostUp || !_loaded)
        {
            Commit(GameState.NotStarted);
            return false;
        }
        if (sample is null)
            return false;
        // Core handles each game call first, so a response can reach the tracker after the connection it came on has gone:
        // that login is over, and logging it in again would make its loss a second disconnect.
        if (loginAccepted && sample.LoggedIn && !IsConnectionLost(sample.Message))
            _loggedIn = true;

        (GameState next, DisconnectReason? loss, string? detail) = Derive(sample);
        if (next == _state)
        {
            _pending = null;
            return false;
        }
        // The connection message is the game saying it lost the connection, which is as good as an edge.
        if (!edge && loss != DisconnectReason.ConnectionLost && _pending != next)
        {
            _pending = next;
            return false;
        }
        if (loss is { } reason)
            Disconnect(reason, detail);
        if (next == GameState.Playing)
        {
            _loggedIn = true;
            _server = sample.Server;
        }
        Commit(next);
        return next == GameState.Playing;
    }

    /// <summary>The state the sample shows, and whether it means the Test Account was disconnected and why.</summary>
    private (GameState State, DisconnectReason? Loss, string? Detail) Derive(Sample sample)
    {
        if (_loginInFlight || _reloginInFlight)
            return (GameState.LoggingIn, null, null);
        bool lost = IsConnectionLost(sample.Message);
        if (sample is { LoggedIn: true, World: true } && !lost)
            return (GameState.Playing, null, null);
        if (_logoutInFlight)
            return (_state, null, null);
        if (_loggedIn && (lost || !sample.LoggedIn))
        {
            (DisconnectReason reason, string? detail) = Why(sample);
            return (After(reason), reason, detail);
        }
        // The game may still say it is connected while it shows why it isn't.
        if (lost)
            return (GameState.Disconnected, null, null);
        if (sample.LoggedIn)
            return (GameState.LoggingIn, null, null);
        return (_lastExitDeliberate ? GameState.LoginScreen : GameState.Disconnected, null, null);
    }

    /// <summary>Why the Test Account was disconnected while the Game Host is up: the connection message, else a kick, else a logout.</summary>
    private static (DisconnectReason Reason, string? Detail) Why(Sample sample)
    {
        if (IsConnectionLost(sample.Message))
            return (DisconnectReason.ConnectionLost, sample.Message);
        return sample.Kicked ? (DisconnectReason.Kicked, null) : (DisconnectReason.Logout, null);
    }

    /// <summary>A deliberate logout returns to the login screen; any other loss leaves the game disconnected.</summary>
    private static GameState After(DisconnectReason reason) => reason == DisconnectReason.Logout ? GameState.LoginScreen : GameState.Disconnected;

    private void Disconnect(DisconnectReason reason, string? detail)
    {
        _generation++;
        _loggedIn = false;
        _lastExitDeliberate = reason == DisconnectReason.Logout;
        _logs.Event(EventTypes.GameDisconnected, detail is null ? new { reason } : new { reason, detail });
    }

    private void Commit(GameState next)
    {
        _pending = null;
        if (next == _state)
            return;
        _generation++;
        _logs.Event(EventTypes.GameState, new { from = _state, to = next });
        _state = next;
        if (next != GameState.Playing)
            _server = null;
        _power.Hold(next is GameState.LoggingIn or GameState.Playing);
    }

    /// <summary>Reads the game over the Bridge; the server only when it is needed, since it isn't set before a login.</summary>
    private Sample Read(bool needServer)
    {
        bool loggedIn = _flash.Call<bool>("isLoggedIn");
        bool world = !_flash.IsNull("world");
        string? message = _flash.IsNull("mcConnDetail.stage") ? null : _flash.GetGameObject<string>("mcConnDetail.txtDetail.text");
        bool kicked = !loggedIn && _flash.Call<bool>("isKicked");
        string? server = loggedIn && world && needServer && !_flash.IsNull("objServerInfo") ? _flash.GetGameObject<string>("objServerInfo.sName") : null;
        return new Sample(loggedIn, world, message, kicked, server);
    }

    private sealed record Sample(bool LoggedIn, bool World, string? Message, bool Kicked, string? Server);
}

/// <summary>Why the Test Account was disconnected, as <c>game.disconnected</c> reports it.</summary>
internal enum DisconnectReason
{
    GameHostExited,
    ConnectionLost,
    Kicked,
    Logout,
}
