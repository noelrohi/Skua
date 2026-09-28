using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>The application options, each saved in the settings by its own command as on Windows.</summary>
public partial class ApplicationOptionsView : UserControl
{
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
            foreach (DisplayOptionItemViewModelBase option in model.ApplicationOptions)
                Options.Children.Add(new OptionItemView(option));
        }
    }
}
