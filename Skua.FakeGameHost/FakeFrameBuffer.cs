using System.Runtime.InteropServices;

/// <summary>
/// The fake's end of the Frame Buffer (see Skua.MacOS/GameHost/FrameBuffer.cs for the layout): while live, it writes a solid 958x550 frame
/// every 33 ms, coloured by its frame number, as skua-gamehost writes the game's frames.
/// </summary>
internal sealed unsafe partial class FakeFrameBuffer
{
    private const int Width = 958;
    private const int Height = 550;
    private const int HeaderBytes = 256;
    private const int SlotsOffset = 64;
    private const int SlotStride = 48;
    private const uint NoSlot = uint.MaxValue;

    private readonly byte* _base;
    private readonly long _slotBytes;
    private readonly object _lock = new();
    private Timer? _timer;
    private long _written;

    private FakeFrameBuffer(byte* mapped, long slotBytes)
    {
        _base = mapped;
        _slotBytes = slotBytes;
    }

    public bool Live { get; private set; }

    public long Written => Interlocked.Read(ref _written);

    /// <summary>Maps the named Frame Buffer, or exits with status 1 as skua-gamehost does.</summary>
    public static FakeFrameBuffer Open(string name)
    {
        int fd = shm_open(name, 2);
        if (fd < 0)
        {
            Console.Error.WriteLine($"fake-gamehost: --frame-buffer: shm_open {name}: errno {Marshal.GetLastPInvokeError()}");
            Environment.Exit(1);
        }
        byte* header = (byte*)mmap(null, HeaderBytes, 3, 1, fd, 0);
        long slotBytes = *(uint*)(header + 24);
        munmap(header, HeaderBytes);
        long length = HeaderBytes + (3 * slotBytes);
        byte* mapped = (byte*)mmap(null, (nuint)length, 3, 1, fd, 0);
        close(fd);
        return new FakeFrameBuffer(mapped, slotBytes);
    }

    public void SetLive(bool live)
    {
        lock (_lock)
        {
            Live = live;
            _timer?.Dispose();
            _timer = live ? new Timer(_ => WriteFrame(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(33)) : null;
        }
    }

    private void WriteFrame()
    {
        lock (_lock)
        {
            if (!Live)
                return;
            uint latest = Volatile.Read(ref *(uint*)(_base + 28));
            uint reading = Volatile.Read(ref *(uint*)(_base + 32));
            uint slot = 0;
            while (slot == latest || slot == reading)
                slot++;
            byte* desc = _base + SlotsOffset + (SlotStride * slot);
            ulong seq = *(ulong*)desc;
            Volatile.Write(ref *(ulong*)desc, seq | 1);
            Interlocked.MemoryBarrier();

            ulong number = *(ulong*)(_base + 40) + 1;
            uint pixel = (uint)(number & 0xFF) | (uint)((number >> 8) & 0xFF) << 8 | (uint)((number >> 16) & 0xFF) << 16 | 0xFF000000;
            new Span<uint>(_base + HeaderBytes + (_slotBytes * slot), Width * Height).Fill(pixel);
            *(ulong*)(desc + 8) = number;
            *(ulong*)(desc + 16) = clock_gettime_nsec_np(8);
            *(uint*)(desc + 24) = Width;
            *(uint*)(desc + 28) = Height;
            *(uint*)(desc + 32) = Width * 4;
            Volatile.Write(ref *(ulong*)desc, (seq | 1) + 1);
            Volatile.Write(ref *(uint*)(_base + 28), slot);
            Volatile.Write(ref *(ulong*)(_base + 40), number);
            Interlocked.Increment(ref _written);
        }
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shm_open(string name, int oflag);

    [LibraryImport("libc", SetLastError = true)]
    private static partial void* mmap(void* address, nuint length, int protection, int flags, int fd, long offset);

    [LibraryImport("libc")]
    private static partial int munmap(void* address, nuint length);

    [LibraryImport("libc")]
    private static partial int close(int fd);

    [LibraryImport("libc")]
    private static partial ulong clock_gettime_nsec_np(int clock);
}
