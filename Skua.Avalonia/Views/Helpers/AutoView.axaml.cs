using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views.Helpers;

/// <summary>Opening the class picker lists the player's classes afresh, as on Windows.</summary>
public partial class AutoView : UserControl
{
    public AutoView()
    {
        InitializeComponent();
        ClassPicker.DropDownOpened += (_, _) => (DataContext as AutoViewModel)?.ReloadClassesCommand.Execute(null);
    }
}
