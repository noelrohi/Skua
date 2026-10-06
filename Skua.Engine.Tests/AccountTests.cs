using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.RegularExpressions;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary><c>skua account</c>: accounts in the fake Keychain, and the login that uses the active one.</summary>
public class AccountTests
{
    private const string MainUser = "MainUser";

    // Quotes, a backslash, shell syntax and a non-ASCII letter: none of it may reach a command line or be mangled on the way to Keychain.
    private const string MainPassword = "Pa55 \"wörd\" $HOME `id` \\ 'end'";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The three-step flow, for a developer who already has a Test Account: add, login, start a Script and follow it.</summary>
    [Fact]
    public async Task Account_add_then_login_logs_in_with_that_account_without_an_Engine_restart_and_the_password_appears_nowhere()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        FakeGameHost gameHost = new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers).Account(MainUser, MainPassword);
        Dictionary<string, string> environment = GameFixture.Environment(gameHost, api, keychain);
        ProcessResult started = await sandbox.RunCliAsync(environment, "engine", "start", "--json");
        int pid = JsonDocument.Parse(started.Stdout).RootElement.GetProperty("pid").GetInt32();

        TestScripts.Write(sandbox, "Tests/Hello.cs", TestScripts.Main("bot.Log($\"hello {bot.Player.Username}\");"));

        ProcessResult add = await sandbox.RunCliWithInputAsync(environment, $"{MainUser}\n{MainPassword}\n", "account", "add");
        ProcessResult login = await sandbox.RunCliAsync(environment, "login", "Galanoth");
        ProcessResult status = await sandbox.RunCliAsync(environment, "status", "--json");
        ProcessResult follow = await sandbox.RunCliAsync(environment, "script", "start", "Tests/Hello.cs", "--follow");
        // The game's own trace of the password, which the Engine must redact.
        await gameHost.DoAsync($"send F debug: password is {MainPassword}");
        await gameHost.DoAsync($"send F debug: again {MainPassword}");
        ProcessResult logs = await sandbox.RunCliAsync(environment, "logs", "flash", "--json");
        ProcessResult events = await sandbox.RunCliAsync(environment, "logs", "events", "--max", "1000", "--json");
        ProcessResult show = await sandbox.RunCliAsync(environment, "account", "show");
        await sandbox.RunCliAsync(environment, "engine", "stop");

        Assert.True(add.ExitCode == 0, add.Stderr);
        Assert.Equal("Added account mainuser (MainUser) to Keychain as 'skua-account-mainuser'; 'skua login' now uses it.", add.Stdout.Trim());
        Assert.Contains("Username: ", add.Stderr);
        Assert.Contains("Password: ", add.Stderr);
        Assert.Equal((0, "Logged in as MainUser on Galanoth."), (login.ExitCode, login.Stdout.Trim()));
        using JsonDocument statusJson = JsonDocument.Parse(status.Stdout);
        Assert.Equal(pid, statusJson.RootElement.GetProperty("engine").GetProperty("pid").GetInt32());
        Assert.Equal(MainUser, statusJson.RootElement.GetProperty("game").GetProperty("player").GetProperty("name").GetString());
        Assert.True(follow.ExitCode == 0, follow.Stderr);
        Assert.Contains($"hello {MainUser}", follow.Stdout);
        Assert.Equal((MainUser, MainPassword), keychain.Find("skua-account-mainuser") is { } item ? (item.Account, Unquote(item.PasswordLine)) : default);
        Assert.Contains("Account mainuser (active): MainUser, Keychain service 'skua-account-mainuser'", show.Stdout);
        // The Test Account's slot is untouched.
        Assert.Equal(("SkuaTester", "password: \"hunter2-Sekrit!\""), keychain.Find(FakeKeychain.DefaultService));
        Assert.Equal(2, Regex.Count(logs.Stdout, Regex.Escape("[redacted]")));
        Assert.Contains("debug: password is [redacted]", logs.Stdout);

        // The password reached security only on its standard input: never on a command line, where ps and shell history would see it.
        // Every file but the fakes' own: the settings, the Engine's log files and the fake security's log of its command lines.
        string files = string.Concat(Directory.EnumerateFiles(sandbox.SkuaDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(Path.Combine(sandbox.SkuaDir, "fake-"), StringComparison.Ordinal) || f == keychain.Log)
            .Select(File.ReadAllText));
        Assert.Contains("-i", File.ReadAllLines(keychain.Log));
        foreach (string text in new[] { add.Stdout, add.Stderr, login.Stdout, login.Stderr, status.Stdout, follow.Stdout, logs.Stdout, events.Stdout, show.Stdout, files })
            Assert.Equal(0, Regex.Count(text, Regex.Escape(MainPassword)));
    }

    [Fact]
    public async Task Show_use_and_remove_switch_between_accounts_and_leave_the_Test_Account_alone()
    {
        await using EngineSandbox sandbox = new();
        FakeKeychain keychain = new(sandbox);
        IDictionary<string, string> environment = keychain.Environment();

        ProcessResult test = await sandbox.RunCliAsync(environment, "account", "show", "--json");
        ProcessResult add = await sandbox.RunCliWithInputAsync(environment, $"{MainPassword}\n", "account", "add", MainUser, "--name", "main", "--json");
        ProcessResult main = await sandbox.RunCliAsync(environment, "account", "show");
        ProcessResult useTest = await sandbox.RunCliAsync(environment, "account", "use", "test");
        string serviceWhileTest = AccountSetting.Read(sandbox.SkuaDir);
        ProcessResult showMain = await sandbox.RunCliAsync(environment, "account", "show", "main", "--json");
        ProcessResult useMain = await sandbox.RunCliAsync(environment, "account", "use", "main");
        string serviceWhileMain = AccountSetting.Read(sandbox.SkuaDir);
        ProcessResult remove = await sandbox.RunCliAsync(environment, "account", "remove");
        ProcessResult afterRemove = await sandbox.RunCliAsync(environment, "account", "show", "--json");
        ProcessResult removeAgain = await sandbox.RunCliAsync(environment, "account", "remove", "main", "--json");
        ProcessResult useMissing = await sandbox.RunCliAsync(environment, "account", "use", "nobody");
        ProcessResult badName = await sandbox.RunCliAsync(environment, "account", "use", "Not A Name");

        Assert.True(test.ExitCode == 0, test.Stderr);
        Assert.Equal(new AccountDto("test", "skua-test-account", "SkuaTester", Active: true, AllowAgents: true), Account(test));
        Assert.True(add.ExitCode == 0, add.Stderr);
        Assert.DoesNotContain("Username: ", add.Stderr);
        Assert.Equal(new AccountDto("main", "skua-account-main", MainUser, Active: true, AllowAgents: false), Account(add));
        Assert.Contains("Account main (active): MainUser, Keychain service 'skua-account-main'", main.Stdout);
        Assert.Equal((0, "Now using account test (SkuaTester); 'skua login' uses it."), (useTest.ExitCode, useTest.Stdout.Trim()));
        Assert.Equal("skua-test-account", serviceWhileTest);
        Assert.Equal(new AccountDto("main", "skua-account-main", MainUser, Active: false, AllowAgents: false), Account(showMain));
        Assert.Equal(0, useMain.ExitCode);
        Assert.Equal("skua-account-main", serviceWhileMain);
        Assert.Equal((0, "Removed account main (MainUser) from Keychain; 'skua login' uses the Test Account again."), (remove.ExitCode, remove.Stdout.Trim()));
        Assert.Null(keychain.Find("skua-account-main"));
        Assert.Equal(new AccountDto("test", "skua-test-account", "SkuaTester", Active: true, AllowAgents: true), Account(afterRemove));
        Assert.Equal(ExitCodes.For(ErrorCode.AccountNotFound), removeAgain.ExitCode);
        Assert.Equal("accountNotFound", JsonDocument.Parse(removeAgain.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(ExitCodes.For(ErrorCode.AccountNotFound), useMissing.ExitCode);
        Assert.Contains("skua account add --name nobody", useMissing.Stderr);
        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), badName.ExitCode);
        // The Test Account's own item is untouched throughout.
        Assert.Equal(("SkuaTester", "password: \"hunter2-Sekrit!\""), keychain.Find(FakeKeychain.DefaultService));
    }

    [Fact]
    public async Task Plain_add_makes_a_personal_account_and_the_Test_Account_changes_only_with_test_or_its_name_and_replace()
    {
        await using EngineSandbox sandbox = new();
        FakeKeychain keychain = new(sandbox);
        IDictionary<string, string> environment = keychain.Environment();

        ProcessResult personal = await sandbox.RunCliWithInputAsync(environment, "main-password\n", "account", "add", "Main User", "--json");
        ProcessResult byName = await sandbox.RunCliWithInputAsync(environment, "other-password\n", "account", "add", "Other", "--name", "test");
        (string, string)? kept = keychain.Find(FakeKeychain.DefaultService);
        ProcessResult test = await sandbox.RunCliWithInputAsync(environment, "other-password\n", "account", "add", "Other", "--test");
        (string, string)? replacedByTest = keychain.Find(FakeKeychain.DefaultService);
        string activeAfterTest = AccountSetting.Read(sandbox.SkuaDir);
        ProcessResult byNameReplace = await sandbox.RunCliWithInputAsync(environment, "third-password\n", "account", "add", "Third", "--name", "test", "--replace");
        ProcessResult again = await sandbox.RunCliWithInputAsync(environment, "x\n", "account", "add", "Main-User");
        ProcessResult named = await sandbox.RunCliWithInputAsync(environment, "x\n", "account", "add", "test");
        ProcessResult agentsOnTest = await sandbox.RunCliWithInputAsync(environment, "x\n", "account", "add", "Other", "--test", "--allow-agents");

        Assert.True(personal.ExitCode == 0, personal.Stderr);
        Assert.Equal(new AccountDto("main-user", "skua-account-main-user", "Main User", Active: true, AllowAgents: false), Account(personal));
        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), byName.ExitCode);
        Assert.Contains("skua account add --test", byName.Stderr);
        Assert.Equal(("SkuaTester", "password: \"hunter2-Sekrit!\""), kept);
        Assert.True(test.ExitCode == 0, test.Stderr);
        // Storing the Test Account leaves the developer's active account as it is.
        Assert.Equal("Added account test (Other) to Keychain as 'skua-test-account'; agents' logins use it.", test.Stdout.Trim());
        Assert.Equal(("Other", "password: \"other-password\""), replacedByTest);
        Assert.Equal("skua-account-main-user", activeAfterTest);
        Assert.True(byNameReplace.ExitCode == 0, byNameReplace.Stderr);
        Assert.Equal(("Third", "password: \"third-password\""), keychain.Find(FakeKeychain.DefaultService));
        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), again.ExitCode);
        Assert.Contains("--replace", again.Stderr);
        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), named.ExitCode);
        Assert.Contains("--name", named.Stderr);
        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), agentsOnTest.ExitCode);
    }

    [Fact]
    public async Task An_agents_login_uses_the_Test_Account_unless_the_active_account_allows_agents()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        Dictionary<string, string> environment = GameFixture.Environment(
            new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers).Account(MainUser, MainPassword).Account("AltUser", "alt-password"), api, keychain);
        await using McpClient client = await McpTests.ConnectAsync(sandbox, environment);

        await sandbox.RunCliWithInputAsync(environment, $"{MainPassword}\n", "account", "add", MainUser, "--name", "main");
        string agentWhileMain = await McpLoginAsync(client);
        ProcessResult cliWhileMain = await sandbox.RunCliAsync(environment, "login", "Galanoth", "--json");
        ProcessResult allowed = await sandbox.RunCliWithInputAsync(environment, "alt-password\n", "account", "add", "AltUser", "--name", "alt", "--allow-agents");
        ProcessResult show = await sandbox.RunCliAsync(environment, "account", "show");
        string agentWhileAlt = await McpLoginAsync(client);
        await sandbox.RunCliAsync(environment, "account", "use", "main");
        string agentBackOnMain = await McpLoginAsync(client);
        string player = (await client.CallToolAsync("status", cancellationToken: Ct)).StructuredContent!.Value
            .GetProperty("game").GetProperty("player").GetProperty("name").GetString()!;

        Assert.Equal("Logged in as SkuaTester (the Test Account) on Galanoth.", agentWhileMain);
        Assert.Equal(MainUser, JsonDocument.Parse(cliWhileMain.Stdout).RootElement.GetProperty("username").GetString());
        Assert.True(allowed.ExitCode == 0, allowed.Stderr);
        Assert.Contains("and so may agents' while it is active", allowed.Stdout);
        Assert.Contains("Account alt (active, agents allowed): AltUser", show.Stdout);
        Assert.Equal("Logged in as AltUser on Galanoth.", agentWhileAlt);
        // Allowed only while it is the active one.
        Assert.Equal("Logged in as SkuaTester (the Test Account) on Galanoth.", agentBackOnMain);
        Assert.Equal("SkuaTester", player);
        Assert.Equal(AccountSetting.AllowAgentsComment, keychain.Comment("skua-account-alt"));
    }

    /// <returns>The reply's sentence, which names the account.</returns>
    private static async Task<string> McpLoginAsync(McpClient client, string? account = null)
    {
        Dictionary<string, object?> arguments = new() { ["server"] = "Galanoth" };
        if (account is not null)
            arguments["account"] = account;
        CallToolResult login = await client.CallToolAsync("login", arguments, cancellationToken: Ct);
        Assert.NotEqual(true, login.IsError);
        string text = ((TextContentBlock)login.Content[1]).Text;
        Assert.Contains(login.StructuredContent!.Value.GetProperty("username").GetString()!, text);
        return text;
    }

    [Fact]
    public async Task Removing_the_Test_Account_takes_its_name()
    {
        await using EngineSandbox sandbox = new();
        FakeKeychain keychain = new(sandbox);

        ProcessResult unnamed = await sandbox.RunCliAsync(keychain.Environment(), "account", "remove");
        (string, string)? kept = keychain.Find(FakeKeychain.DefaultService);
        ProcessResult named = await sandbox.RunCliAsync(keychain.Environment(), "account", "remove", "test");

        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), unnamed.ExitCode);
        Assert.Contains("skua account remove test", unnamed.Stderr);
        Assert.NotNull(kept);
        Assert.True(named.ExitCode == 0, named.Stderr);
        Assert.Null(keychain.Find(FakeKeychain.DefaultService));
    }

    [Fact]
    public async Task Account_add_keeps_the_rest_of_the_settings_file()
    {
        await using EngineSandbox sandbox = new();
        FakeKeychain keychain = new(sandbox);
        string settings = Path.Combine(sandbox.SkuaDir, "Skua.settings.json");
        File.WriteAllText(settings, """{"shared":{"ScriptSource":{"Owner":"noelrohi","Repo":"Scripts","Branch":"Skua"}},"client":{"AnimationFrameRate":24}}""");

        ProcessResult add = await sandbox.RunCliWithInputAsync(keychain.Environment(), "alt-password\n", "account", "add", "AltUser", "--name", "alt");

        Assert.True(add.ExitCode == 0, add.Stderr);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.Equal("noelrohi", json.RootElement.GetProperty("shared").GetProperty("ScriptSource").GetProperty("Owner").GetString());
        Assert.Equal(24, json.RootElement.GetProperty("client").GetProperty("AnimationFrameRate").GetInt32());
        Assert.Equal("skua-account-alt", json.RootElement.GetProperty("client").GetProperty("TestAccountService").GetString());
    }

    [Fact]
    public async Task Account_add_refuses_an_empty_password()
    {
        await using EngineSandbox sandbox = new();
        FakeKeychain keychain = new(sandbox, service: null);

        ProcessResult add = await sandbox.RunCliWithInputAsync(keychain.Environment(), "\n", "account", "add", MainUser);

        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), add.ExitCode);
        Assert.Contains("password", add.Stderr);
        Assert.Null(keychain.Find(FakeKeychain.DefaultService));
        Assert.False(File.Exists(Path.Combine(sandbox.SkuaDir, "Skua.settings.json")));
    }

    [Fact]
    public async Task Account_add_in_a_terminal_hides_the_password_as_it_is_typed()
    {
        await using EngineSandbox sandbox = new();
        FakeKeychain keychain = new(sandbox, service: null);

        Process add = sandbox.StartCliInTerminal(keychain.Environment(), "account", "add", "--name", "main");
        OutputReader output = new(add.StandardOutput);
        await output.WaitForAsync("Username: ");
        await add.StandardInput.WriteAsync($"{MainUser}\n");
        await output.WaitForAsync("Password: ");
        await add.StandardInput.WriteAsync("typed-s3cret\n");
        await add.WaitForExitAsync(Ct);
        string text = await output.EndAsync();

        Assert.Equal(0, add.ExitCode);
        Assert.Contains(MainUser, text);
        Assert.DoesNotContain("typed-s3cret", text);
        Assert.Contains("Added account main (MainUser)", text);
        Assert.Equal("password: \"typed-s3cret\"", keychain.Find("skua-account-main")?.PasswordLine);
    }

    [Fact]
    public async Task A_login_after_switching_accounts_relogs_with_the_new_one()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, fake => fake.Account(MainUser, MainPassword));
        session.Keychain.Add("skua-account-main", MainUser, MainPassword);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        string before = (await session.Connection.StatusAsync(Ct)).Game.Player!.Name;

        AccountSetting.Write(sandbox.SkuaDir, "skua-account-main");
        LoginResult switched = await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        LoginResult again = await session.Connection.LoginAsync(cancellationToken: Ct);
        string after = (await session.Connection.StatusAsync(Ct)).Game.Player!.Name;

        Assert.Equal("SkuaTester", before);
        Assert.Equal(new LoginResult("Galanoth", AlreadyLoggedIn: false, MainUser, IsTestAccount: false), switched);
        Assert.Equal(new LoginResult("Galanoth", AlreadyLoggedIn: true, MainUser, IsTestAccount: false), again);
        Assert.Equal(MainUser, after);
    }

    [Fact]
    public async Task A_login_naming_an_account_uses_its_credentials_and_one_without_still_uses_the_active_account()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, fake => fake.Account("AliceUser", "alice-pw").Account("BobUser", "bob-pw"));
        session.Keychain.Add("skua-account-alice", "AliceUser", "alice-pw");
        session.Keychain.Add("skua-account-bob", "BobUser", "bob-pw");

        LoginResult alice = await session.Connection.LoginAsync("Galanoth", null, "alice", Ct);
        LoginResult bob = await session.Connection.LoginAsync("Galanoth", null, "bob", Ct);
        LoginResult bobAgain = await session.Connection.LoginAsync(null, null, "bob", Ct);
        LoginResult active = await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        LoginResult test = await session.Connection.LoginAsync("Galanoth", null, "test", Ct);

        Assert.Equal(new LoginResult("Galanoth", AlreadyLoggedIn: false, "AliceUser", IsTestAccount: false), alice);
        Assert.Equal(new LoginResult("Galanoth", AlreadyLoggedIn: false, "BobUser", IsTestAccount: false), bob);
        Assert.Equal(new LoginResult("Galanoth", AlreadyLoggedIn: true, "BobUser", IsTestAccount: false), bobAgain);
        Assert.Equal(new LoginResult("Galanoth", AlreadyLoggedIn: false, "SkuaTester", IsTestAccount: true), active);
        Assert.Equal(new LoginResult("Galanoth", AlreadyLoggedIn: true, "SkuaTester", IsTestAccount: true), test);
        // Naming an account doesn't make it the Active Account.
        Assert.Equal(AccountSetting.DefaultService, AccountSetting.Read(sandbox.SkuaDir));
    }

    [Theory]
    [InlineData("nobody", ErrorCode.AccountNotFound, "No account is named 'nobody'")]
    [InlineData("Not A Name", ErrorCode.InvalidArgument, "Account name 'Not A Name' is invalid")]
    public async Task A_login_naming_an_unknown_account_fails_before_reading_a_password(string account, ErrorCode code, string message)
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);

        ControlException e = await Assert.ThrowsAsync<ControlException>(() => session.Connection.LoginAsync("Galanoth", null, account, Ct));

        Assert.Equal(code, e.Code);
        Assert.Contains(message, e.Message);
        Assert.Equal(0, session.Keychain.Reads);
        Assert.Equal(GameState.LoginScreen, (await session.Connection.StatusAsync(Ct)).Game.State);
    }

    [Fact]
    public async Task An_agent_naming_an_account_gets_only_the_Test_Account_or_one_that_allows_agents()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, fake => fake.Account(MainUser, MainPassword).Account("AltUser", "alt-password"));
        session.Keychain.Add("skua-account-main", MainUser, MainPassword);
        session.Keychain.Add("skua-account-alt", "AltUser", "alt-password", AccountSetting.AllowAgentsComment);
        // Even while it is active, an agent may not name it.
        AccountSetting.Write(sandbox.SkuaDir, "skua-account-main");

        ControlException refused = await Assert.ThrowsAsync<ControlException>(() => session.Connection.AgentLoginAsync("Galanoth", null, "main", Ct));
        int readsAfterRefusal = session.Keychain.Reads;
        LoginResult alt = await session.Connection.AgentLoginAsync("Galanoth", null, "alt", Ct);
        LoginResult test = await session.Connection.AgentLoginAsync("Galanoth", null, "test", Ct);

        Assert.Equal(ErrorCode.InvalidArgument, refused.Code);
        Assert.Contains("'main'", refused.Message);
        Assert.Contains("--allow-agents", refused.Message);
        Assert.Equal(0, readsAfterRefusal);
        Assert.Equal(new LoginResult("Galanoth", AlreadyLoggedIn: false, "AltUser", IsTestAccount: false), alt);
        Assert.Equal(new LoginResult("Galanoth", AlreadyLoggedIn: false, "SkuaTester", IsTestAccount: true), test);
    }

    [Fact]
    public async Task Skua_login_and_the_MCP_login_tool_take_an_account()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        keychain.Add("skua-account-main", MainUser, MainPassword);
        Dictionary<string, string> environment = GameFixture.Environment(
            new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers).Account(MainUser, MainPassword), api, keychain);
        await using McpClient client = await McpTests.ConnectAsync(sandbox, environment);

        ProcessResult cli = await sandbox.RunCliAsync(environment, "login", "Galanoth", "--account", "main");
        ProcessResult unknown = await sandbox.RunCliAsync(environment, "login", "--account", "nobody");
        CallToolResult agent = await client.CallToolAsync(
            "login", new Dictionary<string, object?> { ["server"] = "Galanoth", ["account"] = "main" }, cancellationToken: Ct);
        string agentTest = await McpLoginAsync(client, "test");

        Assert.Equal((0, "Logged in as MainUser on Galanoth."), (cli.ExitCode, cli.Stdout.Trim()));
        Assert.Equal(ExitCodes.For(ErrorCode.AccountNotFound), unknown.ExitCode);
        Assert.Contains("'nobody'", unknown.Stderr);
        Assert.True(agent.IsError);
        Assert.Contains("--allow-agents", ((TextContentBlock)agent.Content[0]).Text);
        Assert.Equal("Logged in as SkuaTester (the Test Account) on Galanoth.", agentTest);
    }

    private static AccountDto Account(ProcessResult result) => JsonSerializer.Deserialize<AccountDto>(result.Stdout, ControlJson.Options)!;

    private static string Unquote(string passwordLine) => passwordLine["password: \"".Length..^1];
}
