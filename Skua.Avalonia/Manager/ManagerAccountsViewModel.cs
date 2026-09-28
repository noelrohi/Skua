using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.ViewModels;
using Skua.Core.ViewModels.Manager;
using ManagedAccount = Skua.Control.ManagedAccount;

namespace Skua.Avalonia.Manager;

/// <summary>
/// The Accounts tab on macOS: Core's <see cref="AccountManagerViewModel"/> for the list, its tags and its groups, as on Windows, with launching
/// and importing done the macOS way. Each launch starts a Mac App with the account's own Engine Name, which logs that account in; no password is
/// ever on its command line.
/// </summary>
public sealed partial class ManagerAccountsViewModel : ObservableObject
{
    /// <summary>Between launches, as the Windows Manager waits, so the apps' Game Hosts don't all start at once.</summary>
    public static TimeSpan LaunchSpacing { get; set; } = TimeSpan.FromSeconds(1);

    private readonly ManagerAccounts _store;
    private readonly AppInstances _instances;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogService _files;

    public ManagerAccountsViewModel(
        AccountManagerViewModel accounts, ManagerAccounts store, ManagerSettingsService settings, AppInstances instances, IDialogService dialogs, IFileDialogService files)
    {
        Accounts = accounts;
        _store = store;
        _instances = instances;
        _dialogs = dialogs;
        _files = files;

        // Core's handlers launch Skua.exe with the password on its command line; these launch the Mac App instead.
        WeakReferenceMessenger.Default.Unregister<StartAccountMessage>(accounts);
        WeakReferenceMessenger.Default.Unregister<StartGroupMessage>(accounts);
        WeakReferenceMessenger.Default.Register<ManagerAccountsViewModel, StartAccountMessage>(this, (r, m) => _ = r.LaunchAsync([m.Account], m.WithScript));
        WeakReferenceMessenger.Default.Register<ManagerAccountsViewModel, StartGroupMessage>(this, (r, m) => _ = r.LaunchAsync([.. m.Group.Accounts], m.WithScript));
        settings.AccountsSaved += OnAccountsSaved;
    }

    public AccountManagerViewModel Accounts { get; }

    /// <summary>The dialogs the view asks for tags and groups with.</summary>
    public IDialogService Dialogs => _dialogs;

    /// <summary>Whether a launch logs in on <see cref="AccountManagerViewModel.SelectedServer"/>; otherwise it picks the emptiest server.</summary>
    [ObservableProperty]
    private bool _useSelectedServer;

    /// <summary>What the last launch or import did.</summary>
    [ObservableProperty]
    private string _status = "";

    [RelayCommand]
    private Task LaunchSelectedAsync() => LaunchAsync([.. Accounts.Accounts.Where(a => a.UseCheck)], Accounts.StartWithScript);

    [RelayCommand]
    private Task LaunchAllAsync() => LaunchAsync([.. Accounts.FilteredAccounts], Accounts.StartWithScript);

    /// <summary>
    /// Launches an app per account, one after another; an account whose app already runs is brought to the front instead.
    /// </summary>
    public async Task LaunchAsync(IReadOnlyList<AccountItemViewModel> accounts, bool withScript)
    {
        if (accounts.Count == 0)
        {
            Status = "Select an account to launch.";
            return;
        }
        if (withScript && string.IsNullOrEmpty(Accounts.ScriptPath))
        {
            _dialogs.ShowMessageBox("No script selected. Please select a script first.", "No Script");
            return;
        }

        List<string> launched = [], shown = [], failed = [];
        foreach (AccountItemViewModel account in accounts)
        {
            if (_store.Find(account.Username) is not { } managed)
            {
                failed.Add($"{account.Username} (not saved)");
                continue;
            }
            RunningEngine? running = await AppInstances.DescribeAsync(_instances.Endpoint(managed.Name), CancellationToken.None);
            if (running is { IsApp: true })
            {
                AppInstances.Show(running);
                shown.Add(managed.Name);
                continue;
            }
            try
            {
                string? server = UseSelectedServer ? Accounts.SelectedServer?.Name : null;
                _instances.Launch(new AppArguments(managed.Name, managed.Name, server, withScript ? Accounts.ScriptPath : null)).Dispose();
                launched.Add(managed.Name);
            }
            catch (ControlException e)
            {
                failed.Add($"{managed.Name}: {e.Message}");
                continue;
            }
            if (launched.Count < accounts.Count)
                await Task.Delay(LaunchSpacing);
        }

        List<string> parts = [];
        if (launched.Count > 0)
            parts.Add($"Launched {string.Join(", ", launched)}");
        if (shown.Count > 0)
            parts.Add($"already running: {string.Join(", ", shown)}");
        if (failed.Count > 0)
            parts.Add($"couldn't launch {string.Join("; ", failed)}");
        Status = string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// Imports the Windows Manager's account list from a file the developer picks: its passwords move to Keychain and out of the file, of which a
    /// copy is kept.
    /// </summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        string? path = _files.OpenFile(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Skua settings (*.json)|*.json");
        if (!string.IsNullOrEmpty(path))
            await ImportAsync(path);
    }

    public async Task<ImportResult?> ImportAsync(string path)
    {
        ImportResult result;
        try
        {
            result = await Task.Run(() => WindowsAccountImport.ImportAsync(_store, path, CancellationToken.None));
        }
        catch (ControlException e)
        {
            Status = e.Message;
            _dialogs.ShowMessageBox(e.Message, "Import");
            return null;
        }
        ShowStore();

        List<string> lines = [$"Imported {result.Imported.Count} account(s); their passwords are now in Keychain."];
        if (result.BackupPath is not null)
            lines.Add($"The passwords were removed from {Path.GetFileName(path)}. A copy of it as it was is at {result.BackupPath}; it still holds them, so delete it once you've checked the import.");
        if (result.Updated.Count > 0)
            lines.Add($"Updated {string.Join(", ", result.Updated)}.");
        if (result.Skipped.Count > 0)
            lines.Add($"Skipped {string.Join(", ", result.Skipped)}: the file has no password for them. Add them by hand.");
        foreach (AccountFailure failure in result.Failures)
            lines.Add($"{failure.Username}: {failure.Message}");
        Status = lines[0];
        _dialogs.ShowMessageBox(string.Join("\n\n", lines), "Import");
        return result;
    }

    /// <summary>Shows what the store holds in Core's view model, which only reads its settings when it is made.</summary>
    private void ShowStore()
    {
        foreach (ManagedAccount managed in _store.Accounts)
        {
            AccountItemViewModel? item = Accounts.Accounts.FirstOrDefault(a => string.Equals(a.Username, managed.Username, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = new AccountItemViewModel { Username = managed.Username, Password = "" };
                Accounts.Accounts.Add(item);
            }
            item.DisplayName = managed.DisplayName;
            foreach (string tag in managed.Tags.Where(t => !item.Tags.Contains(t)))
                item.Tags.Add(tag);
        }
        foreach (ManagedGroup managed in _store.Groups)
        {
            GroupItemViewModel? group = Accounts.Groups.FirstOrDefault(g => string.Equals(g.Name, managed.Name, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                group = new GroupItemViewModel(managed.Name);
                Accounts.Groups.Add(group);
            }
            foreach (string username in managed.Usernames)
            {
                if (Accounts.Accounts.FirstOrDefault(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)) is { } member && !group.Accounts.Contains(member))
                    group.Accounts.Add(member);
            }
        }
        Accounts.RefreshTagFilters();
        Accounts.ApplyTagFilter();
    }

    /// <summary>The view model keeps a typed password until its next save; once Keychain has it, it forgets it.</summary>
    private void OnAccountsSaved(IReadOnlyList<AccountFailure> failures)
    {
        foreach (AccountItemViewModel account in Accounts.Accounts)
            account.Password = "";
        Accounts.PasswordInput = "";
        if (failures.Count > 0)
            _dialogs.ShowMessageBox(
                string.Join("\n", failures.Select(f => $"{f.Username}: {f.Message}")) + "\n\nAdd the account again once Keychain can store it.",
                "Keychain");
    }
}
