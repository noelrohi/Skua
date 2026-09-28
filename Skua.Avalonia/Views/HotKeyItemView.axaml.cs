using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Skua.Avalonia.Views;

/// <summary>A hotkey's title and its gesture as a Mac shows one (⌘0); clicking the gesture edits it.</summary>
public partial class HotKeyItemView : UserControl
{
    /// <summary>The gesture as the button shows it, e.g. <c>⌥⌘B</c> for <c>Ctrl+Alt+B</c>.</summary>
    public static readonly FuncValueConverter<string?, string> Shown =
        new(gesture => string.IsNullOrWhiteSpace(gesture) ? "Not set" : HotKeyGestures.Display(gesture));

    public HotKeyItemView()
    {
        InitializeComponent();
    }
}
