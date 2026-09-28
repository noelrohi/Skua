using System.Collections.Specialized;
using Avalonia.Input;
using Skua.Core.Models;

namespace Skua.Avalonia;

/// <summary>
/// A hotkey's key gesture as the settings keep it, <c>Ctrl+Shift+Alt+Key</c> with WPF's key names (<c>F10</c>, <c>D0</c>), which Avalonia's
/// <see cref="Key"/> shares. On the Mac <c>Ctrl</c> is ⌘ (Command), as a Windows shortcut's Ctrl is a Mac one's ⌘, and <c>Alt</c> is ⌥
/// (Option); ⌃ (Control) takes no hotkey.
/// </summary>
public static class HotKeyGestures
{
    /// <summary>
    /// The Mac's defaults: ⌘ and the digit of each Windows default's F-key (F10 is ⌘0), so plain keys stay with the game and none is a
    /// macOS shortcut.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> MacDefaults = new Dictionary<string, string>
    {
        ["ToggleScript"] = "Ctrl+D0",
        ["LoadScript"] = "Ctrl+D9",
        ["OpenBank"] = "Ctrl+D2",
        ["OpenConsole"] = "Ctrl+D3",
        ["ToggleAutoAttack"] = "Ctrl+D4",
        ["ToggleAutoHunt"] = "Ctrl+D5",
        ["ToggleLagKiller"] = "Ctrl+D6",
    };

    /// <summary>Core's defaults (<c>ClientSettings.InitializeDefaults</c>), the Windows app's F-keys.</summary>
    public static readonly IReadOnlyDictionary<string, string> WindowsDefaults = new Dictionary<string, string>
    {
        ["ToggleScript"] = "F10",
        ["LoadScript"] = "F9",
        ["OpenBank"] = "F2",
        ["OpenConsole"] = "F3",
        ["ToggleAutoAttack"] = "F4",
        ["ToggleAutoHunt"] = "F5",
        ["ToggleLagKiller"] = "F6",
    };

    /// <summary>The key and modifiers of a gesture, or null for one without a key or with a key Avalonia doesn't know.</summary>
    public static (Key Key, KeyModifiers Modifiers)? Parse(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
            return null;
        KeyModifiers modifiers = KeyModifiers.None;
        Key? key = null;
        foreach (string part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "ctl" or "cmd" or "command":
                    modifiers |= KeyModifiers.Meta;
                    break;
                case "alt" or "option":
                    modifiers |= KeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= KeyModifiers.Shift;
                    break;
                default:
                    if (key is not null || ParseKey(part) is not { } parsed)
                        return null;
                    key = parsed;
                    break;
            }
        }
        return key is { } k ? (k, modifiers) : null;
    }

    /// <summary>The gesture as Core's <see cref="HotKey"/>: its key name and its ⌘ (<c>Ctrl</c>), ⌥ (<c>Alt</c>) and ⇧ flags.</summary>
    public static HotKey? ToHotKey(string? gesture) => Parse(gesture) is var (key, modifiers)
        ? new HotKey(key.ToString(), modifiers.HasFlag(KeyModifiers.Meta), modifiers.HasFlag(KeyModifiers.Alt), modifiers.HasFlag(KeyModifiers.Shift))
        : null;

    /// <summary>The gesture as a Mac menu shows one, e.g. <c>⌥⇧⌘0</c>; one that doesn't parse is shown as it is.</summary>
    public static string Display(string? gesture)
    {
        if (Parse(gesture) is not var (key, modifiers))
            return gesture ?? string.Empty;
        // macOS orders them ⌃⌥⇧⌘.
        return (modifiers.HasFlag(KeyModifiers.Alt) ? "⌥" : "")
            + (modifiers.HasFlag(KeyModifiers.Shift) ? "⇧" : "")
            + (modifiers.HasFlag(KeyModifiers.Meta) ? "⌘" : "")
            + KeyName(key);
    }

    /// <summary>A key as shown to the developer: a digit for <c>D0</c>, the key's name otherwise.</summary>
    public static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => key.ToString(),
    };

    /// <summary>Whether a key only modifies others, so it can't be a hotkey by itself.</summary>
    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.CapsLock or Key.None;

    /// <summary>
    /// Replaces a list that is still Core's defaults, in any order and whether or not it lists every hotkey (an older one lacks Lag
    /// Killer's), with the Mac's, and reports whether it did. A list with any binding the developer changed is theirs and stays as it is.
    /// </summary>
    public static bool UseMacDefaults(StringCollection hotKeys)
    {
        int bound = 0;
        foreach (string? entry in hotKeys)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            string[] split = entry.Split('|');
            if (split.Length != 2 || WindowsDefaults.GetValueOrDefault(split[0]) != split[1])
                return false;
            bound++;
        }
        if (bound == 0)
            return false;
        hotKeys.Clear();
        foreach ((string binding, string gesture) in MacDefaults)
            hotKeys.Add($"{binding}|{gesture}");
        return true;
    }

    private static Key? ParseKey(string name)
    {
        if (name.Length == 1 && char.IsAsciiDigit(name[0]))
            return Key.D0 + (name[0] - '0');
        return Enum.TryParse(name, ignoreCase: true, out Key key) && Enum.IsDefined(key) && !IsModifier(key) && !int.TryParse(name, out _) ? key : null;
    }
}
