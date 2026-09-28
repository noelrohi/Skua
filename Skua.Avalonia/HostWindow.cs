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
            Width = managed.Width;
            Height = managed.Height;
            CanResize = managed.CanResize;
        }
        else
        {
            Title = "Skua";
        }
    }
}
