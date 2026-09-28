using System.Buffers.Binary;
using System.Text;

namespace Skua.MacOS.GameHost;

/// <summary>A mouse button, as Ruffle's <c>MouseButton</c>.</summary>
public enum GameMouseButton : byte
{
    Unknown = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
}

/// <summary>Where a key sits, as Ruffle's <c>KeyLocation</c>.</summary>
public enum GameKeyLocation : byte
{
    Standard = 0,
    Left = 1,
    Right = 2,
    Numpad = 3,
}

/// <summary>
/// A key as Ruffle's <c>KeyDescriptor</c>: the physical key by its Ruffle <c>PhysicalKey</c> name (the W3C <c>code</c>, e.g. <c>KeyA</c>,
/// <c>ShiftLeft</c>), and the logical key as either the character it types or a Ruffle <c>NamedKey</c> name (e.g. <c>Tab</c>).
/// </summary>
/// <param name="Character">The logical key's character, or 0 when it has none.</param>
/// <param name="Named">The logical key's name when it has no character, else null; neither means an unknown logical key.</param>
public sealed record GameKey(string Physical, int Character, string? Named, GameKeyLocation Location);

/// <summary>
/// One input event for the Game Client, sent to the Game Host as a <c>U</c> frame (fire-and-forget, id 0), which turns it into a Ruffle
/// <c>PlayerEvent</c>. Positions are Game Host viewport pixels.
/// </summary>
/// <remarks>
/// The payload after the id is a <c>u8</c> kind, then the kind's fields (see <c>Skua.GameHost/src/frame.rs</c>); names are a <c>u8</c> length
/// then ASCII.
/// </remarks>
public abstract record GameInput
{
    public const byte MouseMoveKind = 1;
    public const byte MouseDownKind = 2;
    public const byte MouseUpKind = 3;
    public const byte MouseLeaveKind = 4;
    public const byte WheelKind = 5;
    public const byte KeyDownKind = 6;
    public const byte KeyUpKind = 7;
    public const byte TextKind = 8;
    public const byte TextControlKind = 9;
    public const byte FocusGainedKind = 10;
    public const byte FocusLostKind = 11;

    private GameInput()
    {
    }

    public sealed record MouseMove(float X, float Y) : GameInput;

    public sealed record MouseDown(float X, float Y, GameMouseButton Button) : GameInput;

    public sealed record MouseUp(float X, float Y, GameMouseButton Button) : GameInput;

    public sealed record MouseLeave : GameInput;

    /// <param name="Delta">Lines, or pixels when <paramref name="Pixels"/>; positive scrolls up.</param>
    public sealed record Wheel(float Delta, bool Pixels) : GameInput;

    public sealed record KeyDown(GameKey Key) : GameInput;

    public sealed record KeyUp(GameKey Key) : GameInput;

    /// <summary>A typed character, as Ruffle's <c>TextInput</c>.</summary>
    public sealed record Text(int CodePoint) : GameInput;

    /// <summary>A text-editing command such as <c>Backspace</c> or <c>SelectAll</c>, by its Ruffle <c>TextControlCode</c> name.</summary>
    public sealed record TextControl(string Code) : GameInput;

    public sealed record FocusGained : GameInput;

    public sealed record FocusLost : GameInput;

    /// <summary>The <c>U</c> payload after the id.</summary>
    public byte[] Encode()
    {
        List<byte> bytes = [];
        switch (this)
        {
            case MouseMove m:
                Add(bytes, MouseMoveKind, m.X, m.Y);
                break;
            case MouseDown m:
                Add(bytes, MouseDownKind, m.X, m.Y);
                bytes.Add((byte)m.Button);
                break;
            case MouseUp m:
                Add(bytes, MouseUpKind, m.X, m.Y);
                bytes.Add((byte)m.Button);
                break;
            case MouseLeave:
                bytes.Add(MouseLeaveKind);
                break;
            case Wheel w:
                bytes.Add(WheelKind);
                bytes.Add(w.Pixels ? (byte)1 : (byte)0);
                AddF32(bytes, w.Delta);
                break;
            case KeyDown k:
                AddKey(bytes, KeyDownKind, k.Key);
                break;
            case KeyUp k:
                AddKey(bytes, KeyUpKind, k.Key);
                break;
            case Text t:
                bytes.Add(TextKind);
                AddU32(bytes, (uint)t.CodePoint);
                break;
            case TextControl t:
                bytes.Add(TextControlKind);
                AddName(bytes, t.Code);
                break;
            case FocusGained:
                bytes.Add(FocusGainedKind);
                break;
            case FocusLost:
                bytes.Add(FocusLostKind);
                break;
        }
        return [.. bytes];
    }

    /// <summary>The <c>W</c> payload after the id: whether the Game View is live.</summary>
    public static byte[] EncodeView(bool live) => [live ? (byte)1 : (byte)0];

    private static void Add(List<byte> bytes, byte kind, float x, float y)
    {
        bytes.Add(kind);
        AddF32(bytes, x);
        AddF32(bytes, y);
    }

    private static void AddKey(List<byte> bytes, byte kind, GameKey key)
    {
        bytes.Add(kind);
        bytes.Add((byte)key.Location);
        AddU32(bytes, (uint)key.Character);
        AddName(bytes, key.Physical);
        AddName(bytes, key.Named ?? "");
    }

    private static void AddF32(List<byte> bytes, float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }

    private static void AddU32(List<byte> bytes, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }

    private static void AddName(List<byte> bytes, string name)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(name);
        bytes.Add((byte)ascii.Length);
        bytes.AddRange(ascii);
    }
}
