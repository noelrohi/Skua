using System.Buffers.Binary;
using System.Globalization;
using System.Text;

/// <summary>Describes a 'U' input event for the call log (see Skua.MacOS/GameHost/GameInput.cs for the encoding).</summary>
internal static class FakeInput
{
    private static readonly string[] Buttons = ["unknown", "left", "right", "middle"];

    /// <summary>The position of a mouse move, down or up, in viewport pixels; null for any other event.</summary>
    public static (float X, float Y)? Position(ReadOnlySpan<byte> payload) =>
        payload.Length >= 9 && payload[0] is 1 or 2 or 3
            ? (BinaryPrimitives.ReadSingleLittleEndian(payload[1..]), BinaryPrimitives.ReadSingleLittleEndian(payload[5..]))
            : null;

    /// <summary>The text-control code of a text-control event, else null.</summary>
    public static string? TextControl(ReadOnlySpan<byte> payload) => payload.Length >= 2 && payload[0] == 9 ? Name(payload[1..], out _) : null;

    /// <summary>E.g. <c>mouseDown 479 275 left</c>, <c>keyDown KeyA a</c>, <c>keyUp ShiftLeft Shift left</c>, <c>text é</c>.</summary>
    public static string Describe(ReadOnlySpan<byte> payload)
    {
        try
        {
            return payload[0] switch
            {
                1 => $"mouseMove {F(payload, 1)} {F(payload, 5)}",
                2 => $"mouseDown {F(payload, 1)} {F(payload, 5)} {Button(payload[9])}",
                3 => $"mouseUp {F(payload, 1)} {F(payload, 5)} {Button(payload[9])}",
                4 => "mouseLeave",
                5 => $"wheel {F(payload, 2)} {(payload[1] == 1 ? "pixels" : "lines")}",
                6 => "keyDown " + Key(payload[1..]),
                7 => "keyUp " + Key(payload[1..]),
                8 => "text " + char.ConvertFromUtf32((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[1..])),
                9 => "textControl " + Name(payload[1..], out _),
                10 => "focusGained",
                11 => "focusLost",
                12 => "clipboard " + Encoding.UTF8.GetString(payload[1..]),
                byte kind => $"unknown {kind}",
            };
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return "malformed";
        }
    }

    private static string F(ReadOnlySpan<byte> payload, int at) =>
        BinaryPrimitives.ReadSingleLittleEndian(payload[at..]).ToString(CultureInfo.InvariantCulture);

    private static string Button(byte button) => button < Buttons.Length ? Buttons[button] : "unknown";

    // location, u32 char, physical name, named name → "<physical> <char or named>[ <location>]"
    private static string Key(ReadOnlySpan<byte> key)
    {
        string[] locations = ["", " left", " right", " numpad"];
        byte location = key[0];
        uint ch = BinaryPrimitives.ReadUInt32LittleEndian(key[1..]);
        string physical = Name(key[5..], out int used);
        string named = Name(key[(5 + used)..], out _);
        string logical = ch != 0 ? char.ConvertFromUtf32((int)ch) : named.Length > 0 ? named : "unknown";
        return $"{physical} {logical}{(location < locations.Length ? locations[location] : "")}";
    }

    private static string Name(ReadOnlySpan<byte> bytes, out int used)
    {
        used = 1 + bytes[0];
        return Encoding.ASCII.GetString(bytes.Slice(1, bytes[0]));
    }
}
