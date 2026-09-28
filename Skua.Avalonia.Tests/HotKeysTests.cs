using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Services;
using Skua.Core.Utils;
using Skua.Core.ViewModels;
using static Skua.Avalonia.Tests.Ui;

namespace Skua.Avalonia.Tests;

/// <summary>
/// The hotkeys: the Mac's ⌘ defaults, a gesture firing from every kind of window, the Game View giving a gesture to its hotkey unless the
/// player is typing in the game, and a gesture assigned in the panel that is saved and bound again at the next start.
/// </summary>
/// <remarks>
/// In the Game View tests' collection, as they share the one Engine and its game. Every key pressed is released and every window closed
/// in a finally, and each test puts back the bindings, the game's focus and the lag killer it changed.
/// </remarks>
[Collection(nameof(GameViewTests))]
public sealed class HotKeysTests(AppEngine app)
{
    private AvaloniaHotKeyService Service => Assert.IsType<AvaloniaHotKeyService>(app.Get<IHotKeyService>());

    [AvaloniaFact]
    public void A_list_still_at_Cores_F_key_defaults_gets_the_Macs_Command_digits_and_one_the_developer_changed_is_kept()
    {
        StringCollection windows = [.. HotKeyGestures.WindowsDefaults.Select(d => $"{d.Key}|{d.Value}")];
        Assert.True(HotKeyGestures.UseMacDefaults(windows));
        Assert.Equal(HotKeyGestures.MacDefaults.Select(d => $"{d.Key}|{d.Value}"), windows.Cast<string>());

        // An older list without Lag Killer is still the defaults.
        StringCollection older = [.. HotKeyGestures.WindowsDefaults.Where(d => d.Key != "ToggleLagKiller").Select(d => $"{d.Key}|{d.Value}")];
        Assert.True(HotKeyGestures.UseMacDefaults(older));

        StringCollection changed = [.. HotKeyGestures.WindowsDefaults.Select(d => $"{d.Key}|{(d.Key == "OpenBank" ? "F7" : d.Value)}")];
        Assert.False(HotKeyGestures.UseMacDefaults(changed));
        Assert.Contains("OpenBank|F7", changed.Cast<string>());

        // The app's data folder started with Core's defaults.
        Assert.Equal(
            HotKeyGestures.MacDefaults.OrderBy(d => d.Key).Select(d => $"{d.Key}|{d.Value}"),
            Service.GetHotKeys<HotKeyItemViewModel>().OrderBy(h => h.Binding).Select(h => $"{h.Binding}|{h.KeyGesture}"));
        Assert.Equal("Toggle Lag Killer", Service.GetHotKeys<HotKeyItemViewModel>().Single(h => h.Binding == "ToggleLagKiller").Title);
    }

    [AvaloniaTheory]
    [InlineData("Ctrl+D0", Key.D0, KeyModifiers.Meta, "⌘0")]
    [InlineData("Ctrl+Shift+Alt+B", Key.B, KeyModifiers.Meta | KeyModifiers.Shift | KeyModifiers.Alt, "⌥⇧⌘B")]
    [InlineData("F10", Key.F10, KeyModifiers.None, "F10")]
    [InlineData("alt+f4", Key.F4, KeyModifiers.Alt, "⌥F4")]
    [InlineData("Cmd+7", Key.D7, KeyModifiers.Meta, "⌘7")]
    [InlineData("Ctrl+", null, KeyModifiers.None, "Ctrl+")]
    [InlineData("Ctrl+LeftShift", null, KeyModifiers.None, "Ctrl+LeftShift")]
    [InlineData("Failed to bind", null, KeyModifiers.None, "Failed to bind")]
    [InlineData("", null, KeyModifiers.None, "")]
    public void A_gesture_parses_to_a_key_with_Ctrl_as_Command_and_shows_as_a_Mac_menu_does(string gesture, Key? key, KeyModifiers modifiers, string shown)
    {
        Assert.Equal(key is { } k ? (k, modifiers) : null, HotKeyGestures.Parse(gesture));
        Assert.Equal(shown, HotKeyGestures.Display(gesture));
        if (key is not null)
        {
            HotKey hotKey = Service.ParseToHotKey(gesture)!;
            Assert.Equal((key.ToString(), modifiers.HasFlag(KeyModifiers.Meta), modifiers.HasFlag(KeyModifiers.Alt), modifiers.HasFlag(KeyModifiers.Shift)),
                (hotKey.Key, hotKey.Ctrl, hotKey.Alt, hotKey.Shift));
        }
    }

    [AvaloniaFact]
    public async Task A_bound_hotkey_fires_from_the_Game_View_a_panel_and_a_dialog()
    {
        List<string> fired = [];
        Service.Fired += fired.Add;
        (Window game, GameView view) = await ShowGameAsync();
        HostWindow? panel = null;
        DialogWindow? dialog = null;
        try
        {
            view.Focus();
            await PressAsync(game, Key.D3, RawInputModifiers.Meta, PhysicalKey.Digit3, "3");

            panel = await ShowPanelAsync();
            await PressAsync(panel, Key.D3, RawInputModifiers.Meta, PhysicalKey.Digit3, "3");

            dialog = new DialogWindow(new InputDialogViewModel("Input"));
            dialog.Show();
            await PumpUntilAsync(() => Find<TextBox>(dialog) is { Bounds.Width: > 0 }, "the dialog's text box");
            Find<TextBox>(dialog)!.Focus();
            await PressAsync(dialog, Key.D3, RawInputModifiers.Meta, PhysicalKey.Digit3, "3");

            await PumpUntilAsync(() => fired.Count >= 3, "the hotkey to fire in each window");
            Assert.Equal(["OpenConsole", "OpenConsole", "OpenConsole"], fired);
        }
        finally
        {
            Service.Fired -= fired.Add;
            dialog?.Close();
            panel?.Close();
            game.Close();
        }
    }

    [AvaloniaFact]
    public async Task In_the_Game_View_a_hotkey_takes_its_key_from_the_game_and_other_keys_reach_the_game()
    {
        List<string> fired = [];
        Service.Fired += fired.Add;
        IScriptOption options = app.Get<IScriptOption>();
        (Window window, GameView view) = await ShowGameAsync();
        try
        {
            view.Focus();
            int from = AppEngine.Calls().Length;

            await PressAsync(window, Key.D6, RawInputModifiers.Meta, PhysicalKey.Digit6, "6");
            await PressAsync(window, Key.D7, RawInputModifiers.Meta, PhysicalKey.Digit7, "7");

            string[] calls = await WaitForCallsAsync(from, c => c.Contains("input keyUp Digit7 7"));
            Assert.Contains("input keyDown Digit7 7", calls);
            Assert.DoesNotContain(calls, c => c.StartsWith("input keyDown Digit6", StringComparison.Ordinal) || c.StartsWith("input keyUp Digit6", StringComparison.Ordinal));
            await PumpUntilAsync(() => fired.Count > 0, "the hotkey to fire");
            Assert.Equal(["ToggleLagKiller"], fired);
            Assert.True(options.LagKiller);
            Assert.True(view.IsFocused, "the game keeps the focus");
        }
        finally
        {
            Service.Fired -= fired.Add;
            options.LagKiller = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Typing_in_the_games_chat_never_fires_a_hotkey()
    {
        List<string> fired = [];
        Service.Fired += fired.Add;
        IScriptOption options = app.Get<IScriptOption>();
        (Window window, GameView view) = await ShowGameAsync();
        StringCollection before = Bindings();
        try
        {
            // A plain letter, which the game would otherwise lose.
            Bind("ToggleLagKiller", "B");
            view.Focus();
            await AppEngine.DoAsync("focus input");
            int from = AppEngine.Calls().Length;

            await PressAsync(window, Key.B, RawInputModifiers.None, PhysicalKey.B, "b", text: "b");
            // A ⌘ gesture goes to the game too while the player types.
            await PressAsync(window, Key.D3, RawInputModifiers.Meta, PhysicalKey.Digit3, "3");

            string[] calls = await WaitForCallsAsync(from, c => c.Contains("input keyUp Digit3 3"));
            Assert.Equal(["input keyDown KeyB b", "input text b", "input keyUp KeyB b", "input keyDown Digit3 3", "input keyUp Digit3 3"],
                calls.Where(c => c.StartsWith("input key", StringComparison.Ordinal) || c.StartsWith("input text", StringComparison.Ordinal)));

            // A text field the player can't type in, such as a selectable label, isn't typing.
            await AppEngine.DoAsync("focus dynamic");
            await PressAsync(window, Key.B, RawInputModifiers.None, PhysicalKey.B, "b");
            await PumpUntilAsync(() => fired.Count > 0, "the hotkey to fire once the player isn't typing");
            Assert.Equal(["ToggleLagKiller"], fired);
        }
        finally
        {
            Service.Fired -= fired.Add;
            await AppEngine.DoAsync("focus none");
            Restore(before);
            options.LagKiller = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Typing_Option_characters_in_a_text_box_doesnt_fire_an_Option_hotkey_but_a_Command_one_does()
    {
        List<string> fired = [];
        Service.Fired += fired.Add;
        StringCollection before = Bindings();
        TextBox box = new();
        Window window = new() { Content = box };
        try
        {
            Bind("ToggleLagKiller", "Alt+E");
            window.Show();
            box.Focus();
            await PumpUntilAsync(() => box.IsFocused, "the text box to take focus");

            await PressAsync(window, Key.E, RawInputModifiers.Alt, PhysicalKey.E, "é", text: "é");
            await PressAsync(window, Key.D3, RawInputModifiers.Meta, PhysicalKey.Digit3, "3");

            await PumpUntilAsync(() => fired.Count > 0, "the ⌘ hotkey to fire");
            Assert.Equal(["OpenConsole"], fired);
            Assert.Equal("é", box.Text);
        }
        finally
        {
            Service.Fired -= fired.Add;
            Restore(before);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_hotkey_assigned_in_the_panel_is_saved_works_at_once_and_is_bound_again_after_a_restart()
    {
        List<string> fired = [];
        Service.Fired += fired.Add;
        IScriptOption options = app.Get<IScriptOption>();
        StringCollection before = Bindings();
        AvaloniaDialogService dialogs = app.Get<AvaloniaDialogService>();
        Action<Window>? previous = dialogs.WindowCreated;
        List<DialogWindow> opened = [];
        dialogs.WindowCreated = w =>
        {
            previous?.Invoke(w);
            if (w is DialogWindow d)
                opened.Add(d);
        };
        HostWindow panel = await ShowPanelAsync();
        Window? game = null;
        try
        {
            HotKeyItemView item = Find<HotKeyItemView>(panel, v => v.DataContext is HotKeyItemViewModel { Binding: "ToggleLagKiller" })!;
            Button gesture = item.FindControl<Button>("Gesture")!;
            Assert.Equal("⌘6", Text(gesture));

            // The panel's edit shows the dialog and waits for it in a nested frame. So the click is posted, and this waits without running
            // the dispatcher's jobs itself: the frame then runs under the test's dispatcher loop, and the rest of the test runs in it.
            Dispatcher.UIThread.Post(() => Click(gesture));
            await YieldUntilAsync(() => opened.Count > 0 && opened[0] is { IsVisible: true, DataContext: AssignHotKeyDialogViewModel }, "the assign dialog");
            DialogWindow dialog = opened[0];
            // Now inside the frame, the dispatcher's jobs can run here: the dialog lays out, so it has a size to close with.
            await PumpUntilAsync(() => Find<AssignHotKeyDialogView>(dialog) is { Bounds.Width: > 0 }, "the assign dialog's layout");
            AssignHotKeyDialogView view = Find<AssignHotKeyDialogView>(dialog)!;
            AssignHotKeyDialogViewModel model = (AssignHotKeyDialogViewModel)dialog.DataContext!;
            Assert.Equal(("D6", true, false, false), (model.KeyInput, model.CtrlCheck, model.AltCheck, model.ShiftCheck));

            Click(view.FindControl<Button>("Capture")!);
            Assert.Equal(AssignHotKeyDialogView.WaitingInputText, model.KeyInput);
            // ⌘⌥L is taken as the new key, with the modifiers held with it; it isn't anything's hotkey, so nothing fires.
            await PressAsync(dialog, Key.L, RawInputModifiers.Meta | RawInputModifiers.Alt, PhysicalKey.L, "¬");
            Assert.Equal(("L", true, true, false), (model.KeyInput, model.CtrlCheck, model.AltCheck, model.ShiftCheck));
            Assert.Equal("L", Text(view.FindControl<Button>("Capture")!));
            Click(view.FindControl<Button>("Save")!);

            await PumpUntilAsync(() => !dialog.IsVisible && Text(gesture) == "⌥⌘L", "the panel to show the new gesture");
            Assert.Contains("ToggleLagKiller|Ctrl+Alt+L", Saved());

            // It works at once, from the Game View too.
            (game, GameView gameView) = await ShowGameAsync();
            gameView.Focus();
            await PressAsync(game, Key.L, RawInputModifiers.Meta | RawInputModifiers.Alt, PhysicalKey.L, "¬");
            await PumpUntilAsync(() => fired.Count > 0, "the new hotkey to fire");
            Assert.Equal(["ToggleLagKiller"], fired);
            Assert.True(options.LagKiller);

            // A restarted app reads the saved gesture and binds it; the old one is free.
            using AvaloniaHotKeyService restarted = new(
                new Dictionary<string, IRelayCommand> { ["ToggleLagKiller"] = new RelayCommand(() => { }) }, new FileSettings(), new Decamelizer(), app.Flash);
            Assert.Equal("ToggleLagKiller", restarted.BindingFor(Key.L, KeyModifiers.Meta | KeyModifiers.Alt));
            Assert.Null(restarted.BindingFor(Key.D6, KeyModifiers.Meta));
        }
        finally
        {
            Service.Fired -= fired.Add;
            dialogs.WindowCreated = previous;
            foreach (DialogWindow d in opened)
                d.Close();
            Restore(before);
            options.LagKiller = false;
            game?.Close();
            panel.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_assign_dialog_keeps_its_key_on_Esc_and_refuses_a_modifier_alone_Control_or_saving_while_it_waits()
    {
        AssignHotKeyDialogViewModel model = new("Toggle Script", new HotKey("D0", ctrl: true, alt: false, shift: false));
        DialogWindow dialog = new(model);
        using PanelTests.BindingErrors errors = new();
        try
        {
            dialog.Show();
            await PumpUntilAsync(() => Find<AssignHotKeyDialogView>(dialog) is not null, "the dialog's view");
            AssignHotKeyDialogView view = Find<AssignHotKeyDialogView>(dialog)!;
            Button capture = view.FindControl<Button>("Capture")!;
            Assert.Equal("0", Text(capture));

            Click(capture);
            await PressAsync(dialog, Key.LeftShift, RawInputModifiers.Shift, PhysicalKey.ShiftLeft, null);
            Assert.Equal((AssignHotKeyDialogView.WaitingInputText, AssignHotKeyDialogView.ModifierOnlyHintText), (model.KeyInput, model.InputHint));
            await PressAsync(dialog, Key.K, RawInputModifiers.Control, PhysicalKey.K, "k");
            Assert.Equal((AssignHotKeyDialogView.WaitingInputText, AssignHotKeyDialogView.ControlHintText), (model.KeyInput, model.InputHint));
            await PressAsync(dialog, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "\u001b");
            Assert.Equal(("D0", ""), (model.KeyInput, model.InputHint));
            Assert.True(dialog.IsVisible, "Esc while waiting keeps the dialog open");

            Click(capture);
            Click(view.FindControl<Button>("Save")!);
            Assert.Equal(("D0", AssignHotKeyDialogView.SaveWithoutKeyHintText), (model.KeyInput, model.InputHint));
            Assert.True(dialog.IsVisible);

            Click(capture);
            await PressAsync(dialog, Key.F8, RawInputModifiers.None, PhysicalKey.F8, null);
            Click(view.FindControl<Button>("Save")!);
            await PumpUntilAsync(() => !dialog.IsVisible, "the dialog to close");
            Assert.Equal((true, "Ctrl+F8"), (dialog.Result, model.KeyGesture));
            Assert.Empty(errors.Lines);
        }
        finally
        {
            dialog.Close();
        }
    }

    private async Task<(Window, GameView)> ShowGameAsync()
    {
        GameView view = new(app.Flash);
        Window window = new() { Width = 958, Height = 550, Content = view };
        window.Show();
        await PumpUntilAsync(() => view.IsLive && view.Bounds.Width > 0, "the Game View to be live");
        return (window, view);
    }

    private async Task<HostWindow> ShowPanelAsync()
    {
        _ = app.Get<MainMenuViewModel>();
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        windows.ShowManagedWindow("HotKeys");
        await PumpUntilAsync(() => windows.OpenWindow("HotKeys") is { } w && Find<HotKeyItemView>(w) is not null, "the HotKeys panel");
        await PumpUntilAsync(() => true, "a layout pass");
        return windows.OpenWindow("HotKeys")!;
    }

    /// <summary>Waits by yielding to the test's dispatcher loop only, for work that runs in a nested frame it pushes.</summary>
    private static async Task YieldUntilAsync(Func<bool> done, string what)
    {
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (waited.Elapsed > Ui.Timeout)
                throw new TimeoutException($"Waited {Ui.Timeout.TotalSeconds} s for {what}.");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10);
        }
    }

    /// <summary>Presses and releases a key, with its typed text if any, releasing it however the press went.</summary>
    private static async Task PressAsync(Window window, Key key, RawInputModifiers modifiers, PhysicalKey physical, string? symbol, string? text = null)
    {
        try
        {
            window.KeyPress(key, modifiers, physical, symbol!);
            if (text is not null)
                window.KeyTextInput(text);
        }
        finally
        {
            window.KeyRelease(key, modifiers, physical, symbol!);
        }
        await PumpUntilAsync(() => true, "the key's handlers");
    }

    private StringCollection Bindings() => [.. app.Get<ISettingsService>().Get<StringCollection>(AvaloniaHotKeyService.SettingKey)!.Cast<string>()];

    /// <summary>Binds <paramref name="binding"/> to <paramref name="gesture"/>, as the panel saves one, and reloads the bindings.</summary>
    private void Bind(string binding, string gesture)
    {
        StringCollection hotKeys = [.. Bindings().Cast<string>().Select(h => h.StartsWith(binding + "|", StringComparison.Ordinal) ? $"{binding}|{gesture}" : h)];
        Restore(hotKeys);
    }

    /// <summary>Saves the bindings and reloads them; the panel's items follow, as the ones a test changed must.</summary>
    private void Restore(StringCollection hotKeys)
    {
        app.Get<ISettingsService>().Set(AvaloniaHotKeyService.SettingKey, hotKeys);
        Service.Reload();
        foreach (HotKeyItemViewModel item in app.Get<HotKeysViewModel>().HotKeys)
        {
            string? entry = hotKeys.Cast<string>().FirstOrDefault(h => h.StartsWith(item.Binding + "|", StringComparison.Ordinal));
            if (entry is not null)
                item.KeyGesture = entry[(item.Binding.Length + 1)..];
        }
    }

    /// <summary>The bindings a new start reads, from the settings file as a fresh settings service reads it.</summary>
    private static List<string> Saved() => new FileSettings().Get<StringCollection>(AvaloniaHotKeyService.SettingKey)?.Cast<string>().ToList() ?? [];

    /// <summary>The settings file as a new start reads it.</summary>
    private sealed class FileSettings : ISettingsService
    {
        private readonly UnifiedSettingsService _settings = new();

        public FileSettings() => _settings.Initialize(AppRole.Client);

        public void Set<T>(string key, T value) => _settings.Set(key, value);

        public T? Get<T>(string key) => _settings.Get<T>(key);

        public T Get<T>(string key, T defaultValue) => _settings.Get(key, defaultValue);

        public void Initialize(AppRole role) => _settings.Initialize(role);

        public SharedSettings GetShared() => _settings.GetShared();

        public ClientSettings GetClient() => _settings.GetClient();

        public ManagerSettings GetManager() => _settings.GetManager();

        public void SetApplicationVersion() => _settings.SetApplicationVersion();
    }

    private static async Task<string[]> WaitForCallsAsync(int from, Func<string[], bool> done)
    {
        string[] calls = [];
        await PumpUntilAsync(() => done(calls = AppEngine.Calls()[from..]), "the fake Game Host's call log");
        return calls;
    }
}
