using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Utils;
using Skua.MacOS.GameHost;

namespace Skua.Avalonia.Services;

/// <summary>
/// Core's hotkeys in the Mac App, the counterpart of <c>Skua.WPF</c>'s <c>HotKeyService</c>: each binding in the <c>HotKeys</c> setting runs
/// its command when its key gesture is pressed in any of the app's windows. The gestures are the settings' (<see cref="HotKeyGestures"/>),
/// with ⌘ for <c>Ctrl</c>, and a list still at Core's F-key defaults gets the Mac's ⌘ ones.
/// </summary>
/// <remarks>
/// <para>
/// Who gets a key: in the Game View a bound gesture goes to its hotkey, before the game sees it, unless the player is typing in the game
/// (<see cref="BridgeFlashUtil.IsTyping"/>), such as in chat, when every key goes to the game. Anywhere else a gesture a control doesn't
/// handle goes to its hotkey, as a WPF window's key bindings take it, except one that types text into a text box.
/// </para>
/// <para>
/// It binds only while one of the app's windows is the key window, so it needs no <c>user32</c> foreground check (Core's hotkey commands
/// guard with one, so it runs them without asking them), and it registers no system-wide hotkey.
/// </para>
/// </remarks>
public sealed class AvaloniaHotKeyService : IHotKeyService, IDisposable
{
    /// <summary>The setting Core's hotkeys panel saves: one <c>Binding|Gesture</c> line per hotkey.</summary>
    public const string SettingKey = "HotKeys";

    /// <summary>
    /// How long a gesture pressed in the Game View waits for the Game Client to say whether the player is typing; the key goes to the game
    /// when it doesn't answer in time.
    /// </summary>
    public static readonly TimeSpan TypingTimeout = TimeSpan.FromMilliseconds(200);

    private readonly Dictionary<string, IRelayCommand> _hotKeys;
    private readonly ISettingsService _settings;
    private readonly IDecamelizer _decamelizer;
    private readonly BridgeFlashUtil _flash;
    private readonly IDisposable[] _handlers;
    /// <summary>The keys whose key down ran a hotkey; their key up is the hotkey's too, so the game never sees half a key press.</summary>
    private readonly HashSet<Key> _taken = [];
    private Binding[] _bindings = [];

    public AvaloniaHotKeyService(Dictionary<string, IRelayCommand> hotKeys, ISettingsService settings, IDecamelizer decamelizer, BridgeFlashUtil flash)
    {
        _hotKeys = hotKeys;
        _settings = settings;
        _decamelizer = decamelizer;
        _flash = flash;
        _handlers =
        [
            InputElement.KeyDownEvent.AddClassHandler<Window>(OnGameViewKeyDown, RoutingStrategies.Tunnel),
            InputElement.KeyDownEvent.AddClassHandler<Window>(OnKeyDown, RoutingStrategies.Bubble),
            InputElement.KeyUpEvent.AddClassHandler<Window>(OnKeyUp, RoutingStrategies.Tunnel),
        ];
        Reload();
    }

    /// <summary>Raised on the UI thread with a hotkey's binding, e.g. <c>ToggleScript</c>, as its command runs.</summary>
    public event Action<string>? Fired;

    public void Reload()
    {
        List<Binding> bindings = [];
        foreach (string? entry in Load())
        {
            if (string.IsNullOrEmpty(entry))
                continue;
            string[] split = entry.Split('|');
            if (!_hotKeys.TryGetValue(split[0], out IRelayCommand? command) || split.Length < 2 || string.IsNullOrWhiteSpace(split[1]))
                continue;
            if (HotKeyGestures.Parse(split[1]) is not var (key, modifiers))
            {
                StrongReferenceMessenger.Default.Send<HotKeyErrorMessage>(new(split[0]));
                continue;
            }
            bindings.Add(new Binding(split[0], key, modifiers, command));
        }
        Volatile.Write(ref _bindings, [.. bindings]);
    }

    public List<T> GetHotKeys<T>() where T : IHotKey, new()
    {
        List<T> parsed = [];
        foreach (string? entry in Load())
        {
            if (string.IsNullOrEmpty(entry))
                continue;
            string[] split = entry.Split('|');
            parsed.Add(new() { Binding = split[0], Title = _decamelizer.Decamelize(split[0], null), KeyGesture = split.Length > 1 ? split[1] : string.Empty });
        }
        return parsed;
    }

    public HotKey? ParseToHotKey(string keyGesture) => HotKeyGestures.ToHotKey(keyGesture);

    /// <summary>The binding a key press runs, or null.</summary>
    public string? BindingFor(Key key, KeyModifiers modifiers) => Match(key, modifiers)?.Name;

    public void Dispose()
    {
        foreach (IDisposable handler in _handlers)
            handler.Dispose();
    }

    /// <summary>The setting, with the Mac's defaults in place of Core's and every hotkey listed; saved when that changed it.</summary>
    private StringCollection Load()
    {
        StringCollection hotKeys = _settings.Get<StringCollection>(SettingKey) ?? [];
        bool changed = HotKeyGestures.UseMacDefaults(hotKeys);
        changed |= AddMissing(hotKeys);
        if (changed)
            _settings.Set(SettingKey, hotKeys);
        return hotKeys;
    }

    /// <summary>Adds each hotkey the setting lacks, with its Mac default unless another binding has that gesture, as the WPF app adds Lag Killer's.</summary>
    private bool AddMissing(StringCollection hotKeys)
    {
        HashSet<string> listed = [];
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? entry in hotKeys)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            string[] split = entry.Split('|');
            listed.Add(split[0]);
            if (split.Length > 1 && !string.IsNullOrWhiteSpace(split[1]))
                used.Add(split[1]);
        }
        bool added = false;
        foreach (string binding in _hotKeys.Keys.Where(b => !listed.Contains(b)))
        {
            string gesture = HotKeyGestures.MacDefaults.GetValueOrDefault(binding) is { } d && !used.Contains(d) ? d : string.Empty;
            hotKeys.Add($"{binding}|{gesture}");
            added = true;
        }
        return added;
    }

    /// <summary>A gesture in the Game View goes to its hotkey first, unless the player is typing in the game, or it isn't known in time.</summary>
    private void OnGameViewKeyDown(Window window, KeyEventArgs e)
    {
        if (e.Source is GameView && Match(e.Key, e.KeyModifiers) is { } binding && _flash.IsTyping(TypingTimeout) == false)
            Fire(binding, e);
    }

    /// <summary>Anywhere else, a gesture no control handled, unless it types text into a text box.</summary>
    private void OnKeyDown(Window window, KeyEventArgs e)
    {
        if (e.Source is GameView || Match(e.Key, e.KeyModifiers) is not { } binding)
            return;
        if (e.Source is TextBox && GameKeys.TypesText(e.Key, e.KeyModifiers, e.KeySymbol))
            return;
        Fire(binding, e);
    }

    private void OnKeyUp(Window window, KeyEventArgs e)
    {
        if (_taken.Remove(e.Key))
            e.Handled = true;
    }

    /// <summary>Takes the key and runs the command after the key event, so a command that shows a window or dialog doesn't run inside it.</summary>
    private void Fire(Binding binding, KeyEventArgs e)
    {
        e.Handled = true;
        _taken.Add(e.Key);
        Dispatcher.UIThread.Post(() =>
        {
            Fired?.Invoke(binding.Name);
            binding.Command.Execute(null);
        });
    }

    /// <summary>
    /// The binding of a key press: the same key with the same ⌘, ⌥ and ⇧. A press with ⌃ matches none, so ⌃ keeps its macOS and game
    /// meanings.
    /// </summary>
    private Binding? Match(Key key, KeyModifiers modifiers)
    {
        if (HotKeyGestures.IsModifier(key))
            return null;
        KeyModifiers pressed = modifiers & (KeyModifiers.Meta | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Control);
        foreach (Binding binding in Volatile.Read(ref _bindings))
        {
            if (binding.Key == key && binding.Modifiers == pressed)
                return binding;
        }
        return null;
    }

    private sealed record Binding(string Name, Key Key, KeyModifiers Modifiers, IRelayCommand Command);
}
