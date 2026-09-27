namespace Skua.Control;

/// <summary>
/// The Keychain service <c>login</c> reads its account from: <c>TestAccountService</c> under <c>client</c> in <c>&lt;SkuaDIR&gt;/Skua.settings.json</c>,
/// which Core's settings also hold. <c>skua account</c> changes it while an Engine runs, so the Engine reads it afresh at every login.
/// </summary>
public static class AccountSetting
{
    public const string Key = "TestAccountService";

    /// <summary>The Test Account's service, and the setting's default.</summary>
    public const string DefaultService = "skua-test-account";

    /// <summary>
    /// The Keychain comment of an account added with <c>--allow-agents</c>: agents' logins may use it while it is active. Without it they use the
    /// Test Account.
    /// </summary>
    public const string AllowAgentsComment = "skua:allow-agents";

    /// <summary>The service the setting names, or <see cref="DefaultService"/> when the file or the setting is missing or unreadable.</summary>
    public static string Read(string skuaDir)
    {
        try
        {
            return SettingsFile.Read(skuaDir)?["client"]?[Key]?.GetValue<string>() is { } service && !string.IsNullOrWhiteSpace(service)
                ? service
                : DefaultService;
        }
        catch (InvalidOperationException)
        {
            return DefaultService;
        }
    }

    /// <summary>Sets the service in the settings file, keeping the rest of it, under Core's lock.</summary>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.InvalidArgument"/> when the settings file isn't a JSON object, and <see cref="ErrorCode.Busy"/> when another process
    /// holds the file's lock for 10 s.
    /// </exception>
    public static void Write(string skuaDir, string service) =>
        SettingsFile.Update(skuaDir, root => SettingsFile.Section(root, "client")[Key] = service);
}
