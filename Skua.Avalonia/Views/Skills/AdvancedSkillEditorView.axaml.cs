using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Skua.Avalonia.Views.Helpers;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views.Skills;

/// <summary>Builds a skill set: its skills in order, each with its use rules, then its class, mode and timeout; Save saves it.</summary>
/// <remarks>
/// Ports the WPF list's key bindings, with ⌘ for Ctrl: ⌘↑/⌘↓ move the selected skill, ⌫ removes it, ⌥⌫ removes them all, and Return edits
/// its use rules. Plain ↑/↓ select, as the list does itself.
/// </remarks>
public partial class AdvancedSkillEditorView : UserControl
{
    public AdvancedSkillEditorView()
    {
        InitializeComponent();
        ListInput.DigitsOnly(SkillTimeout);
        // WPF inverts the Wait for Cooldown binding; the inverse here only shows it, and a click sets it.
        UseIfAvailable.Click += (_, _) =>
        {
            if (Model is { } model)
                model.UseWaitModeBool = UseIfAvailable.IsChecked != true;
        };
        SkillsList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
    }

    private AdvancedSkillEditorViewModel? Model => DataContext as AdvancedSkillEditorViewModel;

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model)
            return;
        System.Windows.Input.ICommand? command = (e.Key, e.KeyModifiers) switch
        {
            (Key.Up, KeyModifiers.Meta) => model.MoveSkillUpCommand,
            (Key.Down, KeyModifiers.Meta) => model.MoveSkillDownCommand,
            (Key.Back or Key.Delete, KeyModifiers.Alt) => model.ClearSkillsCommand,
            (Key.Back or Key.Delete, KeyModifiers.None) => model.RemoveSkillCommand,
            (Key.Enter, KeyModifiers.None) => model.EditSkillCommand,
            _ => null,
        };
        if (command is null)
            return;
        command.Execute(null);
        e.Handled = true;
    }
}
