using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Skua.MacOS.GameHost;

/// <summary>
/// The Game Host child process and the Engine's end of the Bridge over its pipes. The Game Host exits on its own when its stdin closes,
/// so it never outlives the Engine.
/// </summary>
/// <remarks>
/// One reader thread takes every frame: replies complete their request by id, and the Game Client's calls queue for one dispatch thread,
/// which raises <see cref="Invoked"/> in order. A handler may call back into the Bridge, because replies never wait for the dispatch thread.
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
        _process.Exited += (_, _) => Exited?.Invoke(_process.ExitCode);
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                LogLine?.Invoke(e.Data);
        };
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
        _process.BeginErrorReadLine();
        new Thread(ReadLoop) { IsBackground = true, Name = "Game Host reader" }.Start();
        new Thread(DispatchLoop) { IsBackground = true, Name = "Game Host dispatch" }.Start();
    }

    /// <summary>Calls into the Game Client with an invoke request, and returns the reply XML.</summary>
    /// <exception cref="IOException">The Game Host is gone.</exception>
    /// <exception cref="TimeoutException">No reply came within <see cref="RequestTimeout"/>.</exception>
    public string Call(string invokeXml) => Encoding.UTF8.GetString(Request('C', Encoding.UTF8.GetBytes(invokeXml), RequestTimeout));

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
        }
        _process.Dispose();
    }

    private void ReadLoop()
    {
        Stream stdout = _process.StandardOutput.BaseStream;
        try
        {
            while (BridgeFrames.ReadAsync(stdout).GetAwaiter().GetResult() is { } frame)
                Receive(frame);
        }
        catch (InvalidDataException e)
        {
            BridgeFailed?.Invoke(e.Message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            LogLine?.Invoke($"Bridge read stopped: {e.Message}");
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
                FlashLog?.Invoke(Encoding.UTF8.GetString(frame.Payload));
                break;
            case 'L' when frame.Payload.Length >= 1:
                LogLine?.Invoke(Encoding.UTF8.GetString(frame.Payload, 1, frame.Payload.Length - 1));
                break;
            case 'X':
                lock (_callbacks)
                    _callbacks.Add(Encoding.UTF8.GetString(frame.Payload));
                break;
            default:
                LogLine?.Invoke($"Skipped a Bridge frame of type '{frame.Type}' ({frame.Payload.Length} bytes).");
                break;
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
                LogLine?.Invoke($"A handler of a Game Client call failed: {e}");
            }
        }
    }
}
