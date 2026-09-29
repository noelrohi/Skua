using System.Buffers.Binary;

namespace Skua.MacOS.GameHost;

/// <summary>One Bridge frame: a type byte and its payload.</summary>
public sealed record BridgeFrame(char Type, byte[] Payload);

/// <summary>
/// The Bridge framing over the Game Host's stdin/stdout: a <c>u32</c> little-endian length covering the type byte and the payload,
/// then a <c>u8</c> type, then the payload.
/// </summary>
public static class BridgeFrames
{
    /// <summary>Large enough for a full-size screenshot PNG.</summary>
    public const int MaxLength = 64 * 1024 * 1024;

    /// <summary>
    /// Reads the next frame with blocking reads, or returns null at a clean end of stream. Blocking reads of a pipe are plain
    /// <c>read(2)</c> calls; async ones go through System.Net.Sockets on Unix, where the runtime once failed under load.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream ends mid-frame or declares an impossible length.</exception>
    public static BridgeFrame? Read(Stream stream)
    {
        byte[] header = new byte[4];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read == 0)
            return null;
        if (read < header.Length)
            throw new InvalidDataException("The Bridge stream ended inside a frame header.");

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaxLength)
            throw new InvalidDataException($"A Bridge frame declared an invalid length of {length} bytes.");

        byte[] body = new byte[length];
        if (stream.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) < body.Length)
            throw new InvalidDataException("The Bridge stream ended inside a frame.");

        return new BridgeFrame((char)body[0], body[1..]);
    }

    /// <summary>Encodes a request frame: the type, then the <c>u32</c> request id, then the payload.</summary>
    public static byte[] EncodeRequest(char type, uint id, ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[4 + 1 + 4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(1 + 4 + payload.Length));
        frame[4] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), id);
        payload.CopyTo(frame.AsSpan(9));
        return frame;
    }
}
