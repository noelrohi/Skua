using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>The credential files that stand in for Keychain on Linux, and <c>skua account</c> and <c>login</c> on top of them there.</summary>
public class CredentialFilesTests
{
    private const UnixFileMode OwnerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    // Quotes, a backslash, shell syntax and a non-ASCII letter, as AccountTests' password.
    private const string Password = "Pa55 \"wörd\" $HOME `id` \\ 'end'";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_item_is_added_read_replaced_and_deleted_in_a_file_only_its_owner_can_open()
    {
        await using EngineSandbox sandbox = new();
        CredentialFiles files = new(Path.Combine(sandbox.SkuaDir, "credentials"));

        Assert.Null(files.Find("skua-account-alt1"));
        Assert.Null(files.Read("skua-account-alt1"));
        files.Add("skua-account-alt1", "Alt1", "", Password);
        KeychainAttributes? added = files.Find("skua-account-alt1");
        KeychainItem? read = files.Read("skua-account-alt1");
        files.Add("skua-account-alt1", "Alt1", AccountSetting.AllowAgentsComment, "second");
        KeychainAttributes? replaced = files.Find("skua-account-alt1");
        string password = files.Read("skua-account-alt1")!.Password;
        string file = Path.Combine(files.Directory, "skua-account-alt1");
        (UnixFileMode folderMode, UnixFileMode fileMode) = (File.GetUnixFileMode(files.Directory), File.GetUnixFileMode(file));
        string[] entries = Directory.GetFileSystemEntries(files.Directory);
        bool deleted = files.Delete("skua-account-alt1");
        bool deletedAgain = files.Delete("skua-account-alt1");

        Assert.Equal(new KeychainAttributes("Alt1", null), added);
        Assert.Equal(("Alt1", Password), (read!.Account, read.Password));
        Assert.Equal(new KeychainAttributes("Alt1", AccountSetting.AllowAgentsComment), replaced);
        Assert.Equal("second", password);
        Assert.Equal((OwnerOnlyFolder, OwnerOnlyFile), (folderMode, fileMode));
        // Written whole and moved into place: no temporary file is left beside it.
        Assert.Equal([file], entries);
        Assert.Equal((true, false), (deleted, deletedAgain));
        Assert.Null(files.Find("skua-account-alt1"));
    }

    /// <summary>As AccountTests' three-step flow, with no <c>security</c> tool: the CLI stores the account and the Engine logs in with it.</summary>
    [Fact]
    public async Task On_Linux_account_add_stores_the_account_in_a_credential_file_that_login_reads_and_remove_deletes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux keeps accounts in credential files; macOS keeps them in Keychain.");
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox, service: null);
        FakeGameHost gameHost = new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers).Account("Alt1", Password);
        Dictionary<string, string> environment = GameFixture.Environment(gameHost, api, keychain);
        // No security tool: Keychain is the credential files.
        environment[Keychain.ToolVariable] = "";

        ProcessResult add = await sandbox.RunCliWithInputAsync(environment, $"Alt1\n{Password}\n", "account", "add");
        string file = Path.Combine(sandbox.SkuaDir, Keychain.CredentialsFolder, "skua-account-alt1");
        (UnixFileMode folderMode, UnixFileMode fileMode) = (File.GetUnixFileMode(Path.GetDirectoryName(file)!), File.GetUnixFileMode(file));
        using JsonDocument stored = JsonDocument.Parse(await File.ReadAllTextAsync(file, Ct));
        ProcessResult login = await sandbox.RunCliAsync(environment, "login", "Galanoth");
        ProcessResult remove = await sandbox.RunCliAsync(environment, "account", "remove", "alt1");
        await sandbox.RunCliAsync(environment, "engine", "stop");

        Assert.True(add.ExitCode == 0, add.Stderr);
        Assert.Equal("Added account alt1 (Alt1) to Keychain as 'skua-account-alt1'; 'skua login' now uses it.", add.Stdout.Trim());
        Assert.Equal((OwnerOnlyFolder, OwnerOnlyFile), (folderMode, fileMode));
        Assert.Equal(("Alt1", Password), (stored.RootElement.GetProperty("Account").GetString(), stored.RootElement.GetProperty("Password").GetString()));
        Assert.Equal((0, "Logged in as Alt1 on Galanoth."), (login.ExitCode, login.Stdout.Trim()));
        Assert.True(remove.ExitCode == 0, remove.Stderr);
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(keychain.Log), "The fake security tool ran.");
    }

    /// <summary>A fresh server's account, whose <c>~/.config</c> doesn't exist yet: the data folder is still <c>~/.config/Skua</c>.</summary>
    [Fact]
    public async Task On_Linux_without_SKUA_DIR_the_data_folder_is_in_a_config_folder_that_doesnt_exist_yet()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux's data folder is in ~/.config.");
        await using EngineSandbox sandbox = new();
        string home = Path.Combine(sandbox.SkuaDir, "home");
        Directory.CreateDirectory(home);
        Dictionary<string, string> environment = new()
        {
            ["HOME"] = home,
            ["XDG_CONFIG_HOME"] = "",
            [EngineEndpoint.SkuaDirVariable] = "",
            [Keychain.ToolVariable] = "",
        };

        ProcessResult add = await sandbox.RunCliWithInputAsync(environment, "Alt1\nhunter2\n", "account", "add");

        Assert.True(add.ExitCode == 0, add.Stderr);
        Assert.True(File.Exists(Path.Combine(home, ".config", "Skua", Keychain.CredentialsFolder, "skua-account-alt1")));
        Assert.Contains(AccountSetting.Key, await File.ReadAllTextAsync(Path.Combine(home, ".config", "Skua", "Skua.settings.json"), Ct));
        Assert.False(Directory.Exists(Path.Combine(sandbox.SkuaDir, "Skua")), "The data folder was made in the working directory.");
    }
}
