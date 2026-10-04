using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Skua.Avalonia;

/// <summary>What the Skua Manager's launch did, in the main window: who it logged in as and the Script it started, or why it failed.</summary>
public sealed class LaunchMessage : SelectableTextBlock
{
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

    private readonly StatusViewModel _model;

    public LaunchMessage(StatusViewModel model)
    {
        _model = model;
        Margin = new Thickness(8, 0, 12, 0);
        VerticalAlignment = VerticalAlignment.Center;
        TextTrimming = TextTrimming.CharacterEllipsis;
        FontSize = 12;
        Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _model.PropertyChanged += OnModelChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        Text = _model.Message;
        if (_model.MessageIsError)
            Foreground = ErrorBrush;
        else
            ClearValue(ForegroundProperty);
    }
}
