using System.Buffers.Binary;
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

    [Fact]
    public async Task A_log_handler_that_throws_doesnt_stop_the_Bridge()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost fake = new FakeGameHost(sandbox)
            .Send('F', "[Game] first")
            .Send('F', "[Game] second")
            .Send('E', Invoke("done"));
        using GameHostProcess gameHost = new(EngineSandbox.FakeGameHostExecutable, [fake.Write()]);
        List<string> flash = [];
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // On the reader thread: unhandled there, it would end the process.
        gameHost.FlashLog += line =>
        {
            flash.Add(line);
            throw new InvalidOperationException("a handler's bug");
        };
        gameHost.Invoked += _ => done.TrySetResult();

        gameHost.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["[Game] first", "[Game] second"], flash);
        Assert.True(gameHost.IsRunning);
        Assert.Empty(gameHost.Request('P', [], TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_handler_that_throws_is_traced_with_its_thread_and_exception()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost fake = new FakeGameHost(sandbox).Send('F', "[Game] line").Send('E', Invoke("done"));
        using GameHostProcess gameHost = new(EngineSandbox.FakeGameHostExecutable, [fake.Write()]);
        string marker = "a handler's bug " + Guid.NewGuid().ToString("N");
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gameHost.FlashLog += _ => throw new InvalidOperationException(marker);
        gameHost.Invoked += _ => done.TrySetResult();
        // The Engine records Trace in its debug log; this stands in for it.
        TraceLines traced = new();
        Trace.Listeners.Add(traced);
        try
        {
            gameHost.Start();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            Trace.Listeners.Remove(traced);
        }

        string line = Assert.Single(traced.Lines, l => l.Contains(marker, StringComparison.Ordinal));
        Assert.StartsWith("A Game Host event handler failed on thread 'Game Host reader': System.InvalidOperationException", line);
    }

    [Fact]
    public async Task An_exit_handler_that_throws_doesnt_end_the_process()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost fake = new FakeGameHost(sandbox).Exit(3);
        using GameHostProcess gameHost = new(EngineSandbox.FakeGameHostExecutable, [fake.Write()]);
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // On a thread pool thread: unhandled there, it would end this test process.
        gameHost.Exited += code =>
        {
            exited.TrySetResult(code);
            throw new InvalidOperationException("an exit handler's bug");
        };

        gameHost.Start();

        Assert.Equal(3, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        // Long enough for the runtime to have ended the process, had the exception escaped.
        await Task.Delay(500, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Frames_are_read_with_blocking_reads_so_the_runtimes_async_pipe_read_is_never_used()
    {
        // The runtime once failed with this exception inside an async pipe read on the Game Host reader thread (#105, #129).
        using AsyncFaultingStream stream = new([.. Frame('F', "[Game] line"), .. Frame('E', "done")]);

        BridgeFrame? first = BridgeFrames.Read(stream);
        BridgeFrame? second = BridgeFrames.Read(stream);

        Assert.Equal(('F', "[Game] line"), (first!.Type, Encoding.UTF8.GetString(first.Payload)));
        Assert.Equal(('E', "done"), (second!.Type, Encoding.UTF8.GetString(second.Payload)));
        Assert.Null(BridgeFrames.Read(stream));
    }

    [Fact]
    public async Task Stderr_lines_are_raised_as_log_lines_before_Dispose_returns()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost fake = new FakeGameHost(sandbox).Stderr("WARN wgpu: first").Stderr("WARN wgpu: last").Send('E', Invoke("done"));
        List<string> logs = [];
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (GameHostProcess gameHost = new(EngineSandbox.FakeGameHostExecutable, [fake.Write()]))
        {
            gameHost.LogLine += line =>
            {
                lock (logs)
                    logs.Add(line);
            };
            gameHost.Invoked += _ => done.TrySetResult();
            gameHost.Start();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        lock (logs)
            Assert.Equal(["WARN wgpu: first", "WARN wgpu: last"], logs.Where(l => l.StartsWith("WARN wgpu", StringComparison.Ordinal)));
    }

    private static GameHostProcess Start(FakeGameHost fake)
    {
        GameHostProcess gameHost = new(EngineSandbox.FakeGameHostExecutable, [fake.Write()]);
        gameHost.Start();
        return gameHost;
    }

    private static string Invoke(string function) => $"<invoke name=\"{function}\" returntype=\"xml\"></invoke>";

    private static byte[] Frame(char type, string payload)
    {
        byte[] body = [(byte)type, .. Encoding.UTF8.GetBytes(payload)];
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>A stream whose blocking reads work and whose async reads fail as the runtime's pipe read did.</summary>
    private sealed class AsyncFaultingStream(byte[] bytes) : MemoryStream(bytes)
    {
        private static MissingFieldException Fault() => new("Field not found: 'BufferMemoryReceiveOperation.Buffer'.");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Fault();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw Fault();

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => throw Fault();
    }

    /// <summary>Collects the lines written to Trace while it is listening.</summary>
    private sealed class TraceLines : TraceListener
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                    return [.. _lines];
            }
        }

        public override void Write(string? message) => WriteLine(message);

        public override void WriteLine(string? message)
        {
            if (message is not null)
                lock (_lines)
                    _lines.Add(message);
        }
    }
}
