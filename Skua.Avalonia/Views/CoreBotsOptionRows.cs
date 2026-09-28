using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// A row for each of CoreBots' options, as its type asks, bound both ways to the option's <c>Value</c>: a check box for a bool, a text box
/// (digits only for an int), a pair of check boxes for a two-way choice, and a list for a choice.
/// </summary>
/// <remarks>
/// Ports <c>OptionDataTemplateSelector</c>'s templates in <c>CBOptionsUserControl.xaml</c> and <c>CBOOptionItemUserControl.xaml</c>.
/// Their options are closed generic types that XAML's data templates can't name, so the rows are made here.
/// </remarks>
internal static class CoreBotsOptionRows
{
    /// <summary>Makes each option's row.</summary>
    public static IDataTemplate Template { get; } = new FuncDataTemplate<DisplayOptionItemViewModelBase>((option, _) => Row(option));

    private static global::Avalonia.Controls.Control Row(DisplayOptionItemViewModelBase option) => option switch
    {
        CBOBoolChoiceOptionItemViewModel choice => TwoWayChoice(choice),
        CBOChoiceOptionItemViewModel choice => Titled(choice, new ComboBox
        {
            ItemsSource = choice.Options,
            MinWidth = 160,
            [!SelectingItemsControl.SelectedIndexProperty] = ValueBinding(),
        }),
        CBOBoolOptionItemViewModel => Titled(option, OnOff()),
        _ when option.DisplayType == typeof(bool) => new CheckBox
        {
            Content = option.Content,
            Margin = new(0, 2),
            [ToolTip.TipProperty] = option.Content,
            [!ToggleButton.IsCheckedProperty] = ValueBinding(),
        },
        _ => Text(option),
    };

    /// <summary>The name, with the description as its tip, and the editor beside it, aligned with the other rows in a shared size scope.</summary>
    private static Grid Titled(DisplayOptionItemViewModelBase option, global::Avalonia.Controls.Control editor)
    {
        Grid row = new() { Margin = new(0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { SharedSizeGroup = "Title" });
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        TextBlock title = new() { Text = option.Content, Padding = new(18, 0), FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(title, option.Description);
        Grid.SetColumn(editor, 1);
        editor.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(title);
        row.Children.Add(editor);
        return row;
    }

    private static CheckBox OnOff()
    {
        CheckBox check = new() { MinWidth = 70, [!ToggleButton.IsCheckedProperty] = ValueBinding() };
        check.Bind(ContentControl.ContentProperty, new Binding(nameof(DisplayOptionItemViewModelBase.Value)) { Converter = OnOffText });
        return check;
    }

    private static global::Avalonia.Controls.Control TwoWayChoice(CBOBoolChoiceOptionItemViewModel choice)
    {
        StackPanel rows = new() { Margin = new(0, 3) };
        TextBlock title = new() { Text = choice.Content, Padding = new(18, 0), FontSize = 14 };
        ToolTip.SetTip(title, choice.Description);
        rows.Children.Add(title);
        StackPanel choices = new() { Margin = new(36, 3, 0, 3), Spacing = 3 };
        choices.Children.Add(new CheckBox { Content = choice.FirstChoice, [!ToggleButton.IsCheckedProperty] = ValueBinding() });
        choices.Children.Add(new CheckBox { Content = choice.SecondChoice, [!ToggleButton.IsCheckedProperty] = ValueBinding(Not) });
        rows.Children.Add(choices);
        return rows;
    }

    private static global::Avalonia.Controls.Control Text(DisplayOptionItemViewModelBase option)
    {
        TextBox text = new() { Width = 120, [!TextBox.TextProperty] = ValueBinding() };
        if (option.DisplayType == typeof(int))
            text.TextInput += (_, e) => e.Handled = e.Text is { } typed && !typed.All(char.IsAsciiDigit);
        DockPanel.SetDock(text, Dock.Right);
        DockPanel row = new() { Margin = new(4, 3) };
        ToolTip.SetTip(row, option.Description);
        row.Children.Add(text);
        row.Children.Add(new TextBlock { Text = option.Content, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static Binding ValueBinding(IValueConverter? converter = null) =>
        new(nameof(DisplayOptionItemViewModelBase.Value)) { Mode = BindingMode.TwoWay, Converter = converter };

    private static readonly IValueConverter OnOffText = new FuncValueConverter<object?, string>(value => value is true ? "On" : "Off");

    /// <summary>A bool's opposite, both ways.</summary>
    private static readonly IValueConverter Not = new NotConverter();

    private sealed class NotConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : null;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : null;
    }
}
