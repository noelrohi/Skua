using Avalonia.Input;
using Skua.MacOS.GameHost;

namespace Skua.Avalonia;

/// <summary>
/// Maps Avalonia's keys to Ruffle's, as Ruffle's desktop player maps winit's (Ruffle <c>desktop/src/util.rs</c>): the physical key to a
/// <c>PhysicalKey</c> name, the key symbol or key to the logical key, and modifier-qualified keys to text-editing commands.
/// </summary>
public static class GameKeys
{
    /// <summary>The key a key event stands for.</summary>
    public static GameKey Map(PhysicalKey physical, Key key, string? symbol) =>
        new(PhysicalName(physical), Named(key) is null ? Character(key, symbol) : 0, Named(key), Location(physical));

    /// <summary>
    /// The Ruffle <c>PhysicalKey</c> name: Avalonia's names are the W3C codes Ruffle uses, except letters (<c>A</c> is <c>KeyA</c>),
    /// the numpad (<c>NumPad1</c> is <c>Numpad1</c>) and the Command keys (<c>MetaLeft</c> is <c>SuperLeft</c>). Ruffle reads a name it
    /// doesn't have (e.g. <c>Help</c>, as Ruffle's desktop player maps it) as an unknown key.
    /// </summary>
    public static string PhysicalName(PhysicalKey physical) => physical switch
    {
        >= PhysicalKey.A and <= PhysicalKey.Z => "Key" + physical,
        PhysicalKey.MetaLeft => "SuperLeft",
        PhysicalKey.MetaRight => "SuperRight",
        PhysicalKey.None => "Unknown",
        _ when physical.ToString().StartsWith("NumPad", StringComparison.Ordinal) => "Numpad" + physical.ToString()["NumPad".Length..],
        _ => physical.ToString(),
    };

    public static GameKeyLocation Location(PhysicalKey physical) => physical switch
    {
        PhysicalKey.ShiftLeft or PhysicalKey.ControlLeft or PhysicalKey.AltLeft or PhysicalKey.MetaLeft => GameKeyLocation.Left,
        PhysicalKey.ShiftRight or PhysicalKey.ControlRight or PhysicalKey.AltRight or PhysicalKey.MetaRight => GameKeyLocation.Right,
        _ when physical.ToString().StartsWith("NumPad", StringComparison.Ordinal) => GameKeyLocation.Numpad,
        _ => GameKeyLocation.Standard,
    };

    /// <summary>The Ruffle <c>NamedKey</c> of a key that types no character, or null.</summary>
    public static string? Named(Key key) => key switch
    {
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.CapsLock => "CapsLock",
        Key.LeftCtrl or Key.RightCtrl => "Control",
        Key.LWin or Key.RWin => "Super",
        Key.NumLock => "NumLock",
        Key.Scroll => "ScrollLock",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Down => "ArrowDown",
        Key.Left => "ArrowLeft",
        Key.Right => "ArrowRight",
        Key.Up => "ArrowUp",
        Key.End => "End",
        Key.Home => "Home",
        Key.PageDown => "PageDown",
        Key.PageUp => "PageUp",
        Key.Back => "Backspace",
        Key.Clear or Key.OemClear => "Clear",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Apps => "ContextMenu",
        Key.Escape => "Escape",
        Key.Pause => "Pause",
        Key.Play or Key.MediaPlayPause => "Play",
        Key.Select => "Select",
        Key.Zoom => "ZoomIn",
        Key.PrintScreen => "PrintScreen",
        >= Key.F1 and <= Key.F24 => key.ToString(),
        _ => null,
    };

    /// <summary>The character a key types, as Ruffle's desktop player takes it: the last one of the symbol, a space for Space, else 0.</summary>
    public static int Character(Key key, string? symbol)
    {
        if (key == Key.Space)
            return ' ';
        if (string.IsNullOrEmpty(symbol))
            return 0;
        int last = char.IsLowSurrogate(symbol[^1]) && symbol.Length > 1 ? char.ConvertToUtf32(symbol[^2], symbol[^1]) : symbol[^1];
        return IsTyped(last) ? last : 0;
    }

    /// <summary>
    /// The text-editing command a key press stands for, or null (Ruffle's <c>winit_to_ruffle_text_control</c>); Command counts as Control.
    /// </summary>
    public static string? TextControl(Key key, KeyModifiers modifiers)
    {
        bool shift = modifiers.HasFlag(KeyModifiers.Shift);
        bool command = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);
        return key switch
        {
            Key.Enter => "Enter",
            Key.A when command => "SelectAll",
            Key.C when command => "Copy",
            Key.V when command => "Paste",
            Key.X when command => "Cut",
            Key.Back => command ? "BackspaceWord" : "Backspace",
            Key.Delete => command ? "DeleteWord" : "Delete",
            Key.Left => Move("Left", "Word", command, shift),
            Key.Right => Move("Right", "Word", command, shift),
            Key.Home => Move("Left", "Document", command, shift, "Line"),
            Key.End => Move("Right", "Document", command, shift, "Line"),
            _ => null,
        };
    }

    // MoveLeft, SelectLeftWord, MoveRightLine, SelectLeftDocument…
    private static string Move(string side, string withCommand, bool command, bool shift, string without = "") =>
        (shift ? "Select" : "Move") + side + (command ? withCommand : without);

    /// <summary>
    /// Whether a key down types text: it has a printable symbol, isn't a named key such as Tab, and isn't a Command or Control shortcut
    /// (those go to the game as text-editing commands).
    /// </summary>
    public static bool TypesText(Key key, KeyModifiers modifiers, string? symbol)
    {
        if (string.IsNullOrEmpty(symbol) || Named(key) is not null || TextControl(key, modifiers) is not null
            || modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta))
            return false;
        for (int i = 0; i < symbol.Length; i += char.IsSurrogatePair(symbol, i) ? 2 : 1)
        {
            if (!IsTyped(char.ConvertToUtf32(symbol, i)))
                return false;
        }
        return true;
    }

    /// <summary>Whether a character from text input is typed text: not a control character or one of macOS's function-key characters.</summary>
    public static bool IsTyped(int codePoint) =>
        codePoint >= 0x20 && codePoint != 0x7F && codePoint is not (>= 0xF700 and <= 0xF8FF);
}
