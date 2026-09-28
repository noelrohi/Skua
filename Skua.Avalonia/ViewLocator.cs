using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Skua.Avalonia.Views;
using Skua.Core.ViewModels;

namespace Skua.Avalonia;

/// <summary>
/// The view for each of Core's view models, one per view model as in <c>Skua.WPF/XAML/DataTemplates.xaml</c>. A view model without an
/// entry has no view yet: its menu item is disabled and its window isn't shown.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<global::Avalonia.Controls.Control>> s_views = new()
    {
        [typeof(ScriptLoaderViewModel)] = () => new ScriptLoaderView(),
        [typeof(ScriptRepoViewModel)] = () => new ScriptRepoView(),
        [typeof(LogsViewModel)] = () => new LogsView(),
        [typeof(LogTabViewModel)] = () => new LogTabView(),
    };

    /// <summary>The view models that have a view.</summary>
    public static IReadOnlyCollection<Type> ViewModelTypes => s_views.Keys;

    public static bool HasView(object viewModel) => s_views.ContainsKey(viewModel.GetType());

    public global::Avalonia.Controls.Control? Build(object? param) => param is not null && s_views.TryGetValue(param.GetType(), out Func<global::Avalonia.Controls.Control>? view) ? view() : null;

    public bool Match(object? data) => data is not null && HasView(data);
}
