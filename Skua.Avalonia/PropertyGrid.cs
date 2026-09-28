using System.Collections;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Skua.Core.Utils;

namespace Skua.Avalonia;

/// <summary>
/// An object's properties, as <c>Skua.WPF/PropertyGrid.xaml</c> shows the Grabber's selected item: grouped by category, with decamelized names
/// and each description as a tooltip. Values are read-only and selectable; a nested object or a collection expands in place.
/// </summary>
/// <remarks>
/// A port of what the Grabber uses, not of the WPF grid's editors: the grabbed items are snapshots of the game, and editing one changes
/// nothing in it.
/// </remarks>
public sealed class PropertyGrid : UserControl
{
    public static readonly StyledProperty<object?> SelectedObjectProperty = AvaloniaProperty.Register<PropertyGrid, object?>(nameof(SelectedObject));

    /// <summary>How deep nested objects expand, so an object that refers back to itself stops.</summary>
    private const int MaxDepth = 4;

    private static readonly Decamelizer s_decamelizer = new();

    private readonly int _depth;

    public PropertyGrid()
        : this(0)
    {
    }

    private PropertyGrid(int depth)
    {
        _depth = depth;
    }

    static PropertyGrid()
    {
        SelectedObjectProperty.Changed.AddClassHandler<PropertyGrid>((grid, _) => grid.Build());
    }

    public object? SelectedObject
    {
        get => GetValue(SelectedObjectProperty);
        set => SetValue(SelectedObjectProperty, value);
    }

    /// <summary>The rows the grid shows for <paramref name="value"/>, in order: by category, then by name.</summary>
    public static IReadOnlyList<PropertyRow> Rows(object? value)
    {
        if (value is null || IsSimple(value.GetType()))
            return [];
        List<PropertyRow> rows = [];
        foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(value))
        {
            if (!descriptor.IsBrowsable || descriptor.PropertyType.IsPointer)
                continue;
            object? propertyValue;
            try
            {
                propertyValue = descriptor.GetValue(value);
            }
            catch (Exception e)
            {
                propertyValue = $"({(e.InnerException ?? e).Message})";
            }
            string category = string.IsNullOrWhiteSpace(descriptor.Category) ? CategoryAttribute.Default.Category : descriptor.Category;
            string name = descriptor.DisplayName == descriptor.Name ? s_decamelizer.Decamelize(descriptor.Name, null) : descriptor.DisplayName;
            rows.Add(new PropertyRow(category, name, descriptor.Description, propertyValue));
        }
        return [.. rows.OrderBy(r => r.Category, StringComparer.CurrentCulture).ThenBy(r => r.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>A value as the grid writes it: a collection as its count, anything else as its text.</summary>
    public static string Text(object? value) => value switch
    {
        null => "",
        string s => s,
        ICollection collection => $"({collection.Count} items)",
        IEnumerable => "(items)",
        _ => value.ToString() ?? "",
    };

    private void Build()
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        string? category = null;
        foreach (PropertyRow row in Rows(SelectedObject))
        {
            if (row.Category != category)
            {
                category = row.Category;
                Add(grid, new TextBlock { Text = category, FontWeight = FontWeight.Bold, Margin = new Thickness(0, grid.RowDefinitions.Count == 0 ? 0 : 6, 0, 2) }, 0, 2);
            }
            TextBlock name = new() { Text = row.Name, Margin = new Thickness(_depth == 0 ? 6 : 0, 1, 12, 1), VerticalAlignment = VerticalAlignment.Top };
            if (!string.IsNullOrEmpty(row.Description))
                ToolTip.SetTip(name, row.Description);
            Add(grid, name, 0, 1);
            global::Avalonia.Controls.Control value = Value(row.Value);
            Grid.SetRow(value, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }
        Content = _depth == 0 ? new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } : grid;
    }

    /// <summary>A value's control: its text, or for a nested object or a collection an expander that builds its contents when opened.</summary>
    private global::Avalonia.Controls.Control Value(object? value)
    {
        if (value is null || IsSimple(value.GetType()) || _depth >= MaxDepth || !(value is IEnumerable || Rows(value).Count > 0))
            return new SelectableTextBlock { Text = Text(value), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1) };

        Expander expander = new() { Header = Text(value), Padding = new Thickness(6, 3), HorizontalAlignment = HorizontalAlignment.Stretch };
        expander.Expanding += (_, _) => expander.Content ??= value is IEnumerable items ? Items(items) : new PropertyGrid(_depth + 1) { SelectedObject = value };
        return expander;
    }

    private StackPanel Items(IEnumerable items)
    {
        StackPanel panel = new() { Spacing = 2 };
        foreach (object? item in items)
            panel.Children.Add(Value(item));
        return panel;
    }

    /// <summary>Adds a new row to <paramref name="grid"/> holding <paramref name="control"/>.</summary>
    private static void Add(Grid grid, global::Avalonia.Controls.Control control, int column, int span)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(control, grid.RowDefinitions.Count - 1);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, span);
        grid.Children.Add(control);
    }

    private static bool IsSimple(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
        || type == typeof(TimeSpan) || type == typeof(Guid) || Nullable.GetUnderlyingType(type) is { } underlying && IsSimple(underlying);
}

/// <summary>One of <see cref="PropertyGrid"/>'s rows.</summary>
public sealed record PropertyRow(string Category, string Name, string Description, object? Value);
