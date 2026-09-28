using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Skua.Avalonia;

/// <summary>The take-over offer: what the Engine holding the name is doing, with Take over and Quit, or Try again when it can't be taken over.</summary>
public sealed class TakeOverView : Border
{
    private readonly TakeOverViewModel _model;

    public TakeOverView(TakeOverViewModel model)
    {
        _model = model;
        Padding = new Thickness(20);

        MessageText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        DetailsText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(12, 0, 0, 0) };
        TakeOverButton = new Button { Command = model.TakeOverCommand, IsDefault = true };
        TryAgainButton = new Button { Content = "Try again", Command = model.LookCommand };
        QuitButton = new Button { Content = "Quit", Command = model.QuitCommand, IsCancel = true };

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(QuitButton);
        buttons.Children.Add(TryAgainButton);
        buttons.Children.Add(TakeOverButton);
        Child = new StackPanel { Spacing = 16, Children = { MessageText, DetailsText, buttons } };
        Update();
    }

    public SelectableTextBlock MessageText { get; }

    /// <summary>The Engine's account, map and Script.</summary>
    public SelectableTextBlock DetailsText { get; }

    public Button TakeOverButton { get; }

    public Button TryAgainButton { get; }

    public Button QuitButton { get; }

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
        MessageText.Text = _model.Message;
        DetailsText.Text = _model.Details;
        DetailsText.IsVisible = _model.Details.Length > 0;
        TakeOverButton.Content = _model.TakeOverText;
        TakeOverButton.IsVisible = _model.Step is TakeOverStep.Offer or TakeOverStep.Confirm or TakeOverStep.TakingOver;
        TryAgainButton.IsVisible = _model.Step == TakeOverStep.Stuck;
    }
}
