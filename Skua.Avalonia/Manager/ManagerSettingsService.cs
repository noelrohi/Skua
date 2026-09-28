using System.Diagnostics;
using System.Reflection;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Avalonia.Manager;

/// <summary>
/// Core's settings as the Skua Manager's view models on macOS see them. <c>ManagedAccounts</c>, <c>AccountGroups</c> and <c>LastServer</c> live in
/// <see cref="ManagerAccounts"/>: a password the view model saves goes to Keychain, and what it reads back has none. The rest are fixed or read
/// from <c>Skua.settings.json</c>, and never written: Core's settings service saves the whole file from memory, which would undo what Engines and
/// <c>skua account</c> changed there.
/// </summary>
public sealed class ManagerSettingsService : ISettingsService
{
    private const string AccountsKey = "ManagedAccounts";
    private const string GroupsKey = "AccountGroups";
    private const string LastServerKey = "LastServer";

    private readonly ManagerAccounts _accounts;
    private readonly string _build;

    public ManagerSettingsService(ManagerAccounts accounts)
    {
        _accounts = accounts;
        _build = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    }

    /// <summary>
    /// Raised after a save that stored passwords in Keychain, so the view model can forget them; and with the accounts Keychain didn't take.
    /// </summary>
    public event Action<IReadOnlyList<AccountFailure>>? AccountsSaved;

    public T? Get<T>(string key)
    {
        object? value = key switch
        {
            AccountsKey => _accounts.Accounts.ToDictionary(
                a => a.Username,
                a => new AccountData { DisplayName = a.DisplayName, Password = "", Tags = [.. a.Tags] },
                StringComparer.OrdinalIgnoreCase),
            GroupsKey => _accounts.Groups.Select(g => new GroupData { Name = g.Name, Accounts = [.. g.Usernames] }).ToList(),
            LastServerKey => _accounts.LastServer,
            // The Mac App has no themes to hand on yet (#81), and launches without a GitHub token on its command line.
            "syncTheme" => false,
            "ApplicationVersion" => _build,
            _ => null,
        };
        return value is T typed ? typed : default;
    }

    public T Get<T>(string key, T defaultValue)
    {
        T? value = Get<T>(key);
        return value is null || value.Equals(default(T)) ? defaultValue : value;
    }

    public void Set<T>(string key, T value)
    {
        switch (key, value)
        {
            case (AccountsKey, Dictionary<string, AccountData> accounts):
                List<AccountEntry> entries = [.. accounts.Select(a => new AccountEntry(a.Key, a.Value.DisplayName, a.Value.Tags, a.Value.Password))];
                // Keychain runs the security tool: off the UI thread, whose context the awaits mustn't need.
                IReadOnlyList<AccountFailure> failures = Task.Run(() => _accounts.SetAccountsAsync(entries, CancellationToken.None)).GetAwaiter().GetResult();
                AccountsSaved?.Invoke(failures);
                break;
            case (GroupsKey, List<GroupData> groups):
                _accounts.SetGroups([.. groups.Select(g => new ManagedGroup(g.Name, [.. g.Accounts]))]);
                break;
            case (LastServerKey, string server):
                _accounts.LastServer = server;
                break;
            default:
                Trace.WriteLine($"The Skua Manager doesn't save '{key}' on macOS.");
                break;
        }
    }

    public void Initialize(AppRole role)
    {
    }

    public SharedSettings GetShared() => new();

    public ClientSettings GetClient() => new();

    public ManagerSettings GetManager() => new();

    public void SetApplicationVersion()
    {
    }
}
