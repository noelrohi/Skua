using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.Control;

/// <summary>An account the Skua Manager lists; its password is only in Keychain, under <see cref="Accounts.ServiceOf"/> of its name.</summary>
/// <param name="Name">Its account name (<see cref="Accounts"/>), which is also the Engine Name of the app the Manager launches for it.</param>
public sealed record ManagedAccount(string Name, string Username, string DisplayName, IReadOnlyList<string> Tags);

/// <summary>A group of accounts in the Skua Manager, by username, as the Windows Manager keeps them.</summary>
public sealed record ManagedGroup(string Name, IReadOnlyList<string> Usernames);

/// <summary>An account as the Manager's view model holds it; <see cref="Password"/> is set only when it was just typed.</summary>
public sealed record AccountEntry(string Username, string DisplayName, IReadOnlyList<string> Tags, string? Password = null)
{
    public override string ToString() => $"AccountEntry {{ Username = {Username}, DisplayName = {DisplayName} }}";
}

/// <summary>An account whose password Keychain didn't take.</summary>
public sealed record AccountFailure(string Username, string Message);

/// <summary>
/// The Skua Manager's accounts on macOS: their passwords in Keychain, through the same code as <c>skua account add</c>, and the rest (names,
/// display names, tags, groups and the last server) in <c>&lt;SkuaDIR&gt;/Skua.manager.json</c>, which holds no password. Unlike the Windows
/// Manager's list in <c>Skua.settings.json</c>, no Engine rewrites that file.
/// </summary>
/// <remarks>
/// One Manager runs per data folder, so this process is the file's only writer; its saves are serialized even across stores on the same
/// folder, such as a view model's and a test's.
/// </remarks>
public sealed class ManagerAccounts
{
    public const string FileName = "Skua.manager.json";

    /// <summary>Names an account never gets: the Test Account's, and the default Engine Name, which the developer's own app serves.</summary>
    private static readonly string[] Reserved = [Skua.Control.Accounts.TestAccountName, EngineName.Default];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One lock per file, for every store in this process.</summary>
    private static readonly ConcurrentDictionary<string, Lock> SaveLocks = new();

    private readonly Lock _lock = new();
    private Contents _contents;

    public ManagerAccounts(string skuaDir)
    {
        SkuaDir = skuaDir;
        FilePath = Path.Combine(skuaDir, FileName);
        _contents = Load(FilePath);
    }

    public string SkuaDir { get; }

    public string FilePath { get; }

    public IReadOnlyList<ManagedAccount> Accounts
    {
        get
        {
            lock (_lock)
                return _contents.Accounts;
        }
    }

    public IReadOnlyList<ManagedGroup> Groups
    {
        get
        {
            lock (_lock)
                return _contents.Groups;
        }
    }

    public string? LastServer
    {
        get
        {
            lock (_lock)
                return _contents.LastServer;
        }
        set => Change(c => c with { LastServer = value });
    }

    public ManagedAccount? Find(string username) => Accounts.FirstOrDefault(a => SameUser(a.Username, username));

    /// <summary>
    /// Makes the list these accounts, in this order. An account new to the list gets a name of its own; one with a password has it stored in
    /// Keychain; one no longer listed is deleted from Keychain, and if it was the Active Account the Test Account is active again.
    /// </summary>
    /// <returns>The accounts whose Keychain change failed; the list keeps them, so they can be added again.</returns>
    public async Task<IReadOnlyList<AccountFailure>> SetAccountsAsync(IReadOnlyList<AccountEntry> entries, CancellationToken cancellationToken)
    {
        List<AccountFailure> failures = [];
        List<ManagedAccount> accounts = [];
        IReadOnlyList<ManagedAccount> before = Accounts;
        foreach (AccountEntry entry in entries)
        {
            if (accounts.Any(a => SameUser(a.Username, entry.Username)))
                continue;
            ManagedAccount? known = before.FirstOrDefault(a => SameUser(a.Username, entry.Username));
            string name = known?.Name ?? await ChooseNameAsync(entry.Username, before.Select(a => a.Name).Concat(accounts.Select(a => a.Name)), cancellationToken);
            ManagedAccount account = new(name, entry.Username, string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Username : entry.DisplayName, [.. entry.Tags]);
            accounts.Add(account);
            if (!string.IsNullOrEmpty(entry.Password))
            {
                try
                {
                    await Skua.Control.Accounts.StoreAsync(name, entry.Username, entry.Password, allowAgents: false, cancellationToken);
                }
                catch (ControlException e)
                {
                    failures.Add(new AccountFailure(entry.Username, e.Message));
                }
            }
        }

        foreach (ManagedAccount gone in before.Where(b => !accounts.Any(a => a.Name == b.Name)))
        {
            try
            {
                await Skua.Control.Accounts.DeleteAsync(SkuaDir, gone.Name, cancellationToken);
            }
            catch (ControlException e)
            {
                failures.Add(new AccountFailure(gone.Username, e.Message));
            }
        }

        Change(c => c with { Accounts = accounts, Groups = Prune(c.Groups, accounts) });
        return failures;
    }

    /// <summary>Makes the groups these, keeping only accounts the list has.</summary>
    public void SetGroups(IReadOnlyList<ManagedGroup> groups) => Change(c => c with { Groups = Prune(groups, c.Accounts) });

    /// <summary>
    /// Adds accounts whose passwords are already in Keychain, or updates listed ones, keeping the rest; for an import.
    /// </summary>
    internal void Merge(IReadOnlyList<ManagedAccount> accounts, IReadOnlyList<ManagedGroup> groups) => Change(c =>
    {
        List<ManagedAccount> merged = [.. c.Accounts];
        foreach (ManagedAccount account in accounts)
        {
            int index = merged.FindIndex(a => SameUser(a.Username, account.Username));
            if (index < 0)
                merged.Add(account);
            else
                merged[index] = merged[index] with { DisplayName = account.DisplayName, Tags = [.. merged[index].Tags.Union(account.Tags, StringComparer.OrdinalIgnoreCase)] };
        }
        List<ManagedGroup> mergedGroups = [.. c.Groups];
        foreach (ManagedGroup group in groups)
        {
            int index = mergedGroups.FindIndex(g => string.Equals(g.Name, group.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                mergedGroups.Add(group);
            else
                mergedGroups[index] = mergedGroups[index] with { Usernames = [.. mergedGroups[index].Usernames.Union(group.Usernames, StringComparer.OrdinalIgnoreCase)] };
        }
        return c with { Accounts = merged, Groups = Prune(mergedGroups, merged) };
    });

    /// <summary>
    /// The name for a new account: its username's default name (<see cref="Accounts.NameFor"/>), made unique with <c>-2</c>, <c>-3</c>… among
    /// <paramref name="taken"/>, the reserved names and Keychain items that hold another username. An item holding the same username, such as one
    /// <c>skua account add</c> made, is the same account, so its name is reused.
    /// </summary>
    public static async Task<string> ChooseNameAsync(string username, IEnumerable<string> taken, CancellationToken cancellationToken)
    {
        HashSet<string> used = [.. taken, .. Reserved];
        string stem = Skua.Control.Accounts.NameFor(username) is { Length: > 0 } name ? name : "account";
        for (int n = 1; ; n++)
        {
            string suffix = n == 1 ? "" : $"-{n}";
            string candidate = (stem.Length + suffix.Length > 16 ? stem[..(16 - suffix.Length)].TrimEnd('-') : stem) + suffix;
            if (used.Contains(candidate))
                continue;
            if (await Keychain.FindAsync(Skua.Control.Accounts.ServiceOf(candidate), cancellationToken) is { } item && !SameUser(item.Account, username))
                continue;
            return candidate;
        }
    }

    private static bool SameUser(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static List<ManagedGroup> Prune(IReadOnlyList<ManagedGroup> groups, IReadOnlyList<ManagedAccount> accounts) =>
        [.. groups.Select(g => g with { Usernames = [.. g.Usernames.Where(u => accounts.Any(a => SameUser(a.Username, u))).Distinct(StringComparer.OrdinalIgnoreCase)] })];

    private void Change(Func<Contents, Contents> change)
    {
        lock (_lock)
        {
            _contents = change(_contents);
            Save(_contents);
        }
    }

    private static Contents Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Contents>(File.ReadAllText(path), JsonOptions) is { } contents
                ? contents with { Accounts = [.. contents.Accounts.Where(a => Skua.Control.Accounts.IsValidName(a.Name) && a.Username.Length > 0)] }
                : new Contents();
        }
        catch (FileNotFoundException)
        {
            return new Contents();
        }
        catch (DirectoryNotFoundException)
        {
            return new Contents();
        }
        catch (JsonException)
        {
            // Kept for the developer to look at; the Manager starts with an empty list rather than refusing to open.
            File.Copy(path, path + ".corrupt", overwrite: true);
            return new Contents();
        }
    }

    /// <summary>Written whole to a temporary file of its own and moved into place, owner-only.</summary>
    private void Save(Contents contents)
    {
        Directory.CreateDirectory(SkuaDir);
        string temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        lock (SaveLocks.GetOrAdd(FilePath, _ => new Lock()))
        {
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(contents, JsonOptions));
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporary, FilePath, overwrite: true);
            }
            catch
            {
                File.Delete(temporary);
                throw;
            }
        }
    }

    private sealed record Contents
    {
        public IReadOnlyList<ManagedAccount> Accounts { get; init; } = [];

        public IReadOnlyList<ManagedGroup> Groups { get; init; } = [];

        public string? LastServer { get; init; }
    }
}
