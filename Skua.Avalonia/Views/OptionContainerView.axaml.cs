using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// A Script's options, grouped by category, each edited as its type asks: a check box, a list of an enum's names, or text. Closing the dialog
/// saves them, as on Windows; Done closes it.
/// </summary>
/// <remarks>Ports the WPF DataGrid's grouping and <c>OptionContainerDataTemplateSelector</c> as rows made in code.</remarks>
public partial class OptionContainerView : UserControl
{
    /// <summary>The category of an option that names none (<c>Option.Category</c>'s default).</summary>
    private const string DefaultCategory = "Options";

    public OptionContainerView()
    {
        InitializeComponent();
        Done.Click += (_, _) => DialogWindow.Close(this, true);
    }

    private OptionContainerViewModel? _model;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Groups.Children.Clear();
        if (_model is not null)
            _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as OptionContainerViewModel;
        ShowSelected();
        if (_model is not { } model)
            return;
        model.PropertyChanged += OnModelChanged;
        // Each category is an expander, closed as in the WPF view, except the options without a category of their own, and a lone category.
        List<IGrouping<string, OptionContainerItemViewModel>> groups = model.Options.GroupBy(o => o.Category ?? "").ToList();
        foreach (IGrouping<string, OptionContainerItemViewModel> group in groups)
        {
            StackPanel rows = new() { Spacing = 4 };
            foreach (OptionContainerItemViewModel item in group)
                rows.Children.Add(Row(model, item));
            Groups.Children.Add(new Expander
            {
                Header = group.Key.Length > 0 ? group.Key : "Options",
                IsExpanded = group.Key is "" or DefaultCategory || groups.Count == 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = rows,
            });
        }
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OptionContainerViewModel.SelectedOption))
            ShowSelected();
    }

    /// <summary>The selected option's name and description; bound in code, as nothing is selected at first.</summary>
    private void ShowSelected()
    {
        SelectedName.Text = _model?.SelectedOption?.Option.DisplayName;
        SelectedDescription.Text = _model?.SelectedOption?.Option.Description;
    }

    private static global::Avalonia.Controls.Control Row(OptionContainerViewModel model, OptionContainerItemViewModel item)
    {
        global::Avalonia.Controls.Control editor;
        if (item.Type == typeof(bool))
        {
            CheckBox check = new() { IsChecked = item.Value is true, IsThreeState = false };
            check.IsCheckedChanged += (_, _) => item.Value = check.IsChecked == true;
            editor = check;
        }
        else if (item.Type.IsEnum)
        {
            ComboBox list = new() { ItemsSource = item.EnumValues, SelectedItem = item.SelectedValue, HorizontalAlignment = HorizontalAlignment.Stretch };
            list.SelectionChanged += (_, _) => item.SelectedValue = list.SelectedItem as string;
            editor = list;
        }
        else
        {
            TextBox text = new() { Text = item.Value?.ToString() ?? "" };
            text.TextChanged += (_, _) => item.Value = text.Text ?? "";
            editor = text;
        }
        editor.Width = 190;
        editor.GotFocus += (_, _) => model.SelectedOption = item;

        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = global::Avalonia.Media.Brushes.Transparent, Tag = item };
        TextBlock name = new() { Text = item.Option.DisplayName, VerticalAlignment = VerticalAlignment.Center, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(editor, 1);
        row.Children.Add(name);
        row.Children.Add(editor);
        row.PointerPressed += (_, _) => model.SelectedOption = item;
        return row;
    }
}
