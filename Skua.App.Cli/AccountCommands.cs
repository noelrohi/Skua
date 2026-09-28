using System.Text;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>An account in Keychain; <c>skua account</c> never shows its password.</summary>
/// <param name="Name">What <c>skua account</c> calls it: <c>test</c> for the Test Account, or null for a service set by hand.</param>
/// <param name="Service">The Keychain service it is stored under.</param>
/// <param name="Active">Whether <c>skua login</c> uses it.</param>
/// <param name="AllowAgents">
/// Whether agents' logins (MCP's) may use it while it is active; otherwise they use the Test Account. Always true for the Test Account.
/// </param>
public sealed record AccountDto(string? Name, string Service, string Username, bool Active, bool AllowAgents);

/// <summary>
/// <c>skua account add|show|use|remove</c>: the accounts <c>skua login</c> can use, kept in Keychain by the CLI and the Skua Manager through <see cref="Accounts"/>. The Control Surface never
/// carries a credential, so neither the Engine nor MCP can set one; the Engine reads the active account at every login.
/// </summary>
/// <remarks>
/// A personal account named <c>main</c> is stored under <c>skua-account-main</c>. The Test Account keeps its reserved name <c>test</c> and service,
/// which agents and the live tests use; <c>add</c> changes it only with <c>--test</c>, or its name and <c>--replace</c>.
/// </remarks>
internal static class AccountCommands
{
    public const string TestAccountName = Accounts.TestAccountName;

    /// <summary>
    /// Stores a personal account, named <paramref name="name"/> or after its username, and makes <c>skua login</c> use it; or, with
    /// <paramref name="test"/>, stores the Test Account, leaving the active account as it is.
    /// </summary>
    /// <param name="replace">Whether an account already stored under the name may be replaced; without it, adding one fails.</param>
    /// <param name="allowAgents">Whether agents' logins may use the account while it is active.</param>
    public static async Task<AccountDto> AddAsync(
        string? name, string? username, bool replace, bool test, bool allowAgents, CancellationToken cancellationToken)
    {
        if (test && name is not null and not TestAccountName)
            throw new ControlException(ErrorCode.InvalidArgument, "--test stores the Test Account, whose name is test; drop --name.");
        test |= name == TestAccountName;
        if (test && allowAgents)
            throw new ControlException(ErrorCode.InvalidArgument, "Agents always may use the Test Account; drop --allow-agents.");
        // Only --test itself replaces the Test Account; its name needs --replace, like any stored account.
        replace |= test && name is null;
        if (name is not null)
            await EnsureAbsentAsync(name, replace, cancellationToken);

        username = (username ?? Prompt.ReadLine("Username: ")).Trim();
        if (username.Length == 0)
            throw new ControlException(ErrorCode.InvalidArgument, "The username is empty.");
        if (test)
            name = TestAccountName;
        else if (name is null)
        {
            name = NameFor(username);
            await EnsureAbsentAsync(name, replace, cancellationToken);
        }
        string service = ServiceOf(name);
        string password = Prompt.ReadSecret("Password: ");
        if (password.Length == 0)
            throw new ControlException(ErrorCode.InvalidArgument, "The password is empty; nothing was stored.");

        await Accounts.StoreAsync(name, username, password, allowAgents, cancellationToken);
        if (!test)
            AccountSetting.Write(SkuaDir, service);
        return new AccountDto(name, service, username, Active: AccountSetting.Read(SkuaDir) == service, AllowAgents: test || allowAgents);
    }

    /// <summary>The named account, or the active one.</summary>
    public static async Task<AccountDto> ShowAsync(string? name, CancellationToken cancellationToken)
    {
        string active = AccountSetting.Read(SkuaDir);
        string service = name is null ? active : ServiceOf(name);
        return ToDto(name ?? NameOf(service), service, await FindAsync(name, service, cancellationToken), service == active);
    }

    public static async Task<AccountDto> UseAsync(string name, CancellationToken cancellationToken)
    {
        string service = ServiceOf(name);
        KeychainAttributes account = await FindAsync(name, service, cancellationToken);
        AccountSetting.Write(SkuaDir, service);
        return ToDto(name, service, account, active: true);
    }

    /// <summary>
    /// Deletes the named account, or the active one unless that is the Test Account, which must be named; removing the active one makes the
    /// Test Account active again.
    /// </summary>
    /// <returns>The removed account, active if it was.</returns>
    public static async Task<AccountDto> RemoveAsync(string? name, CancellationToken cancellationToken)
    {
        string active = AccountSetting.Read(SkuaDir);
        string service = name is null ? active : ServiceOf(name);
        if (name is null && service == AccountSetting.DefaultService)
            throw new ControlException(ErrorCode.InvalidArgument,
                $"The active account is the Test Account, which the live tests use; to delete it, name it: 'skua account remove {TestAccountName}'.");
        KeychainAttributes account = await FindAsync(name, service, cancellationToken);
        if (!await Keychain.DeleteAsync(service, cancellationToken))
            throw NotFound(name, service);
        if (service == active)
            AccountSetting.Write(SkuaDir, AccountSetting.DefaultService);
        return ToDto(name ?? NameOf(service), service, account, service == active);
    }

    private static string ServiceOf(string name) => Accounts.ServiceOf(name);

    private static string? NameOf(string service) => Accounts.NameOf(service);

    /// <summary>A personal account's default name: its username in lower case, with a hyphen for each run of other characters.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> when that makes no usable name.</exception>
    private static string NameFor(string username)
    {
        string name = Accounts.NameFor(username);
        if (name.Length == 0 || name == TestAccountName)
            throw new ControlException(ErrorCode.InvalidArgument, $"The username '{username}' makes no account name of its own; give one with --name.");
        return name;
    }

    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> when an account is stored under the name, unless it may be replaced.</exception>
    private static async Task EnsureAbsentAsync(string name, bool replace, CancellationToken cancellationToken)
    {
        string service = ServiceOf(name);
        if (!replace && await Keychain.FindAsync(service, cancellationToken) is { } existing)
            throw new ControlException(ErrorCode.InvalidArgument, name == TestAccountName
                ? $"The Test Account ({existing.Account}) is already in Keychain as '{service}'; replace it with 'skua account add --test'."
                : $"Account {name} ({existing.Account}) is already in Keychain as '{service}'; pass --replace to replace it, or --name to add another.");
    }

    private static AccountDto ToDto(string? name, string service, KeychainAttributes account, bool active) =>
        new(name, service, account.Account, active, AllowAgents: service == AccountSetting.DefaultService || account.Comment == AccountSetting.AllowAgentsComment);

    private static async Task<KeychainAttributes> FindAsync(string? name, string service, CancellationToken cancellationToken) =>
        await Keychain.FindAsync(service, cancellationToken) ?? throw NotFound(name, service);

    private static ControlException NotFound(string? name, string service) => new(ErrorCode.AccountNotFound,
        $"No account is in Keychain under '{service}'; add it with 'skua account add{((name ?? NameOf(service)) is { } known ? known == TestAccountName ? " --test" : $" --name {known}" : "")}'.");

    private static string SkuaDir => EngineEndpoint.DefaultSkuaDir();
}

/// <summary>Asks on the terminal, on stderr so stdout keeps only the result; with stdin redirected, each answer is a line of it.</summary>
internal static class Prompt
{
    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> when stdin ends first.</exception>
    public static string ReadLine(string prompt)
    {
        Console.Error.Write(prompt);
        return Console.In.ReadLine() ?? throw new ControlException(ErrorCode.InvalidArgument, $"No answer to '{prompt.TrimEnd(' ', ':')}': stdin ended.");
    }

    /// <summary>Reads a secret without echoing it on a terminal.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> when stdin ends first.</exception>
    public static string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
            return ReadLine(prompt);

        Console.Error.Write(prompt);
        StringBuilder secret = new();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                    secret.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }
        Console.Error.WriteLine();
        return secret.ToString();
    }
}
