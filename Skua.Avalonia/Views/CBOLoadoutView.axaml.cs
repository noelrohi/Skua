using Avalonia.Controls;
using Avalonia.LogicalTree;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// The classes and equipment CoreBots uses. As on Windows, opening a class list reloads the player's classes and keeps the ones chosen and their modes, and
/// opening an equipment list reloads the inventory.
/// </summary>
public partial class CBOLoadoutView : UserControl
{
    public CBOLoadoutView()
    {
        InitializeComponent();
        foreach (ComboBox classes in new[] { SoloClass, FarmClass, DodgeClass, BossClass })
            classes.DropDownOpened += (_, _) => ReloadClasses();
        foreach (ComboBox equipment in Equipment.GetLogicalDescendants().OfType<ComboBox>())
            equipment.DropDownOpened += (_, _) => (Equipment.DataContext as CBOClassEquipmentViewModel)?.RefreshInventoryCommand.Execute(null);
    }

    private void ReloadClasses() => (ClassSelect.DataContext as CBOClassSelectViewModel)?.RefreshClassesKeepSelection();
}
