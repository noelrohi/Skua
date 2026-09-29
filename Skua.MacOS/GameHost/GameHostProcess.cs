using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Skua.MacOS.GameHost;

/// <summary>A frame the Game Host captured: its size, an estimate of the Game Client's frame number, and the PNG.</summary>
public sealed record GameHostScreenshot(int Width, int Height, long Frame, byte[] Png);

/// <summary>
/// The Game Host child process and the Engine's end of the Bridge over its pipes. The Game Host exits on its own when its stdin closes,
/// so it never outlives the Engine.
/// </summary>
/// <remarks>
/// One reader thread takes every frame: replies complete their request by id, and the Game Client's calls queue for one dispatch thread,
/// which raises <see cref="Invoked"/> in order. A handler may call back into the Bridge, because replies never wait for the dispatch thread.
/// Another thread reads stderr. Both use blocking reads. A handler's exception, or an unexpected one reading a pipe, never ends the process:
/// these threads are the Engine's. The process's exit event is guarded the same way, as it runs on a thread pool thread.
/// Subscribe to the events before calling <see cref="Start"/>, so no frame or exit is missed.
/// </remarks>
public sealed class GameHostProcess : IDisposable
{
    /// <summary>How long a request waits for its reply before it fails.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(1);

    private readonly Process _process;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pending = new();
    private readonly BlockingCollection<string> _invocations = new();
    private readonly List<string> _callbacks = [];
    private Stream? _stdin;
    private Thread? _stderrReader;
    private uint _nextId;
    private volatile bool _closed;
    private bool _disposed;

    public GameHostProcess(string executable, IEnumerable<string> arguments)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += (_, _) => Raise(Exited, _process.ExitCode);
    }

    public int Pid => _process.Id;

    public bool IsRunning => _stdin is not null && !_process.HasExited;

    /// <summary>The names the Game Client has registered with <c>ExternalInterface.addCallback</c>, in order.</summary>
    public IReadOnlyList<string> Callbacks
    {
        get
        {
            lock (_callbacks)
                return _callbacks.ToArray();
        }
    }

    /// <summary>Raised once the process runs with its pid, before any frame, log line or exit is raised.</summary>
    public event Action<int>? Started;

    /// <summary>Raised on the dispatch thread, in order, with the invoke XML of each <c>ExternalInterface.call</c> from the Game Client.</summary>
    public event Action<string>? Invoked;

    /// <summary>Raised for each Flash log line: AS3 <c>trace()</c>, warnings and uncaught AS3 errors.</summary>
    public event Action<string>? FlashLog;

    /// <summary>Raised for each Ruffle/wgpu log line and stderr line of the Game Host, and when the Bridge stops.</summary>
    public event Action<string>? LogLine;

    /// <summary>Raised on the reader thread when the Game Client's mouse cursor changes (a Game Host with a Frame Buffer only).</summary>
    public event Action<GameCursorState>? CursorChanged;

    /// <summary>Raised on the reader thread with text the Game Client put on the clipboard (a Game Host with a Frame Buffer only).</summary>
    public event Action<string>? ClipboardCopied;

    /// <summary>Raised once when the Game Host sends a corrupt frame; the Bridge reads nothing after it.</summary>
    public event Action<string>? BridgeFailed;

    /// <summary>Raised once when the Game Host process ends, with its exit code.</summary>
    public event Action<int>? Exited;

    /// <exception cref="FileNotFoundException">The executable doesn't exist.</exception>
    public void Start()
    {
        string executable = _process.StartInfo.FileName;
        if (!File.Exists(executable))
            throw new FileNotFoundException($"The Game Host '{executable}' doesn't exist.", executable);

        _process.Start();
        _stdin = _process.StandardInput.BaseStream;
        Started?.Invoke(_process.Id);
        // Blocking reads, as BridgeFrames.Read explains; taken here so an early Dispose can't race the threads for them.
        Stream stdout = _process.StandardOutput.BaseStream;
        StreamReader stderr = _process.StandardError;
        new Thread(() => ReadLoop(stdout)) { IsBackground = true, Name = "Game Host reader" }.Start();
        _stderrReader = new Thread(() => ReadStderr(stderr)) { IsBackground = true, Name = "Game Host stderr" };
        _stderrReader.Start();
        new Thread(DispatchLoop) { IsBackground = true, Name = "Game Host dispatch" }.Start();
    }

    /// <summary>Calls into the Game Client with an invoke request, and returns the reply XML.</summary>
    /// <exception cref="IOException">The Game Host is gone.</exception>
    /// <exception cref="TimeoutException">No reply came within <see cref="RequestTimeout"/>.</exception>
    public string Call(string invokeXml) => Encoding.UTF8.GetString(Request('C', Encoding.UTF8.GetBytes(invokeXml), RequestTimeout));

    /// <summary>The Game Host's loop and render counters as JSON; the maxima reset when read.</summary>
    /// <exception cref="IOException">The Game Host is gone.</exception>
    /// <exception cref="TimeoutException">No reply came within <paramref name="timeout"/>.</exception>
    public string Stats(TimeSpan timeout) => Encoding.UTF8.GetString(Request('Q', [], timeout));

    /// <summary>
    /// Has the Game Host render a frame and capture it, scaled down to <paramref name="maxWidth"/> if wider (0 keeps the native size).
    /// Returns null when the Game Host couldn't capture one.
    /// </summary>
    /// <exception cref="IOException">The Game Host is gone.</exception>
    /// <exception cref="TimeoutException">No reply came within <paramref name="timeout"/>.</exception>
    public GameHostScreenshot? Screenshot(uint maxWidth, TimeSpan timeout)
    {
        byte[] request = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(request, maxWidth);
        byte[] reply = Request('S', request, timeout);
        if (reply.Length < 16)
            throw new IOException($"The Game Host sent a screenshot reply of {reply.Length} bytes.");

        uint width = BinaryPrimitives.ReadUInt32LittleEndian(reply);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(4));
        ulong frame = BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(8));
        return width == 0 || height == 0 ? null : new GameHostScreenshot((int)width, (int)height, (long)frame, reply[16..]);
    }

    /// <summary>Sends a frame that gets no reply (id 0), such as <c>U</c> input or a <c>W</c> view change.</summary>
    /// <exception cref="IOException">The Game Host is gone.</exception>
    public void Send(char type, ReadOnlySpan<byte> payload)
    {
        if (_stdin is null)
            throw new InvalidOperationException("The Game Host hasn't started.");
        if (_closed)
            throw new IOException("The Game Host has exited.");

        byte[] frame = BridgeFrames.EncodeRequest(type, 0, payload);
        try
        {
            lock (_writeLock)
            {
                _stdin.Write(frame);
                _stdin.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new IOException("The Game Host has exited.", e);
        }
    }

    /// <summary>Sends a request frame and waits for the reply with the same id; returns the reply's payload after the id.</summary>
    /// <exception cref="IOException">The Game Host is gone.</exception>
    /// <exception cref="TimeoutException">No reply came within <paramref name="timeout"/>.</exception>
    public byte[] Request(char type, ReadOnlySpan<byte> payload, TimeSpan timeout)
    {
        if (_stdin is null)
            throw new InvalidOperationException("The Game Host hasn't started.");

        uint id = Interlocked.Increment(ref _nextId);
        TaskCompletionSource<byte[]> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        // The reader sets _closed before it fails what's pending, so a request added after that sees it here.
        if (_closed)
        {
            _pending.TryRemove(id, out _);
            throw new IOException("The Game Host has exited.");
        }

        byte[] frame = BridgeFrames.EncodeRequest(type, id, payload);
        try
        {
            lock (_writeLock)
            {
                _stdin.Write(frame);
                _stdin.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new IOException("The Game Host has exited.", e);
        }

        if (Task.WaitAny([reply.Task], timeout) < 0)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"The Game Host didn't answer a '{type}' request within {timeout.TotalSeconds:0.#} s.");
        }
        return reply.Task.GetAwaiter().GetResult();
    }

    /// <summary>Closes the Game Host's stdin so it exits, and kills it if it doesn't within a second.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_stdin is not null)
        {
            try
            {
                lock (_writeLock)
                    _stdin.Close();
            }
            catch (IOException)
            {
            }

            if (!_process.WaitForExit(ExitGrace))
                _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
            // Its last lines, such as a panic's, are raised before Dispose returns; a child that holds stderr open doesn't hold Dispose.
            _stderrReader?.Join(ExitGrace);
        }
        _process.Dispose();
    }

    private void ReadLoop(Stream stdout)
    {
        try
        {
            while (BridgeFrames.Read(stdout) is { } frame)
                Receive(frame);
        }
        catch (InvalidDataException e)
        {
            Raise(BridgeFailed, e.Message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            Raise(LogLine, $"Bridge read stopped: {e.Message}");
        }
        catch (Exception e)
        {
            // Anything else, even from the runtime, ends the Bridge as a corrupt frame does rather than the Engine: unhandled on this
            // thread, it would abort the process.
            Raise(BridgeFailed, $"The Bridge read failed: {e}");
        }
        finally
        {
            _closed = true;
            foreach (uint id in _pending.Keys)
            {
                if (_pending.TryRemove(id, out TaskCompletionSource<byte[]>? reply))
                    reply.TrySetException(new IOException("The Game Host has exited."));
            }
            _invocations.CompleteAdding();
        }
    }

    private void ReadStderr(StreamReader stderr)
    {
        try
        {
            while (stderr.ReadLine() is { } line)
                Raise(LogLine, line);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The Game Host is gone; the Bridge read tells.
        }
        catch (Exception e)
        {
            Raise(LogLine, $"The Game Host's stderr read failed: {e}");
        }
    }

    private void Receive(BridgeFrame frame)
    {
        switch (frame.Type)
        {
            case 'R' or 'I' or 'P' or 'Q' when frame.Payload.Length >= 4:
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload);
                if (_pending.TryRemove(id, out TaskCompletionSource<byte[]>? reply))
                    reply.TrySetResult(frame.Payload[4..]);
                break;
            case 'E':
                _invocations.Add(Encoding.UTF8.GetString(frame.Payload));
                break;
            case 'F':
                Raise(FlashLog, Encoding.UTF8.GetString(frame.Payload));
                break;
            case 'L' when frame.Payload.Length >= 1:
                Raise(LogLine, Encoding.UTF8.GetString(frame.Payload, 1, frame.Payload.Length - 1));
                break;
            case 'X':
                lock (_callbacks)
                    _callbacks.Add(Encoding.UTF8.GetString(frame.Payload));
                break;
            case 'O' when GameCursorState.Decode(frame.Payload) is { } cursor:
                Raise(CursorChanged, cursor);
                break;
            case 'K':
                Raise(ClipboardCopied, Encoding.UTF8.GetString(frame.Payload));
                break;
            default:
                Raise(LogLine, $"Skipped a Bridge frame of type '{frame.Type}' ({frame.Payload.Length} bytes).");
                break;
        }
    }

    /// <summary>
    /// Raises an event on a thread where a handler's exception would abort the process; it goes to Trace instead, which the Engine records
    /// in its debug log (and echoes on stderr in skua-engine), with the thread it was raised on.
    /// </summary>
    private static void Raise<T>(Action<T>? handler, T value)
    {
        try
        {
            handler?.Invoke(value);
        }
        catch (Exception e)
        {
            string message = $"A Game Host event handler failed on thread '{Thread.CurrentThread.Name ?? "pool"}': {e}";
            try
            {
                Trace.WriteLine(message);
            }
            catch
            {
                Console.Error.WriteLine(message);
            }
        }
    }

    private void DispatchLoop()
    {
        foreach (string invocation in _invocations.GetConsumingEnumerable())
        {
            try
            {
                Invoked?.Invoke(invocation);
            }
            catch (Exception e)
            {
                Raise(LogLine, $"A handler of a Game Client call failed: {e}");
            }
        }
    }
}
