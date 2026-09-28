using System.Runtime.InteropServices;
using Skua.MacOS.GameHost;

namespace Skua.Engine.Tests;

/// <summary>The Engine's end of the Frame Buffer, against a writer that follows the layout the Game Host does.</summary>
public sealed unsafe partial class FrameBufferTests
{
    [Fact]
    public void Creates_an_object_only_this_user_can_open_and_unlinks_its_name()
    {
        using FrameBuffer buffer = FrameBuffer.Create(FrameBuffer.NewName(), 4, 2);
        string name = buffer.Name;

        int fd = shm_open(name, 2);
        Assert.True(fd >= 0);
        byte* st = stackalloc byte[144];
        Assert.Equal(0, fstat(fd, st));
        close(fd);
        Assert.Equal(0b110_000_000, *(ushort*)(st + 4) & 0x1FF);

        using (FrameBuffer opened = FrameBuffer.Open(name))
            Assert.Equal((4, 2), (opened.MaxWidth, opened.MaxHeight));

        buffer.Unlink();
        Assert.Equal("", buffer.Name);
        Assert.Throws<IOException>(() => FrameBuffer.Open(name));
    }

    [Fact]
    public void Reads_the_latest_frame_once_and_only_when_it_is_whole()
    {
        using FrameBuffer buffer = FrameBuffer.Create(FrameBuffer.NewName(), 4, 2);
        using Writer writer = new(buffer.Name);
        byte[] rows = new byte[4 * 4 * 2];
        fixed (byte* destination = rows)
        {
            Assert.Null(buffer.TryRead(0, destination, 16, 2));

            writer.Write(slot: 0, number: 1, width: 2, height: 2, fill: 0x11, stamp: 42);
            FrameInfo frame = Assert.IsType<FrameInfo>(buffer.TryRead(0, destination, 16, 2));
            Assert.Equal(new FrameInfo(1, 2, 2, 42), frame);
            Assert.Equal(Enumerable.Repeat((byte)0x11, 8), rows[..8]);
            Assert.Equal(Enumerable.Repeat((byte)0x11, 8), rows[16..24]);
            Assert.Equal(0, rows[8]);
            Assert.Null(buffer.TryRead(1, destination, 16, 2));

            // A slot whose seq is odd is mid-write, so the reader skips it.
            writer.Write(slot: 1, number: 2, width: 2, height: 2, fill: 0x22, stamp: 43, leaveOdd: true);
            Assert.Null(buffer.TryRead(1, destination, 16, 2));
            writer.Write(slot: 2, number: 3, width: 2, height: 2, fill: 0x33, stamp: 44);
            Assert.Equal(3, buffer.TryRead(1, destination, 16, 2)?.Number);
            Assert.Equal(0x33, rows[0]);
            Assert.Equal(NoSlot, writer.Reading);
        }
    }

    [Fact]
    public void Encodes_input_as_the_Game_Host_parses_it()
    {
        Assert.Equal([2, .. BitConverter.GetBytes(479f), .. BitConverter.GetBytes(275f), 1],
            new GameInput.MouseDown(479, 275, GameMouseButton.Left).Encode());
        Assert.Equal([5, 1, .. BitConverter.GetBytes(-40f)], new GameInput.Wheel(-40, Pixels: true).Encode());
        Assert.Equal([6, 1, 0, 0, 0, 0, 9, .. "ShiftLeft"u8, 5, .. "Shift"u8],
            new GameInput.KeyDown(new GameKey("ShiftLeft", 0, "Shift", GameKeyLocation.Left)).Encode());
        Assert.Equal([7, 0, (byte)'a', 0, 0, 0, 4, .. "KeyA"u8, 0], new GameInput.KeyUp(new GameKey("KeyA", 'a', null, GameKeyLocation.Standard)).Encode());
        Assert.Equal([8, 0xE9, 0, 0, 0], new GameInput.Text('é').Encode());
        Assert.Equal([9, 9, .. "Backspace"u8], new GameInput.TextControl("Backspace").Encode());
        Assert.Equal([10], new GameInput.FocusGained().Encode());
        Assert.Equal([12, .. "hé"u8], new GameInput.Clipboard("hé").Encode());
        Assert.Equal([1], GameInput.EncodeView(true));
        Assert.Equal([1, .. BitConverter.GetBytes(1916u), .. BitConverter.GetBytes(1100u), .. BitConverter.GetBytes(2f)],
            GameInput.EncodeView(true, new GameViewport(1916, 1100, 2)));
        // Not live: the Game Host goes back to the stage size, so no viewport goes.
        Assert.Equal([0], GameInput.EncodeView(false, new GameViewport(1916, 1100, 2)));
    }

    [Fact]
    public void Decodes_the_Game_Hosts_cursor()
    {
        Assert.Equal(new GameCursorState(GameCursor.Hand, true), GameCursorState.Decode([1, 1]));
        Assert.Equal(new GameCursorState(GameCursor.Grab, false), GameCursorState.Decode([3, 0]));
        Assert.Equal(new GameCursorState(GameCursor.Arrow, true), GameCursorState.Decode([9, 1]));
        Assert.Null(GameCursorState.Decode([1]));
    }

    [Fact]
    public void Tells_the_latest_frames_size_as_frames_grow_and_shrink()
    {
        using FrameBuffer buffer = FrameBuffer.Create(FrameBuffer.NewName(), 6, 4);
        using Writer writer = new(buffer.Name);
        Assert.Null(buffer.LatestSize());

        writer.Write(slot: 0, number: 1, width: 3, height: 2, fill: 0x11, stamp: 1);
        Assert.Equal((3, 2), buffer.LatestSize());
        writer.Write(slot: 1, number: 2, width: 6, height: 4, fill: 0x22, stamp: 2);
        Assert.Equal((6, 4), buffer.LatestSize());

        // A reader sized for the smaller frame refuses the larger one rather than overrun its rows.
        byte[] small = new byte[3 * 4 * 2];
        fixed (byte* destination = small)
            Assert.Null(buffer.TryRead(0, destination, 12, 2));
        byte[] large = new byte[6 * 4 * 4];
        fixed (byte* destination = large)
            Assert.Equal(new FrameInfo(2, 6, 4, 2), buffer.TryRead(0, destination, 24, 4));
    }

    private const uint NoSlot = uint.MaxValue;

    /// <summary>Writes frames as the Game Host does (see Skua.GameHost/src/frame_buffer.rs), into a mapping of its own.</summary>
    private sealed class Writer : IDisposable
    {
        private readonly byte* _base;
        private readonly long _length;

        public Writer(string name)
        {
            int fd = shm_open(name, 2);
            Assert.True(fd >= 0);
            byte* header = (byte*)mmap(null, 256, 3, 1, fd, 0);
            _length = 256 + (3L * *(uint*)(header + 24));
            munmap(header, 256);
            _base = (byte*)mmap(null, (nuint)_length, 3, 1, fd, 0);
            close(fd);
        }

        public uint Reading => *(uint*)(_base + 32);

        public void Write(uint slot, ulong number, uint width, uint height, byte fill, ulong stamp, bool leaveOdd = false)
        {
            byte* desc = _base + 64 + (48 * slot);
            ulong seq = *(ulong*)desc | 1;
            *(ulong*)desc = seq;
            long slotBytes = *(uint*)(_base + 24);
            new Span<byte>(_base + 256 + (slotBytes * slot), (int)(width * 4 * height)).Fill(fill);
            *(ulong*)(desc + 8) = number;
            *(ulong*)(desc + 16) = stamp;
            *(uint*)(desc + 24) = width;
            *(uint*)(desc + 28) = height;
            *(uint*)(desc + 32) = width * 4;
            if (!leaveOdd)
                *(ulong*)desc = seq + 1;
            *(uint*)(_base + 28) = slot;
            *(ulong*)(_base + 40) = number;
        }

        public void Dispose() => munmap(_base, (nuint)_length);
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shm_open(string name, int oflag);

    [LibraryImport("libc")]
    private static partial int fstat(int fd, byte* stat);

    [LibraryImport("libc")]
    private static partial void* mmap(void* address, nuint length, int protection, int flags, int fd, long offset);

    [LibraryImport("libc")]
    private static partial int munmap(void* address, nuint length);

    [LibraryImport("libc")]
    private static partial int close(int fd);
}
