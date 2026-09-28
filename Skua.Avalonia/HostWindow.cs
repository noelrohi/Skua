using Avalonia.Controls;
using Skua.Core.Interfaces;

namespace Skua.Avalonia;

/// <summary>A window showing one of Core's view models through its view, sized and titled as a managed window asks.</summary>
public sealed class HostWindow : Window
{
    public HostWindow(object viewModel)
    {
        DataTemplates.Add(new ViewLocator());
        DataContext = viewModel;
        Content = viewModel;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (viewModel is IManagedWindow managed)
        {
            Title = managed.Title;
            // As Skua.WPF's HostWindow: a size of 0 sizes the window to its content that way, as HotKeys' height does.
            if (managed.Width > 0)
                Width = managed.Width;
            if (managed.Height > 0)
                Height = managed.Height;
            SizeToContent = (managed.Width > 0 ? SizeToContent.Manual : SizeToContent.Width) | (managed.Height > 0 ? SizeToContent.Manual : SizeToContent.Height);
            CanResize = managed.CanResize;
        }
        else
        {
            Title = "Skua";
        }
    }

    /// <summary>
    /// Lets go of the view model, as the WPF window service does, so a closed window's views stop listening to Core's collections: the view
    /// models live on, and a later show makes a new window.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Content = null;
        DataContext = null;
    }
}
