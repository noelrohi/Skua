using System.Diagnostics;
using System.Text;
using Skua.MacOS.GameHost;

namespace Skua.Engine.Tests;

/// <summary>The Engine's end of the Bridge, driven against the fake Game Host.</summary>
public class BridgeTests
{
    [Fact]
    public async Task Each_call_gets_its_own_reply_even_when_replies_come_out_of_order()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost fake = new FakeGameHost(sandbox)
            .Reply("slow", "<string>slow</string>").Delay("slow", 500)
            .Reply("fast", "<string>fast</string>");
        using GameHostProcess gameHost = Start(fake);

        Task<string> slow = Task.Run(() => gameHost.Call(Invoke("slow")), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        string fast = gameHost.Call(Invoke("fast"));

        Assert.Equal("<string>fast</string>", fast);
        Assert.False(slow.IsCompleted);
        Assert.Equal("<string>slow</string>", await slow);
    }

    [Fact]
    public async Task Concurrent_calls_each_get_their_own_reply()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost fake = new(sandbox);
        for (int i = 0; i < 20; i++)
            fake.Reply($"f{i}", $"<number>{i}</number>").Delay($"f{i}", (20 - i) * 10);
        using GameHostProcess gameHost = Start(fake);

        string[] replies = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => gameHost.Call(Invoke($"f{i}")))));

        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"<number>{i}</number>"), replies);
    }

    [Fact]
    public async Task Game_Client_calls_are_dispatched_in_order_on_one_thread_and_may_call_back()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost fake = new FakeGameHost(sandbox).Reply("echo", "<true/>");
        for (int i = 0; i < 50; i++)
            fake.Send('E', Invoke($"event{i}"));
        using GameHostProcess gameHost = new(EngineSandbox.FakeGameHostExecutable, [fake.Write()]);
        List<(string Request, int Thread, string Reply)> received = [];
        TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gameHost.Invoked += request =>
        {
            string reply = gameHost.Call(Invoke("echo"));
            lock (received)
            {
                received.Add((request, Environment.CurrentManagedThreadId, reply));
                if (received.Count == 50)
                    all.SetResult();
            }
        };

        gameHost.Start();
        await all.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(0, 50).Select(i => Invoke($"event{i}")), received.Select(r => r.Request));
        Assert.Single(received.Select(r => r.Thread).Distinct());
        Assert.All(received, r => Assert.Equal("<true/>", r.Reply));
    }

    [Fact]
    public async Task A_pending_call_fails_as_soon_as_the_Game_Host_exits()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost fake = new FakeGameHost(sandbox).Delay("never", 60_000).Sleep(300).Exit(1);
        using GameHostProcess gameHost = Start(fake);
        Stopwatch waited = Stopwatch.StartNew();

        Assert.Throws<IOException>(() => gameHost.Call(Invoke("never")));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"The call failed only after {waited.Elapsed.TotalSeconds:0.0} s.");
        Assert.Throws<IOException>(() => gameHost.Call(Invoke("after")));
    }

    [Fact]
    public async Task Pings_and_stats_are_answered_by_id()
    {
        await using EngineSandbox sandbox = new();
        using GameHostProcess gameHost = Start(new FakeGameHost(sandbox));

        byte[] pong = gameHost.Request('P', [], TimeSpan.FromSeconds(5));
        byte[] stats = gameHost.Request('Q', [], TimeSpan.FromSeconds(5));

        Assert.Empty(pong);
        Assert.Equal("{}", Encoding.UTF8.GetString(stats));
    }

    [Fact]
    public async Task Registered_callbacks_and_log_lines_are_reported()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost fake = new FakeGameHost(sandbox)
            .Send('X', "isNull").Send('X', "loadClient")
            .Send('F', "[Game] trace line")
            .Send('L', "\u0002WARN ruffle: a warning")
            .Send('E', Invoke("done"));
        using GameHostProcess gameHost = new(EngineSandbox.FakeGameHostExecutable, [fake.Write()]);
        List<string> flash = [];
        List<string> logs = [];
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gameHost.FlashLog += flash.Add;
        gameHost.LogLine += logs.Add;
        gameHost.Invoked += _ => done.TrySetResult();

        gameHost.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["isNull", "loadClient"], gameHost.Callbacks);
        Assert.Equal(["[Game] trace line"], flash);
        Assert.Contains("WARN ruffle: a warning", logs);
    }

    private static GameHostProcess Start(FakeGameHost fake)
    {
        GameHostProcess gameHost = new(EngineSandbox.FakeGameHostExecutable, [fake.Write()]);
        gameHost.Start();
        return gameHost;
    }

    private static string Invoke(string function) => $"<invoke name=\"{function}\" returntype=\"xml\"></invoke>";
}
