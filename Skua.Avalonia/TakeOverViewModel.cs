using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Control;

namespace Skua.Avalonia;

/// <summary>Where the take-over stands, which decides what <see cref="TakeOverView"/> offers.</summary>
public enum TakeOverStep
{
    /// <summary>Reading what the Engine holding the name is doing.</summary>
    Looking,

    /// <summary>A <c>skua-engine</c> holds the name: Take over or Quit.</summary>
    Offer,

    /// <summary>It refused to stop while busy: a second Take over stops it anyway.</summary>
    Confirm,

    /// <summary>Stopping it, then starting the app's Engine.</summary>
    TakingOver,

    /// <summary>The app's Engine runs; the host shows the main window.</summary>
    Done,

    /// <summary>It can't be taken over (another Skua app hosts it, or it doesn't answer), or taking over failed: Try again or Quit.</summary>
    Stuck,
}

/// <summary>
/// The Mac App's offer when a <c>skua-engine</c> already holds its Engine Name at launch (ADR 0006): what that Engine is doing (account,
/// map, Script), then Take over or Quit. Taking over stops it with <c>shutdown</c>, waits for its lock, and starts the app's Engine. It
/// never takes over silently, and asks a second time when a Script runs in it.
/// </summary>
/// <remarks>Its properties change on the UI thread only.</remarks>
public sealed partial class TakeOverViewModel : ObservableObject
{
    /// <summary>Long enough for the Engine to stop a Script cooperatively and close its Game Host, as for <c>skua engine stop</c>.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    private readonly EngineEndpoint _endpoint;
    private readonly Func<Task> _startEngine;

    /// <param name="endpoint">The Engine Name the app serves.</param>
    /// <param name="startEngine">Starts the app's Engine once the name is free; it throws what <c>HostedEngine.StartAsync</c> throws.</param>
    public TakeOverViewModel(EngineEndpoint endpoint, Func<Task> startEngine)
    {
        _endpoint = endpoint;
        _startEngine = startEngine;
    }

    public string EngineName => _endpoint.Name;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TakeOverCommand), nameof(LookCommand))]
    [NotifyPropertyChangedFor(nameof(TakeOverText))]
    private TakeOverStep _step = TakeOverStep.Looking;

    /// <summary>What holds the name and what taking over does, or why it can't be taken over.</summary>
    [ObservableProperty]
    private string _message = "";

    /// <summary>What the Engine is doing, one fact per line; empty when that can't be read.</summary>
    [ObservableProperty]
    private string _details = "";

    /// <summary>Raised when the developer chooses Quit.</summary>
    public event Action? QuitRequested;

    public string TakeOverText => Step == TakeOverStep.Confirm ? "Stop it and take over" : "Take over";

    /// <summary>Reads what the Engine holding the name is doing. If none holds it any more, the app starts its Engine at once.</summary>
    [RelayCommand(CanExecute = nameof(CanLook))]
    public async Task LookAsync()
    {
        Step = TakeOverStep.Looking;
        Message = $"Looking at Engine '{EngineName}'…";
        Details = "";
        try
        {
            using EngineConnection? connection = await Task.Run(() => EngineClient.TryConnectAsync(_endpoint));
            if (connection is null)
            {
                if (EngineLock.IsHeld(_endpoint.LockPath))
                    Stuck($"Engine '{EngineName}' holds {_endpoint.LockPath} but doesn't answer: it is starting or hung. Try again in a moment.");
                else
                    await StartAsync();
                return;
            }

            HelloResult hello = connection.Hello;
            if (hello.Host == EngineHost.App)
            {
                Stuck($"Another Skua app (pid {hello.Pid}) runs Engine '{EngineName}', and only quitting it stops it. Use that app, or open this one with --name and another name.");
                return;
            }

            Message = $"Engine '{EngineName}' is already running in skua-engine (pid {hello.Pid}). Take it over to play here: that stops it, so its game closes and any Script stops, and then this app starts its own Engine.";
            Details = await DescribeAsync(connection);
            Step = TakeOverStep.Offer;
        }
        catch (ControlException e)
        {
            Stuck(e.Message);
        }
    }

    private bool CanLook() => Step is TakeOverStep.Stuck;

    /// <summary>
    /// Stops the Engine if it is idle; if it isn't, asks to confirm, and the second call stops it anyway. Then waits for its lock and starts the
    /// app's Engine.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTakeOver))]
    public async Task TakeOverAsync()
    {
        bool confirmed = Step == TakeOverStep.Confirm;
        Step = TakeOverStep.TakingOver;
        Message = $"Stopping Engine '{EngineName}'…";
        try
        {
            using (EngineConnection? connection = await Task.Run(() => EngineClient.TryConnectAsync(_endpoint)))
            {
                if (connection is not null && !await StopAsync(connection, confirmed))
                    return;
            }
            await Task.Run(() => EngineClient.WaitUntilStoppedAsync(_endpoint, StopTimeout));
            await StartAsync();
        }
        catch (ControlException e) when (e.Code == ErrorCode.EngineOwnedByApp)
        {
            // Another Skua app took the name in the meantime.
            await LookAsync();
        }
        catch (ControlException e)
        {
            Stuck(e.Message);
        }
    }

    private bool CanTakeOver() => Step is TakeOverStep.Offer or TakeOverStep.Confirm;

    [RelayCommand]
    private void Quit() => QuitRequested?.Invoke();

    /// <summary>
    /// Asks the Engine to stop: with <c>shutdown_if_idle</c> first, so a busy one is never stopped without a second confirmation; once
    /// confirmed, or for one from before <c>shutdown_if_idle</c>, with <c>shutdown</c>. Returns false when it now waits for that confirmation.
    /// </summary>
    private async Task<bool> StopAsync(EngineConnection connection, bool confirmed)
    {
        try
        {
            if (!confirmed && await Task.Run(() => connection.ShutdownIfIdleAsync()))
                return true;
            await Task.Run(() => connection.ShutdownAsync());
            return true;
        }
        catch (ControlException e) when (e.Code is ErrorCode.ScriptRunning or ErrorCode.Busy)
        {
            Message = e.Code == ErrorCode.ScriptRunning
                ? $"A Script is running in Engine '{EngineName}'. Taking over stops the Script, and closes the game it plays."
                : $"Engine '{EngineName}' is busy with a command, such as a login or a move. Taking over interrupts it, and closes the game.";
            if (connection.IsCompatible)
                Details = await DescribeAsync(connection);
            Step = TakeOverStep.Confirm;
            return false;
        }
        catch (ControlException e) when (e.Code == ErrorCode.EngineUnavailable)
        {
            // The Engine may close the connection before its reply arrives.
            return true;
        }
    }

    private async Task StartAsync()
    {
        Step = TakeOverStep.TakingOver;
        Message = $"Starting Engine '{EngineName}' here…";
        Details = "";
        try
        {
            await _startEngine();
        }
        catch (Exception e)
        {
            Stuck($"Skua's Engine didn't start: {e.Message}");
            return;
        }
        Message = $"Engine '{EngineName}' runs in this app.";
        Step = TakeOverStep.Done;
    }

    /// <summary>The account, map and Script of the Engine, when it speaks this protocol version; otherwise its build, since its status can't be read.</summary>
    private static async Task<string> DescribeAsync(EngineConnection connection)
    {
        HelloResult hello = connection.Hello;
        if (!connection.IsCompatible)
            return $"It is from another build ({hello.Build}, protocol {hello.Protocol}), so what it is doing can't be read.";

        StatusDto status;
        try
        {
            status = await Task.Run(() => connection.StatusAsync());
        }
        catch (ControlException e)
        {
            return $"Its status can't be read: {e.Message}";
        }
        PlayerDto? player = status.Game.Player;
        List<string> lines =
        [
            status.Game.State == GameState.Playing && player is { Name.Length: > 0 }
                ? $"Account: {player.Name}, level {player.Level}, on {status.Game.Server}"
                : "Account: not logged in",
        ];
        if (player?.Map is { Length: > 0 } map)
            lines.Add($"Map: {map}{(player.Cell is { Length: > 0 } cell ? $", {cell}" : "")}");
        lines.Add(status.Script.State switch
        {
            ScriptState.Idle => "Script: none running",
            ScriptState.Running => $"Script: {status.Script.Run?.Script} is running",
            _ => $"Script: {status.Script.Run?.Script ?? "a Script"} is {status.Script.State.ToString().ToLowerInvariant()}",
        });
        if (hello.Build != ControlProtocol.Build)
            lines.Add($"Build: {hello.Build}, not this app's {ControlProtocol.Build}");
        return string.Join('\n', lines);
    }

    private void Stuck(string message)
    {
        Message = message;
        Details = "";
        Step = TakeOverStep.Stuck;
    }
}
