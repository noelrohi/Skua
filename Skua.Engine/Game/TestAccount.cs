using Skua.Control;

namespace Skua.Engine.Game;

/// <summary>The account <c>login</c> uses: the Test Account, or the one <c>skua account</c> made active. Never log or return <see cref="Password"/>.</summary>
internal sealed record TestAccount(string Username, string Password)
{
    /// <summary>The Engine setting (in <c>client</c> of Skua.settings.json) that names the Keychain service holding the account.</summary>
    public const string ServiceSetting = AccountSetting.Key;

    public override string ToString() => $"TestAccount {{ Username = {Username} }}";

    /// <summary>
    /// Reads the generic password under <paramref name="service"/> from Keychain; its account is the username. macOS may ask once whether
    /// <c>security</c> may read it.
    /// </summary>
    /// <exception cref="StreamJsonRpc.LocalRpcException"><see cref="ErrorCode.LoginFailed"/> when there is none or it can't be read.</exception>
    public static async Task<TestAccount> ReadAsync(string service, CancellationToken cancellationToken)
    {
        KeychainItem? item;
        try
        {
            item = await Keychain.ReadAsync(service, cancellationToken);
        }
        catch (ControlException e) when (e.Code == ErrorCode.KeychainFailed)
        {
            throw RpcErrors.Of(ErrorCode.LoginFailed, $"Couldn't read the account from Keychain ({e.Message}); allow security access when macOS asks.");
        }
        if (item is null)
            throw RpcErrors.Of(ErrorCode.LoginFailed,
                $"There is no account in Keychain under the service '{service}': add one with 'skua account add' (it asks for the username and password), " +
                "or switch to another with 'skua account use <name>'.");
        if (item.Account.Length == 0 || item.Password.Length == 0)
            throw RpcErrors.Of(ErrorCode.LoginFailed, $"The Keychain item '{service}' has no account or no password; add it again with 'skua account add'.");
        return new TestAccount(item.Account, item.Password);
    }
}
