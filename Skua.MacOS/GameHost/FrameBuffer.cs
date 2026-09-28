using System.Runtime.InteropServices;

namespace Skua.MacOS.GameHost;

/// <summary>
/// The Frame Buffer (ADR 0006): a POSIX shared-memory object the Engine creates and the Game Host writes each frame into while the
/// Game View is live. A header, then three slots of RGBA8 rows, opaque; the latest complete frame wins.
/// </summary>
/// <remarks>
/// The layout, which <c>Skua.GameHost/src/frame_buffer.rs</c> mirrors (all little-endian):
/// <code>
///   0  u32 magic "SKFB"     4  u32 version     8  u32 format (1 = RGBA8)     12 u32 slot count (3)
///   16 u32 max width        20 u32 max height  24 u32 slot bytes             28 u32 latest slot (NoSlot until the first frame)
///   32 u32 reading slot (NoSlot when the reader holds none)                  40 u64 frames published
///   64 + 48 * i: slot i's u64 seq (odd while written), u64 frame number, u64 write stamp (ns, CLOCK_UPTIME_RAW, as
///                mach_absolute_time), u32 width, u32 height, u32 stride
///   256 + slot bytes * i: slot i's rows
/// </code>
/// The writer picks the slot that is neither the latest nor the one being read, bumps its seq to odd, writes the rows and the slot's
/// fields, bumps the seq to even, then publishes the slot as the latest. The reader checks the seq again after its copy (a seqlock).
/// </remarks>
public sealed unsafe partial class FrameBuffer : IDisposable
{
    public const uint Magic = 0x42464B53; // "SKFB"
    public const uint Version = 1;
    public const uint FormatRgba8 = 1;
    public const int SlotCount = 3;
    public const int HeaderBytes = 256;
    public const uint NoSlot = uint.MaxValue;

    public const int MagicOffset = 0;
    public const int VersionOffset = 4;
    public const int FormatOffset = 8;
    public const int SlotCountOffset = 12;
    public const int MaxWidthOffset = 16;
    public const int MaxHeightOffset = 20;
    public const int SlotBytesOffset = 24;
    public const int LatestOffset = 28;
    public const int ReadingOffset = 32;
    public const int PublishedOffset = 40;
    public const int SlotsOffset = 64;
    public const int SlotStride = 48;
    public const int SlotSeqOffset = 0;
    public const int SlotNumberOffset = 8;
    public const int SlotStampOffset = 16;
    public const int SlotWidthOffset = 24;
    public const int SlotHeightOffset = 28;
    public const int SlotRowStrideOffset = 32;

    private readonly object _lock = new();
    private readonly int _fd;
    private byte* _base;
    private string? _name;

    private FrameBuffer(string name, int fd, byte* mapped, long length, int maxWidth, int maxHeight)
    {
        _name = name;
        _fd = fd;
        _base = mapped;
        Length = length;
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
    }

    /// <summary>The shared-memory name the Game Host opens, until <see cref="Unlink"/>.</summary>
    public string Name => _name ?? "";

    public long Length { get; }

    public int MaxWidth { get; }

    public int MaxHeight { get; }

    public static long SlotBytes(int maxWidth, int maxHeight) => (long)maxWidth * maxHeight * 4;

    public static long TotalBytes(int maxWidth, int maxHeight) => HeaderBytes + SlotCount * SlotBytes(maxWidth, maxHeight);

    /// <summary>A name for this process's next Frame Buffer; macOS allows 31 characters.</summary>
    public static string NewName() => $"/skua-fb-{Environment.ProcessId}-{Interlocked.Increment(ref s_created)}";

    private static int s_created;

    /// <summary>
    /// Creates the shared-memory object, readable and writable by this user only, sized for frames up to
    /// <paramref name="maxWidth"/> by <paramref name="maxHeight"/>, with an empty header.
    /// </summary>
    /// <exception cref="IOException">The object couldn't be created or mapped.</exception>
    public static FrameBuffer Create(string name, int maxWidth, int maxHeight)
    {
        int fd = ShmOpenCreate(name);
        // A process that crashed before unlinking may have left the name, and its pid is ours now.
        if (fd < 0 && Marshal.GetLastPInvokeError() == EEXIST && shm_unlink(name) == 0)
            fd = ShmOpenCreate(name);
        if (fd < 0)
            throw new IOException($"shm_open {name} failed: errno {Marshal.GetLastPInvokeError()}.");
        long length = TotalBytes(maxWidth, maxHeight);
        byte* mapped = null;
        try
        {
            if (ftruncate(fd, length) != 0)
                throw new IOException($"ftruncate {name} to {length} bytes failed: errno {Marshal.GetLastPInvokeError()}.");
            mapped = Map(fd, length, name);
            FrameBuffer buffer = new(name, fd, mapped, length, maxWidth, maxHeight);
            buffer.WriteU32(MagicOffset, Magic);
            buffer.WriteU32(VersionOffset, Version);
            buffer.WriteU32(FormatOffset, FormatRgba8);
            buffer.WriteU32(SlotCountOffset, SlotCount);
            buffer.WriteU32(MaxWidthOffset, (uint)maxWidth);
            buffer.WriteU32(MaxHeightOffset, (uint)maxHeight);
            buffer.WriteU32(SlotBytesOffset, (uint)SlotBytes(maxWidth, maxHeight));
            buffer.WriteU32(LatestOffset, NoSlot);
            buffer.WriteU32(ReadingOffset, NoSlot);
            return buffer;
        }
        catch
        {
            if (mapped is not null)
                munmap(mapped, (nuint)length);
            close(fd);
            shm_unlink(name);
            throw;
        }
    }

    /// <summary>Opens an existing Frame Buffer by name, as the Game Host does; for tests and the fake Game Host's peers.</summary>
    /// <exception cref="IOException">It doesn't exist, or isn't a Frame Buffer.</exception>
    public static FrameBuffer Open(string name)
    {
        int fd = shm_open(name, O_RDWR);
        if (fd < 0)
            throw new IOException($"shm_open {name} failed: errno {Marshal.GetLastPInvokeError()}.");
        byte* header = null;
        try
        {
            header = Map(fd, HeaderBytes, name);
            if (*(uint*)header != Magic || *(uint*)(header + VersionOffset) != Version)
                throw new IOException($"{name} isn't a version {Version} Frame Buffer.");
            int maxWidth = (int)*(uint*)(header + MaxWidthOffset);
            int maxHeight = (int)*(uint*)(header + MaxHeightOffset);
            munmap(header, HeaderBytes);
            header = null;
            long length = TotalBytes(maxWidth, maxHeight);
            return new FrameBuffer(name, fd, Map(fd, length, name), length, maxWidth, maxHeight);
        }
        catch
        {
            if (header is not null)
                munmap(header, HeaderBytes);
            close(fd);
            throw;
        }
    }

    /// <summary>Removes the name, so nothing else can open the object; the mappings stay valid. Safe to call more than once.</summary>
    public void Unlink()
    {
        lock (_lock)
        {
            if (_name is null)
                return;
            shm_unlink(_name);
            _name = null;
        }
    }

    /// <summary>
    /// Copies the latest frame into <paramref name="destination"/> (rows of <paramref name="destinationStride"/> bytes, at least the frame's
    /// size) if it is newer than <paramref name="lastNumber"/>, and returns its number, size and write stamp; otherwise returns null.
    /// </summary>
    /// <remarks>A frame the Game Host overwrote during the copy is dropped: the next call gets a newer one.</remarks>
    public FrameInfo? TryRead(long lastNumber, byte* destination, int destinationStride, int destinationHeight)
    {
        lock (_lock)
        {
            if (_base is null)
                return null;
            uint latest = Volatile.Read(ref *(uint*)(_base + LatestOffset));
            if (latest >= SlotCount)
                return null;
            byte* slot = _base + SlotsOffset + (SlotStride * latest);
            ulong seq = Volatile.Read(ref *(ulong*)(slot + SlotSeqOffset));
            if ((seq & 1) != 0)
                return null;
            long number = (long)*(ulong*)(slot + SlotNumberOffset);
            if (number <= lastNumber)
                return null;

            Volatile.Write(ref *(uint*)(_base + ReadingOffset), latest);
            try
            {
                // The writer may have taken the slot between the first read of latest and the mark.
                if (Volatile.Read(ref *(ulong*)(slot + SlotSeqOffset)) != seq)
                    return null;
                int width = (int)*(uint*)(slot + SlotWidthOffset);
                int height = (int)*(uint*)(slot + SlotHeightOffset);
                int stride = (int)*(uint*)(slot + SlotRowStrideOffset);
                long stamp = (long)*(ulong*)(slot + SlotStampOffset);
                if (width <= 0 || height <= 0 || width > MaxWidth || height > MaxHeight || stride < width * 4
                    || (long)stride * height > SlotBytes(MaxWidth, MaxHeight) || height > destinationHeight || destinationStride < width * 4)
                    return null;

                byte* rows = _base + HeaderBytes + (SlotBytes(MaxWidth, MaxHeight) * latest);
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy(rows + ((long)y * stride), destination + ((long)y * destinationStride), destinationStride, width * 4);

                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref *(ulong*)(slot + SlotSeqOffset)) != seq)
                    return null;
                return new FrameInfo(number, width, height, stamp);
            }
            finally
            {
                Volatile.Write(ref *(uint*)(_base + ReadingOffset), NoSlot);
            }
        }
    }

    /// <summary>
    /// The size of the latest frame, so a reader can size its destination before <see cref="TryRead"/>; null before the first. The writer
    /// may publish another size meanwhile, which <see cref="TryRead"/> then refuses if it doesn't fit.
    /// </summary>
    public (int Width, int Height)? LatestSize()
    {
        lock (_lock)
        {
            if (_base is null)
                return null;
            uint latest = Volatile.Read(ref *(uint*)(_base + LatestOffset));
            if (latest >= SlotCount)
                return null;
            byte* slot = _base + SlotsOffset + (SlotStride * latest);
            int width = (int)Volatile.Read(ref *(uint*)(slot + SlotWidthOffset));
            int height = (int)Volatile.Read(ref *(uint*)(slot + SlotHeightOffset));
            return width > 0 && height > 0 && width <= MaxWidth && height <= MaxHeight ? (width, height) : null;
        }
    }

    /// <summary>The frames the Game Host has published so far.</summary>
    public long Published
    {
        get
        {
            lock (_lock)
                return _base is null ? 0 : (long)Volatile.Read(ref *(ulong*)(_base + PublishedOffset));
        }
    }

    /// <summary>Now on the write stamps' clock, in nanoseconds.</summary>
    public static long Now() => (long)clock_gettime_nsec_np(CLOCK_UPTIME_RAW);

    /// <summary>Unmaps and closes it, and unlinks the name if that hasn't happened.</summary>
    public void Dispose()
    {
        Unlink();
        lock (_lock)
        {
            if (_base is null)
                return;
            munmap(_base, (nuint)Length);
            _base = null;
            close(_fd);
        }
    }

    private void WriteU32(int offset, uint value) => *(uint*)(_base + offset) = value;

    private static byte* Map(int fd, long length, string name)
    {
        void* mapped = mmap(null, (nuint)length, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
        if (mapped == MAP_FAILED)
            throw new IOException($"mmap {name} ({length} bytes) failed: errno {Marshal.GetLastPInvokeError()}.");
        return (byte*)mapped;
    }

    /// <summary>shm_open(name, O_RDWR | O_CREAT | O_EXCL, 0600), checked.</summary>
    /// <remarks>
    /// shm_open is variadic. On Apple arm64 a variadic argument travels on the stack, where a P/Invoke never puts it, so the mode goes
    /// as a ninth argument: the first eight fill the registers. It also goes as the third, where x86-64 reads it.
    /// </remarks>
    private static int ShmOpenCreate(string name)
    {
        const long mode = 0b110_000_000;
        int fd = shm_open_create(name, O_RDWR | O_CREAT | O_EXCL, mode, 0, 0, 0, 0, 0, mode);
        if (fd < 0)
            return fd;
        Span<byte> stat = stackalloc byte[StatBytes];
        fixed (byte* st = stat)
        {
            // st_mode is a u16 at offset 4 of macOS's struct stat.
            if (fstat(fd, st) != 0 || (*(ushort*)(st + 4) & 0x1FF) != mode)
            {
                close(fd);
                shm_unlink(name);
                throw new IOException($"shm_open {name} didn't create it as 0600.");
            }
        }
        return fd;
    }

    private const int EEXIST = 17;
    private const int O_RDWR = 0x2;
    private const int O_CREAT = 0x200;
    private const int O_EXCL = 0x800;
    private const int PROT_READ = 0x1;
    private const int PROT_WRITE = 0x2;
    private const int MAP_SHARED = 0x1;
    private const int CLOCK_UPTIME_RAW = 8;
    private const int StatBytes = 144;
    private static readonly void* MAP_FAILED = (void*)-1;

    [LibraryImport("libc", EntryPoint = "shm_open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shm_open_create(string name, int oflag, long mode, long x3, long x4, long x5, long x6, long x7, long stackMode);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shm_open(string name, int oflag);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shm_unlink(string name);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ftruncate(int fd, long length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fstat(int fd, byte* stat);

    [LibraryImport("libc", SetLastError = true)]
    private static partial void* mmap(void* address, nuint length, int protection, int flags, int fd, long offset);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap(void* address, nuint length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc")]
    private static partial ulong clock_gettime_nsec_np(int clock);
}

/// <summary>A frame read from the Frame Buffer: its number, its size and its write stamp (see <see cref="FrameBuffer.Now"/>).</summary>
public readonly record struct FrameInfo(long Number, int Width, int Height, long Stamp);
