using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace Skua.Avalonia;

/// <summary>
/// The Notices Scripts showed: a button with a badge counting the unread ones, whose flyout lists them newest first. Opening one shows its
/// full text, which can be selected and copied. Nothing here ever waits on a Script, nor a Script on it.
/// </summary>
public sealed class NoticesButton : Button
{
    private readonly ScriptDialogsViewModel _model;

    public NoticesButton(ScriptDialogsViewModel model)
    {
        _model = model;
        Padding = new Thickness(8, 0);
        MinHeight = 0;
        FontSize = 12;
        VerticalAlignment = VerticalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        Background = Brushes.Transparent;

        Badge = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
        BadgeBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5, 0),
            MinWidth = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Badge,
        };
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children = { new TextBlock { Text = "Notices", VerticalAlignment = VerticalAlignment.Center }, BadgeBorder },
        };

        List = new ListBox
        {
            ItemsSource = model.Notices,
            MaxHeight = 360,
            Width = 420,
            // A row being recycled, as at Clear, has its content cleared before its template, so the template is built once more with null (#137).
            ItemTemplate = new FuncDataTemplate<ShownNotice>((notice, _) => notice is null ? null : Row(notice), supportsRecycling: false),
        };
        List.DoubleTapped += (_, _) => OpenSelected();
        OpenButton = new Button { Content = "Open" };
        OpenButton.Click += (_, _) => OpenSelected();
        ClearButton = new Button { Content = "Clear" };
        ClearButton.Click += (_, _) => _model.ClearNotices();
        Empty = new TextBlock { Text = "No Notices yet.", Opacity = 0.7, Margin = new Thickness(4, 8) };
        Flyout = new Flyout
        {
            Placement = PlacementMode.TopEdgeAlignedRight,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Empty,
                    List,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { ClearButton, OpenButton },
                    },
                },
            },
        };
        // Seeing the list reads them.
        Flyout.Opened += (_, _) => _model.MarkNoticesRead();
        Update();
    }

    public TextBlock Badge { get; }

    public Border BadgeBorder { get; }

    public ListBox List { get; }

    /// <summary>Opens the selected Notice's full text.</summary>
    public Button OpenButton { get; }

    public Button ClearButton { get; }

    private TextBlock Empty { get; }

    /// <summary>Called on the UI thread with each Notice window opened, e.g. to give it the app's menu.</summary>
    public Action<Window>? WindowOpened { get; set; }

    /// <summary>Opens a window with the Notice's full text, which can be selected and copied.</summary>
    public Window Open(ShownNotice notice)
    {
        Window window = NoticeWindow.Create(notice);
        WindowOpened?.Invoke(window);
        window.Show();
        return window;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _model.PropertyChanged += OnModelChanged;
        _model.Notices.CollectionChanged += OnNoticesChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _model.PropertyChanged -= OnModelChanged;
        _model.Notices.CollectionChanged -= OnNoticesChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScriptDialogsViewModel.Unread))
            Update();
    }

    private void OnNoticesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Update();

    private void Update()
    {
        Badge.Text = _model.Unread > 99 ? "99+" : _model.Unread.ToString();
        BadgeBorder.IsVisible = _model.Unread > 0;
        bool any = _model.Notices.Count > 0;
        Empty.IsVisible = !any;
        List.IsVisible = any;
        ClearButton.IsEnabled = any;
        OpenButton.IsEnabled = any;
        ToolTip.SetTip(this, _model.Unread > 0 ? $"{_model.Unread} unread Notices from Scripts" : "Notices from Scripts");
    }

    private void OpenSelected()
    {
        if ((List.SelectedItem ?? _model.Notices.FirstOrDefault()) is ShownNotice notice)
            Open(notice);
    }

    private static global::Avalonia.Controls.Control Row(ShownNotice notice) => new StackPanel
    {
        Spacing = 1,
        Children =
        {
            new TextBlock
            {
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = $"{notice.ShownAt:HH:mm:ss}  {(notice.Caption.Length > 0 ? notice.Caption : "Notice")}",
            },
            new TextBlock { FontSize = 12, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis, Text = notice.Summary },
        },
    };
}

/// <summary>A window with a Notice's full text, selectable, and a button that copies it.</summary>
public static class NoticeWindow
{
    public static Window Create(ShownNotice notice)
    {
        Window window = new()
        {
            Title = notice.Caption.Length > 0 ? notice.Caption : "Notice",
            Width = 520,
            Height = 360,
            MinWidth = 300,
            MinHeight = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        SelectableTextBlock text = new() { Name = "Text", Text = notice.Text, TextWrapping = TextWrapping.Wrap };
        TextBlock copied = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        Button copy = new() { Name = "Copy", Content = "Copy" };
        copy.Click += async (_, _) =>
        {
            if (window.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(notice.Text);
                copied.Text = "Copied.";
            }
        };
        Button close = new() { Content = "Close", IsDefault = true, IsCancel = true };
        close.Click += (_, _) => window.Close();

        DockPanel footer = new() { Margin = new Thickness(0, 12, 0, 0) };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, close } };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(copied);
        TextBlock source = new()
        {
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 8),
            Text = $"{notice.ShownAt:yyyy-MM-dd HH:mm:ss}" + (notice.Script is { } script ? $" · {script}" : ""),
        };
        DockPanel content = new() { Margin = new Thickness(16) };
        DockPanel.SetDock(source, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(source);
        content.Children.Add(footer);
        content.Children.Add(new ScrollViewer { Content = text });
        window.Content = content;
        return window;
    }
}
