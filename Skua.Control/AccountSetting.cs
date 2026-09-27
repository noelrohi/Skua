using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>Core's lock on the settings file, taken across processes.</summary>
    private const string FileMutex = @"Global\Skua.Settings.IO";

    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(10);

    public static string SettingsFile(string skuaDir) => Path.Combine(skuaDir, "Skua.settings.json");

    /// <summary>The service the setting names, or <see cref="DefaultService"/> when the file or the setting is missing or unreadable.</summary>
    public static string Read(string skuaDir)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(SettingsFile(skuaDir)))?["client"]?[Key]?.GetValue<string>() is { } service && !string.IsNullOrWhiteSpace(service)
                ? service
                : DefaultService;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return DefaultService;
        }
    }

    /// <summary>Sets the service in the settings file, keeping the rest of it, under Core's lock.</summary>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.InvalidArgument"/> when the settings file isn't a JSON object, and <see cref="ErrorCode.Busy"/> when another process
    /// holds the file's lock for 10 s.
    /// </exception>
    public static void Write(string skuaDir, string service)
    {
        string path = SettingsFile(skuaDir);
        using Mutex mutex = new(false, FileMutex);
        bool held;
        try
        {
            held = mutex.WaitOne(MutexTimeout);
        }
        catch (AbandonedMutexException)
        {
            held = true;
        }
        if (!held)
            throw new ControlException(ErrorCode.Busy, $"Another Skua process holds {path} locked; try again.");
        try
        {
            JsonObject root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                    ?? throw new ControlException(ErrorCode.InvalidArgument, $"{path} isn't a JSON object; fix or delete it.");
            }
            catch (FileNotFoundException)
            {
                root = [];
            }
            catch (JsonException e)
            {
                throw new ControlException(ErrorCode.InvalidArgument, $"{path} isn't valid JSON ({e.Message}); fix or delete it.");
            }

            if (root["client"] is not JsonObject client)
                root["client"] = client = [];
            client[Key] = service;

            Directory.CreateDirectory(skuaDir);
            string temporary = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
