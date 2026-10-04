using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Skua.Control;

namespace Skua.Avalonia;

/// <summary>
/// The status strip of the Mac App's main window and the Skua Manager's launch, over the Engine the app hosts: what <c>status</c> reports,
/// read again each time the Engine says it may have changed, and <c>login</c> and <c>script start</c> called in-process, as <c>skua</c> calls them.
/// </summary>
/// <remarks>
/// Its properties change on the UI thread only. The Engine's calls run on the thread pool, since some of them call the Game Client and wait.
/// </remarks>
public sealed partial class StatusViewModel : ObservableObject
{
    private readonly IEngineRpc _engine;
    private int _changed;
    private bool _refreshing;

    /// <param name="engine">The Engine's operations.</param>
    /// <param name="engineName">The Engine Name it serves.</param>
    /// <param name="host">Who hosts it, as <c>status</c> will name it: <c>app</c> in the Mac App.</param>
    public StatusViewModel(IEngineRpc engine, string engineName, string host)
    {
        _engine = engine;
        EngineName = engineName;
        Host = host;
        Changed();
    }

    public string EngineName { get; }

    public string Host { get; }

    /// <summary>The game state, as <c>status</c> reports it; <see cref="GameStateText"/> is how the strip shows it.</summary>
    [ObservableProperty]
    private GameState _gameState = GameState.NotStarted;

    /// <summary>The character the game plays, or null when not logged in.</summary>
    [ObservableProperty]
    private string? _account;

    [ObservableProperty]
    private string? _server;

    [ObservableProperty]
    private string? _map;

    [ObservableProperty]
    private string? _cell;

    [ObservableProperty]
    private int? _level;

    /// <summary>The running Script, or null when none is.</summary>
    [ObservableProperty]
    private string? _script;

    /// <summary>What the launch did: who it logged in as and the Script it started, or why it failed.</summary>
    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    public string GameStateText => GameState switch
    {
        GameState.NotStarted => "no game",
        GameState.LoginScreen => "login screen",
        GameState.LoggingIn => "logging in",
        GameState.Playing => "logged in",
        GameState.Disconnected => "disconnected",
        _ => GameState.ToString(),
    };

    partial void OnGameStateChanged(GameState value) => OnPropertyChanged(nameof(GameStateText));

    /// <summary>
    /// Reads the status again soon. Call it from any thread, as often as the Engine likes: the reads never overlap, and changes that come
    /// during one are covered by one more.
    /// </summary>
    public void Changed()
    {
        if (Interlocked.Exchange(ref _changed, 1) == 0)
            Dispatcher.UIThread.Post(() => _ = RefreshAsync());
    }

    /// <summary>Logs in on <paramref name="server"/>, or on one the Engine picks, showing the outcome; returns whether it is playing.</summary>
    private async Task<bool> LogInAsync(string? server)
    {
        Show(null, error: false);
        try
        {
            LoginResult result = await Task.Run(() => _engine.LoginAsync(server, null, asAgent: false, account: null, CancellationToken.None));
            Show($"{(result.AlreadyLoggedIn ? "Already playing" : "Logged in")} as {result.Username}{(result.IsTestAccount ? " (the Test Account)" : "")} on {result.Server}.", error: false);
            return true;
        }
        catch (Exception e)
        {
            Show(e.Message, error: true);
            return false;
        }
    }

    /// <summary>
    /// What an app the Skua Manager launched does once its window shows: logs its account in, on <paramref name="server"/> or one the Engine
    /// picks, then starts <paramref name="script"/> if given, as <c>skua script start</c> does.
    /// </summary>
    public async Task LaunchAsync(string? server, string? script)
    {
        if (!await LogInAsync(server) || script is null)
            return;
        try
        {
            ScriptStartResult started = await Task.Run(() => _engine.ScriptStartAsync(script, cancellationToken: CancellationToken.None));
            Show($"{Message} Started {Path.GetFileName(started.Status.Run?.Script ?? script)}.", error: false);
        }
        catch (Exception e)
        {
            Show($"Couldn't start {Path.GetFileName(script)}: {e.Message}", error: true);
        }
    }

    private async Task RefreshAsync()
    {
        // The read in flight goes again when it sees the flag this call was posted for.
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            while (Interlocked.Exchange(ref _changed, 0) == 1)
                Apply(await Task.Run(() => _engine.StatusAsync(CancellationToken.None)));
        }
        catch (Exception)
        {
            // status never fails while the Engine runs; one that has stopped takes the window with it.
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Apply(StatusDto status)
    {
        GameState = status.Game.State;
        Server = status.Game.Server;
        PlayerDto? player = status.Game.Player;
        Account = player?.Name is { Length: > 0 } name ? name : null;
        Map = player?.Map;
        Cell = player?.Cell;
        Level = player?.Level;
        Script = status.Script.State is ScriptState.Idle ? null : status.Script.Run?.Script ?? status.Script.State.ToString();
    }

    private void Show(string? message, bool error)
    {
        Message = message;
        MessageIsError = error && message is not null;
    }
}
