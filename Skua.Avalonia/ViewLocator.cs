using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Skua.Avalonia.Manager;
using Skua.Avalonia.Views;
using Skua.Avalonia.Views.Helpers;
using Skua.Avalonia.Views.Manager;
using Skua.Core.ViewModels;
using Skua.Core.ViewModels.Manager;

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
        [typeof(CoreBotsViewModel)] = () => new CoreBotsView(),
        [typeof(CBOLoadoutViewModel)] = () => new CBOLoadoutView(),
        [typeof(CBOptionsViewModel)] = () => new CBOptionsView(),
        [typeof(CBOOtherOptionsViewModel)] = () => new CBOOtherOptionsView(),
        [typeof(RuntimeHelpersViewModel)] = () => new RuntimeHelpersView(),
        [typeof(NotifyDropViewModel)] = () => new NotifyDropView(),
        [typeof(FastTravelViewModel)] = () => new FastTravelView(),
        [typeof(CurrentDropsViewModel)] = () => new CurrentDropsView(),
        // The main menu's Auto and Jump, which the window shows below their buttons.
        [typeof(AutoViewModel)] = () => new AutoView(),
        [typeof(JumpViewModel)] = () => new JumpView(),
        // The Skua Manager's, which runs as its own process (ADR 0006).
        [typeof(ManagerMainViewModel)] = () => new ManagerView(),
        [typeof(ManagerAccountsViewModel)] = () => new AccountsView(),
        [typeof(RunningViewModel)] = () => new RunningView(),
        [typeof(UpdatesViewModel)] = () => new UpdatesView(),
        [typeof(GoalsViewModel)] = () => new GoalsView(),
        [typeof(GameOptionsViewModel)] = () => new GameOptionsView(),
        [typeof(ApplicationOptionsViewModel)] = () => new ApplicationOptionsView(),
        [typeof(ApplicationThemesViewModel)] = () => new ApplicationThemesView(),
        [typeof(HotKeysViewModel)] = () => new HotKeysView(),
        [typeof(HotKeyItemViewModel)] = () => new HotKeyItemView(),
    };

    /// <summary>
    /// The views of the dialogs Core and Scripts make their own view models for and show with <c>IDialogService.ShowDialog</c>; none of these
    /// view models comes from the container.
    /// </summary>
    private static readonly Dictionary<Type, Func<global::Avalonia.Controls.Control>> s_dialogs = new()
    {
        [typeof(InputDialogViewModel)] = () => new InputDialogView(),
        [typeof(OptionContainerViewModel)] = () => new OptionContainerView(),
        [typeof(FastTravelEditorDialogViewModel)] = () => new FastTravelEditorDialogView(),
        [typeof(AssignHotKeyDialogViewModel)] = () => new AssignHotKeyDialogView(),
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
