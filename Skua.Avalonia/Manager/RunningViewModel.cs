using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Control;
using Skua.Core.Interfaces;

namespace Skua.Avalonia.Manager;

/// <summary>One running Engine in the Running tab.</summary>
public sealed partial class RunningItemViewModel : ObservableObject
{
    public RunningItemViewModel(RunningEngine engine, string? account)
    {
        Engine = engine;
        Account = account;
        Update(engine);
    }

    public RunningEngine Engine { get; private set; }

    public string Name => Engine.Name;

    /// <summary>The Manager's display name of the account it was launched for, or null for another Engine.</summary>
    public string? Account { get; }

    public string Host => Engine.IsApp ? "App" : "Headless";

    public bool CanShow => Engine.IsApp;

    [ObservableProperty]
    private string _game = "";

    [ObservableProperty]
    private string _script = "";

    public void Update(RunningEngine engine)
    {
        Engine = engine;
        Game = engine.Status?.Game switch
        {
            null => engine.Hello.Protocol == ControlProtocol.Version ? "Not answering" : $"Another build ({engine.Hello.Build})",
            { GameHostUp: false } => "Game Host down",
            { State: GameState.Playing, Server: var server, Player: var player } => $"Playing on {server}{(player is null ? "" : $" as {player.Name}, level {player.Level}, in {player.Map}")}",
            { State: var state } => state.ToString(),
        };
        Script = engine.Status?.Script.Run?.Script is { } running ? Path.GetFileName(running) : "";
    }
}

/// <summary>
/// The Running tab: every Engine under the data folder, apps and headless ones alike, refreshed every few seconds. An app can be brought to the
/// front; any can be stopped, which asks first while a Script runs in it.
/// </summary>
public sealed partial class RunningViewModel : ObservableObject, IDisposable
{
    public static TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(2);

    private readonly AppInstances _instances;
    private readonly ManagerAccounts _store;
    private readonly IDialogService _dialogs;
    private readonly IDispatcherService _dispatcher;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    public RunningViewModel(AppInstances instances, ManagerAccounts store, IDialogService dialogs, IDispatcherService dispatcher)
    {
        _instances = instances;
        _store = store;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
    }

    public ObservableCollection<RunningItemViewModel> Engines { get; } = [];

    [ObservableProperty]
    private string _status = "";

    /// <summary>Refreshes the list until disposed.</summary>
    public async Task WatchAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await RefreshAsync();
                await Task.Delay(RefreshInterval, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await _refreshing.WaitAsync();
        try
        {
            IReadOnlyList<RunningEngine> engines = await _instances.ListAsync(_stop.Token);
            _dispatcher.Invoke(() => Show(engines));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _refreshing.Release();
        }
    }

    [RelayCommand]
    private void BringToFront(RunningItemViewModel item)
    {
        if (!AppInstances.Show(item.Engine))
            Status = $"{item.Name} isn't running any more.";
    }

    [RelayCommand]
    private async Task StopAsync(RunningItemViewModel item)
    {
        if (!string.IsNullOrEmpty(item.Script)
            && _dialogs.ShowMessageBox($"{item.Script} is running in {item.Name}. Stop it and quit?", "Stop", yesAndNo: true) != true)
            return;
        Status = $"Stopping {item.Name}…";
        try
        {
            await _instances.StopAsync(item.Engine, _stop.Token);
            Status = $"Stopped {item.Name}.";
        }
        catch (ControlException e)
        {
            Status = e.Message;
        }
        await RefreshAsync();
    }

    private void Show(IReadOnlyList<RunningEngine> engines)
    {
        foreach (RunningItemViewModel gone in Engines.Where(e => !engines.Any(r => r.Name == e.Name && r.Hello.Pid == e.Engine.Hello.Pid)).ToList())
            Engines.Remove(gone);
        for (int i = 0; i < engines.Count; i++)
        {
            RunningEngine engine = engines[i];
            if (Engines.FirstOrDefault(e => e.Name == engine.Name) is { } item)
            {
                item.Update(engine);
                Engines.Move(Engines.IndexOf(item), i);
            }
            else
            {
                Engines.Insert(i, new RunningItemViewModel(engine, _store.Accounts.FirstOrDefault(a => a.Name == engine.Name)?.DisplayName));
            }
        }
    }

    public void Dispose()
    {
        // Not disposed: a refresh or stop in flight may still read its token.
        _stop.Cancel();
    }
}
