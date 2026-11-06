using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>The application options, each saved in the settings by its own command as on Windows.</summary>
/// <remarks>Core's options with no macOS meaning are left out; Core's list, and so Windows, keeps them.</remarks>
public partial class ApplicationOptionsView : UserControl
{
    /// <summary>
    /// The tags of the options hidden here: Clear Flash Cache, as the Game Host runs Ruffle and has no Flash cache, and the client
    /// animation frame-rate and Square Window Corners, which only WPF reads (macOS draws the window's corners itself).
    /// </summary>
    private static readonly IReadOnlySet<string> HiddenOnMac = new HashSet<string> { "Clear Flash Cache", "ClientAnim", "SquareWindowCorners" };

    public ApplicationOptionsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Options.Children.Clear();
        if (DataContext is ApplicationOptionsViewModel model)
        {
            foreach (DisplayOptionItemViewModelBase option in model.ApplicationOptions.Where(o => !HiddenOnMac.Contains(o.Tag)))
                Options.Children.Add(new OptionItemView(option));
        }
    }
}
