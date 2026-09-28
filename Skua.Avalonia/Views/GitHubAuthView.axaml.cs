using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Skua.Avalonia.Views;

/// <summary>
/// GitHub's device-flow sign-in: Get User Code copies the code, Open Browser opens GitHub's page for it, and Authorize fetches the token,
/// which the app keeps in Keychain. Close closes the window.
/// </summary>
public partial class GitHubAuthView : UserControl
{
    public GitHubAuthView()
    {
        InitializeComponent();
        Close.Click += (_, _) => this.FindAncestorOfType<Window>()?.Close();
    }
}
