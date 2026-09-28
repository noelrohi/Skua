using System.Text.RegularExpressions;

namespace Skua.Control;

/// <summary>
/// The accounts <c>skua account</c> and the Skua Manager keep in Keychain, by name: a personal account named <c>main</c> is stored under
/// <c>skua-account-main</c>, and the Test Account keeps its reserved name <c>test</c> and service, which agents and the live tests use.
/// A password goes only to Keychain, never to a settings file or a log.
/// </summary>
public static partial class Accounts
{
    public const string TestAccountName = "test";

    public const string ServicePrefix = "skua-account-";

    /// <summary>Whether <paramref name="name"/> is an account name: <c>[a-z0-9-]{1,16}</c>, as an Engine Name is.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>The Keychain service the named account is stored under.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> unless the name matches <c>[a-z0-9-]{1,16}</c>.</exception>
    public static string ServiceOf(string name)
    {
        if (!IsValidName(name))
            throw new ControlException(ErrorCode.InvalidArgument, $"Account name '{name}' is invalid; it must match [a-z0-9-]{{1,16}}.");
        return name == TestAccountName ? AccountSetting.DefaultService : ServicePrefix + name;
    }

    /// <summary>The name of the account stored under <paramref name="service"/>, or null for a service set by hand.</summary>
    public static string? NameOf(string service) =>
        service == AccountSetting.DefaultService ? TestAccountName
        : service.StartsWith(ServicePrefix, StringComparison.Ordinal) && IsValidName(service[ServicePrefix.Length..]) ? service[ServicePrefix.Length..]
        : null;

    /// <summary>
    /// A personal account's default name: its username in lower case, with a hyphen for each run of other characters, cut to 16; empty when
    /// that leaves nothing.
    /// </summary>
    public static string NameFor(string username)
    {
        string name = NotNameCharacters().Replace(username.ToLowerInvariant(), "-").Trim('-');
        return name.Length > 16 ? name[..16].TrimEnd('-') : name;
    }

    /// <summary>Stores the account in Keychain under its name's service, replacing any item there. It doesn't change the Active Account.</summary>
    /// <param name="allowAgents">Whether agents' logins may use it while it is active (ADR 0005).</param>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.InvalidArgument"/> for a bad name or an empty password, and <see cref="ErrorCode.KeychainFailed"/> when <c>security</c> fails.
    /// </exception>
    public static Task StoreAsync(string name, string username, string password, bool allowAgents, CancellationToken cancellationToken) =>
        Keychain.AddAsync(ServiceOf(name), username, $"Skua account {name}", allowAgents ? AccountSetting.AllowAgentsComment : "", password, cancellationToken);

    /// <summary>
    /// Deletes the named account from Keychain; when it was the Active Account, the Test Account becomes active again.
    /// </summary>
    /// <returns>Whether Keychain had it.</returns>
    /// <exception cref="ControlException"><see cref="ErrorCode.KeychainFailed"/> when <c>security</c> fails.</exception>
    public static async Task<bool> DeleteAsync(string skuaDir, string name, CancellationToken cancellationToken)
    {
        string service = ServiceOf(name);
        bool deleted = await Keychain.DeleteAsync(service, cancellationToken);
        if (AccountSetting.Read(skuaDir) == service)
            AccountSetting.Write(skuaDir, AccountSetting.DefaultService);
        return deleted;
    }

    [GeneratedRegex("^[a-z0-9-]{1,16}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotNameCharacters();
}
