using System.Text.Json;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>
/// <c>skua engine up</c> (#285): a windowless Engine for each named account, logged in as the account named after it, against sandboxed Engines
/// on the fake Game Host, servers API and Keychain.
/// </summary>
public class EngineUpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Up_starts_and_logs_in_each_Engine_as_its_own_account_and_a_failure_leaves_the_others_up()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        keychain.Add("skua-account-alt1", "Alt1User", "alt1-password");
        keychain.Add("skua-account-alt2", "Alt2User", "alt2-password");
        // The game doesn't take this one's password.
        keychain.Add("skua-account-alt3", "Alt3User", "not-its-password");
        Dictionary<string, string> environment = GameFixture.Environment(
            new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers).Account("Alt1User", "alt1-password").Account("Alt2User", "alt2-password")
                .Account("Alt3User", "alt3-password"), api, keychain);
        string[] names = ["alt1", "alt2", "alt3"];
        try
        {
            ProcessResult up = await sandbox.RunCliAsync(environment, "engine", "up", "alt1", "alt2", "alt3", "--server", "Galanoth", "--timeout", "10");
            string[] lines = up.Stdout.TrimEnd().Split('\n');
            Assert.True(up.ExitCode == ExitCodes.Failure, up.Stdout + up.Stderr);
            Assert.Equal(3, lines.Length);
            Assert.Equal("alt1  started, logged in as Alt1User on Galanoth.", lines[0]);
            Assert.Equal("alt2  started, logged in as Alt2User on Galanoth.", lines[1]);
            Assert.StartsWith("alt3  started, then failed: ", lines[2]);

            int[] pids = await Task.WhenAll(names.Take(2).Select(name => PidAsync(sandbox, name)));
            ProcessResult again = await sandbox.RunCliAsync(environment, "engine", "up", "alt1", "alt2", "--server", "Galanoth", "--json");

            Assert.True(again.ExitCode == 0, again.Stdout + again.Stderr);
            using JsonDocument json = JsonDocument.Parse(again.Stdout);
            JsonElement[] results = [.. json.RootElement.EnumerateArray()];
            Assert.Equal(["alt1", "alt2"], results.Select(r => r.GetProperty("name").GetString()));
            Assert.All(results, r => Assert.False(r.GetProperty("started").GetBoolean()));
            Assert.All(results, r => Assert.True(r.GetProperty("login").GetProperty("alreadyLoggedIn").GetBoolean()));
            Assert.Equal(pids, await Task.WhenAll(names.Take(2).Select(name => PidAsync(sandbox, name))));
        }
        finally
        {
            foreach (string name in names)
                await EngineClient.StopAsync(EngineEndpoint.Resolve(name, sandbox.SkuaDir), EngineSandbox.StopTimeout);
        }
    }

    [Fact]
    public async Task Up_without_names_brings_up_every_Skua_Manager_account_and_starts_no_Engine_for_an_account_Keychain_lacks()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        keychain.Add("skua-account-alt1", "Alt1User", "alt1-password");
        keychain.Add("skua-account-alt2", "Alt2User", "alt2-password");
        Dictionary<string, string> environment = GameFixture.Environment(
            new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers).Account("Alt1User", "alt1-password").Account("Alt2User", "alt2-password"),
            api, keychain);
        File.WriteAllText(Path.Combine(sandbox.SkuaDir, ManagerAccounts.FileName), """
            {"accounts": [
              {"name": "alt1", "username": "Alt1User", "displayName": "Alt1User", "tags": []},
              {"name": "alt2", "username": "Alt2User", "displayName": "Alt2User", "tags": []}
            ]}
            """);
        try
        {
            ProcessResult up = await sandbox.RunCliAsync(environment, "engine", "up");
            ProcessResult missing = await sandbox.RunCliAsync(environment, "engine", "up", "alt9");
            ProcessResult invalid = await sandbox.RunCliAsync(environment, "engine", "up", "Not Valid");

            Assert.True(up.ExitCode == 0, up.Stdout + up.Stderr);
            Assert.Matches(@"^alt1  started, logged in as Alt1User on [^\n]+\.\nalt2  started, logged in as Alt2User on [^\n]+\.$", up.Stdout.TrimEnd());
            Assert.Equal(ExitCodes.Failure, missing.ExitCode);
            Assert.StartsWith("alt9  failed: No account is named 'alt9' in Keychain", missing.Stdout);
            Assert.DoesNotContain("alt9", EngineEndpoint.InDataFolder(sandbox.SkuaDir).Select(e => e.Name));
            Assert.Equal((1, true), (invalid.ExitCode, invalid.Stderr.Contains("'Not Valid' is invalid")));
        }
        finally
        {
            foreach (string name in new[] { "alt1", "alt2" })
                await EngineClient.StopAsync(EngineEndpoint.Resolve(name, sandbox.SkuaDir), EngineSandbox.StopTimeout);
        }
    }

    [Fact]
    public async Task Up_without_names_and_no_Skua_Manager_accounts_says_to_name_them()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult up = await sandbox.RunCliAsync("engine", "up");

        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), up.ExitCode);
        Assert.Contains("skua engine up alt1 alt2", up.Stderr);
        Assert.Empty(EngineEndpoint.InDataFolder(sandbox.SkuaDir));
    }

    private static async Task<int> PidAsync(EngineSandbox sandbox, string name)
    {
        using EngineConnection connection = (await EngineClient.TryConnectAsync(EngineEndpoint.Resolve(name, sandbox.SkuaDir), Ct))!;
        return connection.Hello.Pid;
    }
}
