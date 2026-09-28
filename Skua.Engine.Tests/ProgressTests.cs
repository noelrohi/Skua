using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>The live progress view of <c>skua script start --follow</c> and <c>skua watch</c>, against the fake game's XP and gold.</summary>
public class ProgressTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Watch_without_a_terminal_prints_a_line_per_interval_as_the_XP_and_gold_change()
    {
        await using EngineSandbox sandbox = new();
        (FakeGameHost gameHost, Dictionary<string, string> environment, FakeAqApi api) = Game(sandbox);
        await using (api)
        {
            await sandbox.RunCliAsync(environment, "login", "Galanoth");
            TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);
            await sandbox.RunCliAsync(environment, "script", "start", "Tests/Loop.cs", "--no-update");

            Process watch = sandbox.StartCli(environment, "watch", "--interval", "1");
            OutputReader output = new(watch.StandardOutput);
            await output.WaitForAsync("XP 37.5%");
            await gameHost.DoAsync("gain 250 1200");
            await output.WaitForAsync("XP 43.8%");
            Process json = sandbox.StartCli(environment, "watch", "--interval", "1", "--json");
            string line = (await json.StandardOutput.ReadLineAsync(Timeout()))!;

            string text = output.Text;
            Assert.Matches(@"\d\d:\d\d:\d\d Level 10 · XP 37\.5% · 5,000 gold \(\+0\) · battleon · run 1 · \d+ s", text);
            Assert.Matches(@"Level 10 · XP 43\.8% · 6,200 gold \(\+1,200\) · battleon · run 1 · \d+ s", text);
            Assert.False(text.Contains('\u001b'), text.Replace("\u001b", "<ESC>"));
            using JsonDocument progress = JsonDocument.Parse(line);
            Assert.Equal(10, progress.RootElement.GetProperty("level").GetInt32());
            Assert.Equal(43.8, progress.RootElement.GetProperty("xpPercent").GetDouble());
            Assert.Equal(6200, progress.RootElement.GetProperty("gold").GetInt32());
            Assert.Equal(0, progress.RootElement.GetProperty("goldGained").GetInt32());
            Assert.Equal(1, progress.RootElement.GetProperty("run").GetProperty("number").GetInt32());
        }
    }

    [Fact]
    public async Task Script_start_follow_in_a_terminal_keeps_a_live_status_line_under_the_log()
    {
        await using EngineSandbox sandbox = new();
        (FakeGameHost gameHost, Dictionary<string, string> environment, FakeAqApi api) = Game(sandbox);
        await using (api)
        {
            await sandbox.RunCliAsync(environment, "login", "Galanoth");
            TestScripts.Write(sandbox, "Tests/Chatty.cs", ChattyScript);

            Process follow = sandbox.StartCliInTerminal(environment, "script", "start", "Tests/Chatty.cs", "--follow", "--no-update");
            OutputReader output = new(follow.StandardOutput);
            await output.WaitForAsync("tick 1");
            await output.WaitForAsync("XP 37.5% · 5,000 gold (+0) · battleon · run 1");
            await gameHost.DoAsync("gain 250 1200");
            await output.WaitForAsync("XP 43.8% · 6,200 gold (+1,200)");
            // Ticks logged before the first status reply have no status line to clear, and a loaded machine can log several; so the
            // tick checked is the first one logged once the status line shows.
            int statusShown = output.Text.IndexOf("XP 43.8%", StringComparison.Ordinal);
            string after = await output.WaitForAsync(new Regex(@"tick \d+\r?\n\r\u001b\[2KLevel 10 · XP"), statusShown);
            follow.Kill();

            // Each log line clears the status line first, and the status line is redrawn after it.
            string tick = Regex.Match(after, @"tick \d+").Value;
            Assert.Matches($@"\r\u001b\[2K{tick}\r?\n", after);
        }
    }

    [Fact]
    public async Task Watch_in_a_terminal_shows_the_running_Scripts_log_and_Ctrl_C_leaves_the_Script_running()
    {
        await using EngineSandbox sandbox = new();
        (FakeGameHost gameHost, Dictionary<string, string> environment, FakeAqApi api) = Game(sandbox);
        await using (api)
        {
            await sandbox.RunCliAsync(environment, "login", "Galanoth");
            TestScripts.Write(sandbox, "Tests/Chatty.cs", ChattyScript);
            await sandbox.RunCliAsync(environment, "script", "start", "Tests/Chatty.cs", "--no-update");

            Process watch = sandbox.StartCliInTerminal(environment, "watch");
            OutputReader output = new(watch.StandardOutput);
            await output.WaitForAsync("Level 10 · XP 37.5%");
            await output.WaitForAsync("tick 2");
            await watch.StandardInput.WriteAsync("\u0003");
            await watch.WaitForExitAsync(Timeout());
            string text = await output.EndAsync();
            ProcessResult status = await sandbox.RunCliAsync(environment, "script", "status", "--json");

            Assert.Contains("stopped watching; the Script still runs", text);
            Assert.Equal("running", JsonDocument.Parse(status.Stdout).RootElement.GetProperty("state").GetString());
        }
    }

    [Fact]
    public async Task Watch_before_login_says_the_game_isnt_playing()
    {
        await using EngineSandbox sandbox = new();

        Process watch = sandbox.StartCli("watch", "--interval", "1");
        string line = (await watch.StandardOutput.ReadLineAsync(Timeout()))!;

        Assert.Matches(@"^\d\d:\d\d:\d\d Not playing \(\w+\)$", line);
    }

    /// <summary>A Script that logs a tick every 300 ms until it is stopped.</summary>
    private static string ChattyScript { get; } = TestScripts.Main("""
        for (int i = 1; !bot.ShouldExit; i++)
        {
            bot.Log($"tick {i}");
            Thread.Sleep(300);
        }
        """);

    private static (FakeGameHost GameHost, Dictionary<string, string> Environment, FakeAqApi Api) Game(EngineSandbox sandbox)
    {
        FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        FakeGameHost gameHost = new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers);
        return (gameHost, GameFixture.Environment(gameHost, api, keychain), api);
    }

    private static CancellationToken Timeout()
    {
        CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return timeout.Token;
    }
}
