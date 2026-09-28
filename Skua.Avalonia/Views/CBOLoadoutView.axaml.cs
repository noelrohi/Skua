using Avalonia.Controls;
using Avalonia.LogicalTree;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// The classes and equipment CoreBots uses. As on Windows, opening a class list reloads the player's classes and keeps the ones chosen, and
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

    private void ReloadClasses()
    {
        if (ClassSelect.DataContext is not CBOClassSelectViewModel model)
            return;
        (string? solo, string? farm, string? dodge, string? boss) = (model.SelectedSoloClass, model.SelectedFarmClass, model.SelectedDodgeClass, model.SelectedBossClass);
        model.ReloadClassesCommand.Execute(null);
        (model.SelectedSoloClass, model.SelectedFarmClass, model.SelectedDodgeClass, model.SelectedBossClass) = (solo, farm, dodge, boss);
    }
}
