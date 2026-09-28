using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// One of Core's display options, edited as its type asks: a check box, a number or text with a button that sets it, or a button that acts.
/// Ported from <c>Skua.WPF/UserControls/OptionItemUserControl.xaml</c> and <c>OptionDataTemplateSelector</c>, made in code.
/// </summary>
/// <remarks>
/// The editor follows the option's value while it shows, as the WPF bindings do, so a Script's change to a game option shows at once. Each
/// edit runs through the panel's <c>edit</c>, which the Game Options panel uses to save the options it changes.
/// </remarks>
public sealed class OptionItemView : UserControl
{
    private readonly DisplayOptionItemViewModelBase _item;
    private readonly Action<Action> _edit;
    private CheckBox? _check;
    private TextBox? _text;
    private bool _showing;

    public OptionItemView(DisplayOptionItemViewModelBase item, Action<Action>? edit = null)
    {
        _item = item;
        _edit = edit ?? (action => action());
        IRelayCommand? command = (item as CommandOptionItemViewModel)?.Command;
        ToolTip.SetTip(this, item.Description);
        Margin = new Thickness(3);

        if (item.DisplayType == typeof(bool))
        {
            _check = new CheckBox { Content = item.Content, IsThreeState = false };
            _check.IsCheckedChanged += (_, _) =>
            {
                if (_showing)
                    return;
                _edit(() =>
                {
                    bool value = _check.IsChecked == true;
                    _item.Value = value;
                    command?.Execute(value);
                });
            };
            Content = _check;
        }
        else if (item.DisplayType == typeof(int) || item.DisplayType == typeof(string))
        {
            bool isNumber = item.DisplayType == typeof(int);
            _text = new TextBox { Width = isNumber ? 70 : double.NaN, VerticalContentAlignment = VerticalAlignment.Center };
            if (!isNumber)
                _text.PlaceholderText = item.Content;
            _text.TextChanged += (_, _) =>
            {
                if (!Equals(_item.Value?.ToString(), _text.Text))
                    _item.Value = _text.Text;
            };
            Button set = new() { Content = isNumber ? item.Content : "Set", VerticalAlignment = VerticalAlignment.Stretch };
            set.Click += (_, _) => _edit(() => command?.Execute(_text.Text ?? ""));

            DockPanel row = new() { LastChildFill = true };
            DockPanel.SetDock(set, isNumber ? Dock.Left : Dock.Right);
            if (isNumber)
            {
                DockPanel.SetDock(_text, Dock.Left);
                row.Children.Add(_text);
                if (item.SuffixText is { } suffix)
                {
                    TextBlock suffixText = new() { Text = suffix, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                    DockPanel.SetDock(suffixText, Dock.Left);
                    row.Children.Add(suffixText);
                }
                set.HorizontalAlignment = HorizontalAlignment.Stretch;
                set.Margin = new Thickness(item.SuffixText is null ? 3 : 0, 0, 0, 0);
                row.Children.Add(set);
            }
            else
            {
                set.Margin = new Thickness(3, 0, 0, 0);
                row.Children.Add(set);
                row.Children.Add(_text);
            }
            Content = row;
        }
        else if (item.DisplayType == typeof(IRelayCommand))
        {
            Button act = new() { Content = item.Content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            act.Click += (_, _) => _edit(() => command?.Execute(null));
            Content = act;
        }
        else
        {
            // WPF has no editor for other types either (its enum template is unset).
            Content = new TextBlock { Text = item.Content, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
        }
    }

    /// <summary>The option this row edits.</summary>
    public DisplayOptionItemViewModelBase Item => _item;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _item.PropertyChanged += OnItemChanged;
        ShowValue();
    }

    /// <summary>Stops following the option, which lives on after the window closes.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _item.PropertyChanged -= OnItemChanged;
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DisplayOptionItemViewModelBase.Value))
            UiThread.Post(ShowValue);
    }

    private void ShowValue()
    {
        _showing = true;
        try
        {
            if (_check is not null)
                _check.IsChecked = _item.Value is true;
        }
        finally
        {
            _showing = false;
        }
        if (_text is not null && !Equals(_text.Text, _item.Value?.ToString()))
            _text.Text = _item.Value?.ToString() ?? "";
    }
}
