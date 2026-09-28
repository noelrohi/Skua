using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Skua.Avalonia;

/// <summary>
/// The main window's login controls: a server picker, Log in with the Active Account and Log out, with what the last one did beside them.
/// </summary>
public sealed class LoginBar : Border
{
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

    private readonly StatusViewModel _model;
    private bool _serversLoaded;

    public LoginBar(StatusViewModel model)
    {
        _model = model;
        Padding = new Thickness(8, 4);
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));

        ServerPicker = new ComboBox { ItemsSource = model.Servers, SelectedItem = model.SelectedServer, MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        ServerPicker.SelectionChanged += (_, _) =>
        {
            // Relisting the servers clears the selection for a moment; the model keeps its pick.
            if (ServerPicker.SelectedItem is ServerChoice choice)
                _model.SelectedServer = choice;
        };
        // Listed once the bar shows, and again on opening the picker if that failed; the Engine checks the server afresh at each login.
        ServerPicker.DropDownOpened += (_, _) =>
        {
            if (_model.Servers.Count == 1)
                _model.LoadServersCommand.Execute(null);
        };
        LogInButton = new Button { Content = "Log in", Command = model.LogInCommand, VerticalAlignment = VerticalAlignment.Center };
        LogOutButton = new Button { Content = "Log out", Command = model.LogOutCommand, VerticalAlignment = VerticalAlignment.Center };
        MessageText = new SelectableTextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        DockPanel panel = new() { LastChildFill = true };
        StackPanel controls = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 12, 0) };
        controls.Children.Add(ServerPicker);
        controls.Children.Add(LogInButton);
        controls.Children.Add(LogOutButton);
        DockPanel.SetDock(controls, Dock.Left);
        panel.Children.Add(controls);
        panel.Children.Add(MessageText);
        Child = panel;
        Update();
    }

    public ComboBox ServerPicker { get; }

    public Button LogInButton { get; }

    public Button LogOutButton { get; }

    /// <summary>Who the last login logged in as, or why it failed.</summary>
    public SelectableTextBlock MessageText { get; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _model.PropertyChanged += OnModelChanged;
        Update();
        if (!_serversLoaded)
        {
            _serversLoaded = true;
            _model.LoadServersCommand.Execute(null);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        if (!ReferenceEquals(ServerPicker.SelectedItem, _model.SelectedServer))
            ServerPicker.SelectedItem = _model.SelectedServer;
        MessageText.Text = _model.Message;
        if (_model.MessageIsError)
            MessageText.Foreground = ErrorBrush;
        else
            MessageText.ClearValue(TextBlock.ForegroundProperty);
    }
}
