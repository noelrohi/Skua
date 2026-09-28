using System.Text.Json.Nodes;

namespace Skua.Control;

/// <summary>What an import did.</summary>
/// <param name="Imported">The accounts whose passwords moved to Keychain.</param>
/// <param name="Updated">Accounts the Manager already listed and the file held no password for; their tags and groups were merged.</param>
/// <param name="Skipped">Accounts the file held no password for and the Manager didn't list, so they can't log in.</param>
/// <param name="Failures">Accounts whose passwords Keychain didn't take; the file keeps those.</param>
/// <param name="BackupPath">The copy of the file as it was, which still holds the passwords; null when the file held none to remove.</param>
public sealed record ImportResult(
    IReadOnlyList<string> Imported, IReadOnlyList<string> Updated, IReadOnlyList<string> Skipped, IReadOnlyList<AccountFailure> Failures, string? BackupPath);

/// <summary>
/// Imports the Windows Skua Manager's account list, which keeps passwords in plain text: <c>ManagedAccounts</c> and <c>AccountGroups</c> under
/// <c>manager</c> in its <c>Skua.settings.json</c>, or at the top of the older <c>ManagerSettings.json</c>; each account as an object, or in the
/// legacy <c>display{=}username{=}password</c> strings. Passwords move to Keychain and are then removed from the file, after a copy of it is
/// kept next to it.
/// </summary>
public static class WindowsAccountImport
{
    private const string LegacySeparator = "{=}";

    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.InvalidArgument"/> when the file can't be read or holds no account list, and <see cref="ErrorCode.Busy"/> when another
    /// process holds the settings lock.
    /// </exception>
    public static async Task<ImportResult> ImportAsync(ManagerAccounts store, string path, CancellationToken cancellationToken)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken), new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject
                ?? throw new ControlException(ErrorCode.InvalidArgument, $"{path} isn't a JSON object.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new ControlException(ErrorCode.InvalidArgument, $"Couldn't read {path}: {e.Message}");
        }
        JsonObject section = ListSection(root) ?? throw new ControlException(ErrorCode.InvalidArgument,
            $"{path} holds no Windows account list (ManagedAccounts); pick the Windows Manager's Skua.settings.json or ManagerSettings.json.");

        List<(string Username, string DisplayName, string Password, List<string> Tags)> accounts = ReadAccounts(section["ManagedAccounts"]);
        List<string> imported = [], updated = [], skipped = [];
        List<AccountFailure> failures = [];
        List<ManagedAccount> merged = [];
        HashSet<string> moved = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string username, string displayName, string password, List<string> tags) in accounts)
        {
            string display = string.IsNullOrWhiteSpace(displayName) ? username : displayName;
            ManagedAccount? known = store.Find(username);
            if (password.Length == 0)
            {
                if (known is null)
                {
                    skipped.Add(username);
                    continue;
                }
                merged.Add(known with { DisplayName = display, Tags = tags });
                updated.Add(username);
                continue;
            }

            string name = known?.Name
                ?? await ManagerAccounts.ChooseNameAsync(username, store.Accounts.Select(a => a.Name).Concat(merged.Select(a => a.Name)), cancellationToken);
            try
            {
                await Accounts.StoreAsync(name, username, password, allowAgents: false, cancellationToken);
            }
            catch (ControlException e)
            {
                failures.Add(new AccountFailure(username, e.Message));
                continue;
            }
            merged.Add(new ManagedAccount(name, username, display, tags));
            imported.Add(username);
            moved.Add(username);
        }

        store.Merge(merged, ReadGroups(section["AccountGroups"]));

        string? backup = null;
        if (moved.Count > 0)
        {
            backup = $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(path, backup);
            File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            SettingsFile.UpdateFile(path, fresh =>
            {
                if (ListSection(fresh)?["ManagedAccounts"] is { } list)
                    RemovePasswords(list, moved);
            });
        }
        return new ImportResult(imported, updated, skipped, failures, backup);
    }

    private static JsonObject? ListSection(JsonObject root) =>
        root["manager"] is JsonObject manager && manager["ManagedAccounts"] is not null ? manager
        : root["ManagedAccounts"] is not null ? root
        : null;

    private static List<(string, string, string, List<string>)> ReadAccounts(JsonNode? list)
    {
        List<(string, string, string, List<string>)> accounts = [];
        if (list is JsonObject byUsername)
        {
            foreach ((string username, JsonNode? data) in byUsername)
            {
                if (username.Length == 0 || data is not JsonObject account)
                    continue;
                accounts.Add((username, Text(account["DisplayName"]), Text(account["Password"]),
                    account["Tags"] is JsonArray tags ? [.. tags.Select(Text).Where(t => t.Length > 0)] : []));
            }
        }
        else if (list is JsonArray legacy)
        {
            foreach (JsonNode? item in legacy)
            {
                string[] parts = Text(item).Split(LegacySeparator);
                if (parts.Length >= 3 && parts[1].Length > 0)
                    accounts.Add((parts[1], parts[0], parts[2], []));
            }
        }
        return accounts;
    }

    private static List<ManagedGroup> ReadGroups(JsonNode? list) => list is JsonArray groups
        ? [.. groups.OfType<JsonObject>()
            .Where(g => Text(g["Name"]).Length > 0)
            .Select(g => new ManagedGroup(Text(g["Name"]), g["Accounts"] is JsonArray members ? [.. members.Select(Text).Where(u => u.Length > 0)] : []))]
        : [];

    private static void RemovePasswords(JsonNode list, HashSet<string> usernames)
    {
        if (list is JsonObject byUsername)
        {
            foreach ((string username, JsonNode? data) in byUsername)
            {
                if (usernames.Contains(username) && data is JsonObject account)
                {
                    foreach (string key in account.Select(p => p.Key).Where(k => k.Equals("Password", StringComparison.OrdinalIgnoreCase)).ToList())
                        account[key] = "";
                }
            }
        }
        else if (list is JsonArray legacy)
        {
            for (int i = 0; i < legacy.Count; i++)
            {
                string[] parts = Text(legacy[i]).Split(LegacySeparator);
                if (parts.Length >= 3 && usernames.Contains(parts[1]))
                    legacy[i] = string.Join(LegacySeparator, [parts[0], parts[1], "", .. parts[3..]]);
            }
        }
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : "";
}
