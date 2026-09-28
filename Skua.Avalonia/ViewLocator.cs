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

    /// <summary>
    /// The views of the dialogs Core and Scripts make their own view models for and show with <c>IDialogService.ShowDialog</c>; none of these
    /// view models comes from the container.
    /// </summary>
    private static readonly Dictionary<Type, Func<global::Avalonia.Controls.Control>> s_dialogs = new()
    {
        [typeof(InputDialogViewModel)] = () => new InputDialogView(),
        [typeof(OptionContainerViewModel)] = () => new OptionContainerView(),
    };

    /// <summary>The view models that have a view, other than the dialogs'.</summary>
    public static IReadOnlyCollection<Type> ViewModelTypes => s_views.Keys;

    /// <summary>The dialogs' view models that have a view.</summary>
    public static IReadOnlyCollection<Type> DialogViewModelTypes => s_dialogs.Keys;

    public static bool HasView(object viewModel) => View(viewModel.GetType()) is not null;

    public global::Avalonia.Controls.Control? Build(object? param) => param is not null && View(param.GetType()) is { } view ? view() : null;

    public bool Match(object? data) => data is not null && HasView(data);

    private static Func<global::Avalonia.Controls.Control>? View(Type type) => s_views.GetValueOrDefault(type) ?? s_dialogs.GetValueOrDefault(type);
}
