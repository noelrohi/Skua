using Avalonia.Controls;

namespace Skua.Avalonia.Views.Skills;

/// <summary>Edits a copy of a skill's use rules; Confirm closes the dialog with true, which gives the skill the copy.</summary>
public partial class SkillRuleEditorDialogView : UserControl
{
    public SkillRuleEditorDialogView()
    {
        InitializeComponent();
        Confirm.Click += (_, _) => DialogWindow.Close(this, true);
        Cancel.Click += (_, _) => DialogWindow.Close(this, false);
    }
}
