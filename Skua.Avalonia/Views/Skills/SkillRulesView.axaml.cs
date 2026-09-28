using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Skua.Avalonia.Views.Helpers;

namespace Skua.Avalonia.Views.Skills;

/// <summary>A skill's use rules: health, mana, party health, one aura or several, skip and wait.</summary>
/// <remarks>Ports <c>TextBoxOnlyNumbersBehavior</c> and <c>TextBoxOnlyFloatingPointBehavior</c>: the stacks, the aura checks' too, take a decimal.</remarks>
public partial class SkillRulesView : UserControl
{
    public SkillRulesView()
    {
        InitializeComponent();
        foreach (TextBox box in new[] { Health, Mana, PartyHealth, Wait })
            ListInput.DigitsOnly(box);
        AddHandler(TextInputEvent, OnStacksInput, RoutingStrategies.Tunnel);
    }

    private void OnStacksInput(object? sender, TextInputEventArgs e)
    {
        if (e.Source is TextBox box && (box == AuraStacks || box.Classes.Contains("stacks"))
            && e.Text is { } text && !text.All(c => char.IsAsciiDigit(c) || (c == '.' && !(box.Text ?? string.Empty).Contains('.'))))
            e.Handled = true;
    }
}
