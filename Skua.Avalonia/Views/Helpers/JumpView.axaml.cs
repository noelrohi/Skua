using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views.Helpers;

/// <summary>Opening the cell picker lists the map's cells afresh, as on Windows.</summary>
public partial class JumpView : UserControl
{
    public JumpView()
    {
        InitializeComponent();
        Cells.DropDownOpened += (_, _) => (DataContext as JumpViewModel)?.UpdateCellsCommand.Execute(null);
    }
}
