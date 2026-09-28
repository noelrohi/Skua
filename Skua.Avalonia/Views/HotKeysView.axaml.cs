using Avalonia.Controls;

namespace Skua.Avalonia.Views;

/// <summary>Each hotkey with its gesture; clicking one asks for a new gesture (<see cref="AssignHotKeyDialogView"/>) and saves it.</summary>
public partial class HotKeysView : UserControl
{
    public HotKeysView()
    {
        InitializeComponent();
    }
}
