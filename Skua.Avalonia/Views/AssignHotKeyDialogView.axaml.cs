using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// Asks for a hotkey's gesture, as <c>AssignHotKeyDialog</c> does on Windows: clicking the key button waits for the next key, which it
/// takes with the ⌘, ⌥ and ⇧ held with it; the check boxes add or remove those. Save closes the dialog with true.
/// </summary>
/// <remarks>
/// While it waits, it takes every key press in its window before anything else does, so a key that is already a hotkey is captured
/// rather than run. Esc stops waiting and keeps the key it had.
/// </remarks>
public partial class AssignHotKeyDialogView : UserControl
{
    public const string WaitingInputText = "Waiting input...";
    public const string CaptureHintText = "Press a key, with ⌘, ⌥ or ⇧ if you like (Esc to cancel).";
    public const string ModifierOnlyHintText = "Modifier keys cannot be used alone. Press another key.";
    public const string ControlHintText = "⌃ Control can't be part of a hotkey. Use ⌘ or ⌥.";
    public const string SaveWithoutKeyHintText = "Press a non-modifier key before saving.";

    /// <summary>The key button's text: the key as the developer knows it (<c>0</c> for <c>D0</c>), or the waiting text.</summary>
    public static readonly FuncValueConverter<string?, string> KeyShown = new(key => HotKeyGestures.Display(key));

    private TopLevel? _topLevel;
    private string _backupKey = string.Empty;

    public AssignHotKeyDialogView()
    {
        InitializeComponent();
        Capture.Click += (_, _) => StartCapture();
        Save.Click += (_, _) => OnSave();
        Cancel.Click += (_, _) => DialogWindow.Close(this, false);
    }

    /// <summary>Whether the next key press becomes the hotkey's key.</summary>
    public bool IsCapturing { get; private set; }

    private AssignHotKeyDialogViewModel? Model => DataContext as AssignHotKeyDialogViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _topLevel?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _topLevel = null;
    }

    private void StartCapture()
    {
        if (Model is not { } model)
            return;
        if (!IsCapturing)
            _backupKey = model.KeyInput;
        IsCapturing = true;
        model.KeyInput = WaitingInputText;
        model.InputHint = CaptureHintText;
    }

    private void StopCapture(string key, string hint)
    {
        IsCapturing = false;
        if (Model is { } model)
        {
            model.KeyInput = key;
            model.InputHint = hint;
        }
    }

    private void OnSave()
    {
        if (Model is not { } model)
            return;
        if (IsCapturing)
        {
            StopCapture(_backupKey, SaveWithoutKeyHintText);
            return;
        }
        // Modifiers without a key make a gesture that binds nothing; no key and no modifiers clears the hotkey, as on Windows.
        if (string.IsNullOrWhiteSpace(model.KeyInput) ? model.CtrlCheck || model.AltCheck || model.ShiftCheck
            : Enum.TryParse(model.KeyInput, out Key key) && HotKeyGestures.IsModifier(key))
        {
            model.InputHint = SaveWithoutKeyHintText;
            return;
        }
        model.InputHint = string.Empty;
        DialogWindow.Close(this, true);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsCapturing || Model is not { } model)
            return;
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            StopCapture(_backupKey, string.Empty);
            return;
        }
        if (HotKeyGestures.IsModifier(e.Key))
        {
            model.InputHint = ModifierOnlyHintText;
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            model.InputHint = ControlHintText;
            return;
        }
        // The modifiers held with it join the ones checked.
        model.CtrlCheck |= e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        model.AltCheck |= e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        model.ShiftCheck |= e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        StopCapture(e.Key.ToString(), string.Empty);
    }
}
