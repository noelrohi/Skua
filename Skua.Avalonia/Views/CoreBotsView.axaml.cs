using Avalonia.Controls;

namespace Skua.Avalonia.Views;

/// <summary>CoreBots' options for the logged-in player: Load reads them, Save and closing the window write them where CoreBots reads them.</summary>
public partial class CoreBotsView : UserControl
{
    public CoreBotsView()
    {
        InitializeComponent();
    }
}
