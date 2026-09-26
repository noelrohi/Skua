// PROTOTYPE (#6): the Engine side of the Bridge transport (framing over the Game Host's stdin/stdout).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace BridgeConsole;

public sealed class GameHost : IDisposable
{
    public record Shot(int Width, int Height, ulong Frames, byte[] Png);

    private readonly Process _proc;
    private readonly Stream _in;
    private readonly Stream _out;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pending = new();
    private readonly BlockingCollection<string> _events = new();
    private uint _nextId;

    public readonly List<string> Callbacks = new();
    public event Action<string>? EventXml;       // delivered in order on one dispatch thread
    public event Action<string>? FlashLog;       // trace / uncaught AS3 errors
    public event Action<string>? DebugLog;       // Ruffle/wgpu log lines, stderr
    public int Pid => _proc.Id;
    public int MaxQueueDepth;
    public Process Process => _proc;

    public GameHost(string exe, string swf, bool showGame = false, string? extraArgs = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (showGame) psi.ArgumentList.Add("--show-game");
        if (extraArgs != null) psi.ArgumentList.Add(extraArgs);
        psi.ArgumentList.Add(swf);
        _proc = Process.Start(psi)!;
        _in = _proc.StandardInput.BaseStream;
        _out = _proc.StandardOutput.BaseStream;
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) DebugLog?.Invoke("[gamehost stderr] " + e.Data); };
        _proc.BeginErrorReadLine();
        new Thread(ReadLoop) { IsBackground = true, Name = "gamehost-reader" }.Start();
        new Thread(DispatchLoop) { IsBackground = true, Name = "gamehost-events" }.Start();
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = s.Read(buf[off..]);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
    }

    private void ReadLoop()
    {
        var hdr = new byte[4];
        try
        {
            while (true)
            {
                ReadExact(_out, hdr);
                int len = BitConverter.ToInt32(hdr);
                var body = new byte[len];
                ReadExact(_out, body);
                byte kind = body[0];
                switch ((char)kind)
                {
                    case 'R':
                    case 'I':
                    case 'P':
                    case 'Q':
                        uint id = BitConverter.ToUInt32(body, 1);
                        if (_pending.TryRemove(id, out var tcs))
                            tcs.TrySetResult(body[5..]);
                        break;
                    case 'E':
                        _events.Add(Encoding.UTF8.GetString(body, 1, len - 1));
                        if (_events.Count > MaxQueueDepth) MaxQueueDepth = _events.Count;
                        break;
                    case 'F':
                        FlashLog?.Invoke(Encoding.UTF8.GetString(body, 1, len - 1));
                        break;
                    case 'L':
                        DebugLog?.Invoke("[gamehost] " + Encoding.UTF8.GetString(body, 2, len - 2));
                        break;
                    case 'X':
                        lock (Callbacks) Callbacks.Add(Encoding.UTF8.GetString(body, 1, len - 1));
                        break;
                }
            }
        }
        catch (Exception)
        {
            foreach (var p in _pending.Values) p.TrySetException(new IOException("Game Host exited"));
            _events.CompleteAdding();
        }
    }

    private void DispatchLoop()
    {
        foreach (var xml in _events.GetConsumingEnumerable())
        {
            try { EventXml?.Invoke(xml); }
            catch (Exception e) { DebugLog?.Invoke("[event handler] " + e.Message); }
        }
    }

    private byte[] Request(char kind, ReadOnlySpan<byte> payload, TimeSpan timeout)
    {
        uint id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var frame = new byte[4 + 1 + 4 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), 1 + 4 + payload.Length);
        frame[4] = (byte)kind;
        BitConverter.TryWriteBytes(frame.AsSpan(5, 4), id);
        payload.CopyTo(frame.AsSpan(9));
        lock (_writeLock)
        {
            _in.Write(frame);
            _in.Flush();
        }
        if (!tcs.Task.Wait(timeout))
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"Game Host did not answer '{kind}' in {timeout}");
        }
        return tcs.Task.Result;
    }

    /// <summary>Synchronous Engine -> Game Client call; returns the reply XML.</summary>
    public string Call(string invokeXml) => Encoding.UTF8.GetString(Request('C', Encoding.UTF8.GetBytes(invokeXml), TimeSpan.FromSeconds(30)));

    public void Ping() => Request('P', ReadOnlySpan<byte>.Empty, TimeSpan.FromSeconds(10));

    public string RenderBench(int n) => Encoding.UTF8.GetString(Request('B', BitConverter.GetBytes(n), TimeSpan.FromSeconds(120)));

    public string Mem() => Encoding.UTF8.GetString(Request('M', ReadOnlySpan<byte>.Empty, TimeSpan.FromSeconds(10)));

    public void FullGc() => Request('G', ReadOnlySpan<byte>.Empty, TimeSpan.FromSeconds(60));

    public string Stats() => Encoding.UTF8.GetString(Request('Q', ReadOnlySpan<byte>.Empty, TimeSpan.FromSeconds(10)));

    public Shot Screenshot(int maxWidth = 0)
    {
        var b = Request('S', BitConverter.GetBytes(maxWidth), TimeSpan.FromSeconds(10));
        return new Shot(BitConverter.ToInt32(b, 0), BitConverter.ToInt32(b, 4), BitConverter.ToUInt64(b, 8), b[16..]);
    }

    public void Dispose()
    {
        try { _in.Close(); } catch { }
        if (!_proc.WaitForExit(2000)) _proc.Kill();
    }
}
