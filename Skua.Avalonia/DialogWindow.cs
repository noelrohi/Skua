using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Skua.Avalonia;

/// <summary>
/// A dialog showing one of Core's view models through its view, as <c>Skua.WPF</c>'s <c>HostDialog</c> does for <c>ShowDialog</c>: titled by
/// the view model's <c>Title</c>, sized to its content. Its view closes it with a result through <see cref="Close(global::Avalonia.Controls.Control, bool?)"/>.
/// </summary>
public sealed class DialogWindow : Window
{
    public DialogWindow(object viewModel, string? title = null)
    {
        DataTemplates.Add(new ViewLocator());
        DataContext = viewModel;
        Content = viewModel;
        Title = title ?? (viewModel.GetType().GetProperty("Title")?.GetValue(viewModel) as string) ?? "Skua";
        MinWidth = 400;
        MaxHeight = 800;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    /// <summary>What the view closed it with: true for its confirming button, null when closed any other way.</summary>
    public bool? Result { get; private set; }

    /// <summary>Closes the dialog showing <paramref name="view"/> with <paramref name="result"/>, as a WPF view sets its window's <c>DialogResult</c>.</summary>
    public static void Close(global::Avalonia.Controls.Control view, bool? result)
    {
        if (view.FindAncestorOfType<DialogWindow>(includeSelf: true) is { } dialog)
        {
            dialog.Result = result;
            dialog.Close(result);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Content = null;
        DataContext = null;
    }
}
