using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Skua.Avalonia.Views.Helpers;

/// <summary>The input behaviours the WPF helper views share, ported: removing a list's items by key, and number-only text boxes.</summary>
internal static class ListInput
{
    /// <summary>A list's selected items as Core's commands take them (<c>IList&lt;object&gt;</c>).</summary>
    public static List<object> Selected(ListBox list) => list.SelectedItems?.Cast<object>().ToList() ?? [];

    /// <summary>
    /// Ports the WPF lists' Delete and Alt+Delete key bindings: Delete (⌫ on a Mac keyboard, or ⌦) removes the selected items, and with ⌥
    /// removes them all.
    /// </summary>
    public static void RemoveOnDelete(ListBox list, Func<ICommand?> removeSelected, Func<ICommand?> removeAll)
    {
        list.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Back or Key.Delete))
                return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                removeAll()?.Execute(null);
            else
                removeSelected()?.Execute(Selected(list));
            e.Handled = true;
        };
    }

    /// <summary>Ports <c>TextBoxOnlyNumbersBehavior</c>: the text box takes digits alone.</summary>
    public static void DigitsOnly(TextBox textBox) =>
        textBox.AddHandler(InputElement.TextInputEvent, (_, e) =>
        {
            if (e.Text is { } text && !text.All(char.IsAsciiDigit))
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
}
