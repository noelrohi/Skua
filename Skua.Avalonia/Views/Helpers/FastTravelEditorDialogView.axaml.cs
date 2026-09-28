using Avalonia.Controls;

namespace Skua.Avalonia.Views.Helpers;

/// <summary>Edits a copy of a saved travel; Confirm closes the dialog with true, which replaces the travel with the copy.</summary>
public partial class FastTravelEditorDialogView : UserControl
{
    public FastTravelEditorDialogView()
    {
        InitializeComponent();
        Confirm.Click += (_, _) => DialogWindow.Close(this, true);
        Cancel.Click += (_, _) => DialogWindow.Close(this, false);
    }
}
