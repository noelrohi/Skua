using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Control;

namespace Skua.Avalonia;

/// <summary>
/// The status strip and the login controls of the Mac App's main window, over the Engine the app hosts: what <c>status</c> reports, read
/// again each time the Engine says it may have changed, and <c>servers</c>, <c>login</c> and <c>logout</c> called in-process, as <c>skua</c> calls them.
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
    [NotifyCanExecuteChangedFor(nameof(LogOutCommand))]
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

    /// <summary>The servers to pick from: <see cref="ServerChoice.Any"/> first, then the game's servers as <c>servers</c> lists them.</summary>
    public ObservableCollection<ServerChoice> Servers { get; } = [ServerChoice.Any];

    [ObservableProperty]
    private ServerChoice _selectedServer = ServerChoice.Any;

    /// <summary>Whether a login or a logout from this window is in flight, which disables both.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LogInCommand), nameof(LogOutCommand))]
    private bool _busy;

    /// <summary>What the last login, logout or server list did: who it logged in as, or why it failed.</summary>
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

    /// <summary>Lists the servers again, keeping the pick if the server is still listed.</summary>
    [RelayCommand]
    private async Task LoadServersAsync()
    {
        try
        {
            ServersResult result = await Task.Run(() => _engine.ServersAsync(CancellationToken.None));
            string? picked = SelectedServer.Name;
            Servers.Clear();
            Servers.Add(ServerChoice.Any);
            foreach (ServerDto server in result.Servers)
                Servers.Add(new ServerChoice(server.Name, Label(server)));
            SelectedServer = Servers.FirstOrDefault(s => s.Name == picked) ?? ServerChoice.Any;
        }
        catch (Exception e)
        {
            Show(e.Message, error: true);
        }
    }

    /// <summary>Logs in with the Active Account on the picked server, or on one the Engine picks, as <c>skua login</c> does.</summary>
    [RelayCommand(CanExecute = nameof(CanLogIn))]
    private async Task LogInAsync()
    {
        Busy = true;
        Show(null, error: false);
        try
        {
            string? server = SelectedServer.Name;
            LoginResult result = await Task.Run(() => _engine.LoginAsync(server, null, asAgent: false, CancellationToken.None));
            Show($"{(result.AlreadyLoggedIn ? "Already playing" : "Logged in")} as {result.Username}{(result.IsTestAccount ? " (the Test Account)" : "")} on {result.Server}.", error: false);
        }
        catch (Exception e)
        {
            Show(e.Message, error: true);
        }
        finally
        {
            Busy = false;
        }
    }

    private bool CanLogIn() => !Busy;

    [RelayCommand(CanExecute = nameof(CanLogOut))]
    private async Task LogOutAsync()
    {
        Busy = true;
        Show(null, error: false);
        try
        {
            LogoutResult result = await Task.Run(() => _engine.LogoutAsync(CancellationToken.None));
            Show(result.WasLoggedIn ? "Logged out." : "Not logged in.", error: false);
        }
        catch (Exception e)
        {
            Show(e.Message, error: true);
        }
        finally
        {
            Busy = false;
        }
    }

    private bool CanLogOut() => !Busy && GameState is GameState.LoggingIn or GameState.Playing or GameState.Disconnected;

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

    private static string Label(ServerDto server) =>
        !server.Online ? $"{server.Name} (offline)"
        : $"{server.Name} ({server.PlayerCount}/{server.MaxPlayers}{(server.MemberOnly ? ", members" : "")})";
}

/// <summary>A server in the picker; <see cref="Name"/> is null for <see cref="Any"/>, which lets the Engine pick one.</summary>
public sealed record ServerChoice(string? Name, string Label)
{
    public static readonly ServerChoice Any = new(null, "Any server");

    public override string ToString() => Label;
}
