using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Skua.Avalonia.Manager;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views;
using Skua.Avalonia.Views.Manager;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;
using Skua.Core.ViewModels.Manager;

namespace Skua.Avalonia.Tests;

/// <summary>
/// Parity with the Windows app (#94, <c>docs/plans/mac-app-parity.md</c>): every one of Core's view models that the Windows XAML shows has
/// an Avalonia view, or is listed here as a deliberate difference, with its reason, or as a known gap, with its issue.
/// </summary>
/// <remarks>
/// A view model has a WPF view when <c>Skua.WPF</c>, <c>Skua.App.WPF</c> or <c>Skua.Manager</c> XAML names it as a data template's
/// <c>{x:Type}</c> or a view's <c>{d:DesignInstance}</c>. It has an Avalonia view when <see cref="ViewLocator"/> maps it, when a
/// <c>Skua.Avalonia</c> view or item template is typed for it (<c>x:DataType</c>), or when <see cref="ShownInCode"/> names the code that
/// shows it. Both sides' XAML is copied next to the tests at build time.
/// </remarks>
[Collection(nameof(GameViewTests))]
public sealed partial class ViewCoverageTests(AppEngine app)
{
    /// <summary>View models the Mac App shows with code rather than a view that XAML types for them.</summary>
    private static readonly Dictionary<Type, (Type ShownBy, string How)> ShownInCode = new()
    {
        [typeof(MainMenuViewModel)] = (typeof(MainMenus), "the window's menu and the macOS menu bar are built from it"),
        [typeof(DisplayOptionItemViewModelBase)] = (typeof(OptionItemView), "each option's editor, as its type asks"),
        [typeof(CBOBoolOptionItemViewModel)] = (typeof(CBOptionsView), "CoreBots' option rows, which XAML can't template (closed generics)"),
        [typeof(CBOBoolChoiceOptionItemViewModel)] = (typeof(CBOptionsView), "CoreBots' option rows, which XAML can't template (closed generics)"),
        [typeof(CBOChoiceOptionItemViewModel)] = (typeof(CBOptionsView), "CoreBots' option rows, which XAML can't template (closed generics)"),
        [typeof(AccountManagerViewModel)] = (typeof(AccountsView), "the Skua Manager's account list, through ManagerAccountsViewModel.Accounts"),
        [typeof(SelectGroupDialogViewModel)] = (typeof(ManagerDialogService), "the Skua Manager's group picker dialog"),
    };

    /// <summary>View models with a WPF view that the Mac App deliberately doesn't show, and why.</summary>
    private static readonly Dictionary<Type, string> Deliberate = new()
    {
        [typeof(BotControlViewModelBase)] =
            "WPF's fallback template for a panel with none of its own (it shows AboutView). ViewLocator maps each panel's own type, so it needs no fallback.",
        [typeof(LauncherViewModel)] =
            "The Skua Manager's Running tab replaces the Launcher: it starts a Mac App per account, not Skua.exe processes (ADR 0006).",
        [typeof(ClientUpdatesViewModel)] =
            "The Skua Manager's Updates tab replaces Client Updates: it compares the installed build with the checkout and downloads no Windows release (ADR 0006).",
        [typeof(ClientUpdateItemViewModel)] = "A Client Updates release row; Updates replaces Client Updates (ADR 0006).",
        [typeof(ManagerOptionsViewModel)] =
            "The Windows Manager's options (download folder, theme sync, GitHub token) have no macOS counterpart; the GitHub token is in Keychain, through the app menu's GitHub Login (ADR 0006).",
    };

    /// <summary>View models with a WPF view that the Mac App doesn't show yet, and the issue that brings it.</summary>
    private static readonly Dictionary<Type, int> Gaps = new()
    {
        [typeof(BotWindowViewModel)] = 109,
        // The main window exists, but its title doesn't follow MainViewModel's.
        [typeof(MainViewModel)] = 110,
    };

    [Fact]
    public void Every_Core_view_model_with_a_WPF_view_has_an_Avalonia_view_or_is_listed_as_deliberate_or_a_gap()
    {
        HashSet<Type> wpf = WpfViewModels();
        HashSet<Type> avalonia = AvaloniaViewModels();

        // The XAML was read: these have WPF views in each of the three projects.
        Assert.Superset(new HashSet<Type> { typeof(ScriptLoaderViewModel), typeof(MainViewModel), typeof(ManagerMainViewModel), typeof(CBOBoolOptionItemViewModel) }, wpf);

        List<string> missing = wpf
            .Where(t => !avalonia.Contains(t) && !Deliberate.ContainsKey(t) && !Gaps.ContainsKey(t))
            .Select(t => t.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0,
            $"These view models have a WPF view but no Avalonia view: {string.Join(", ", missing)}. Port the view, or list it in {nameof(Deliberate)} with its reason or in {nameof(Gaps)} with its issue.");
    }

    [Fact]
    public void The_deliberate_differences_and_gaps_list_only_view_models_with_a_WPF_view_and_no_Avalonia_view()
    {
        HashSet<Type> wpf = WpfViewModels();
        HashSet<Type> avalonia = AvaloniaViewModels();

        foreach (Type type in Deliberate.Keys.Concat(Gaps.Keys))
        {
            Assert.True(wpf.Contains(type), $"{type.Name} is listed, but no WPF view shows it.");
            Assert.False(avalonia.Contains(type), $"{type.Name} has an Avalonia view now: take it off the list.");
        }
        Assert.Empty(Deliberate.Keys.Intersect(Gaps.Keys));
        Assert.All(Deliberate.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [AvaloniaFact]
    public async Task Every_window_the_Windows_app_registers_opens_in_the_Mac_App_with_its_view()
    {
        // Core's own list of managed windows, as the Windows app registers them, read through a window service that only records.
        RecordingWindowService recorded = new();
        ManagedWindows.Register(new WithWindowService(app.Engine.Services, recorded));
        _ = app.Get<MainMenuViewModel>();
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        Assert.Contains("Script Repo", recorded.Registered.Keys);
        Assert.Contains("Notify Drop", recorded.Registered.Keys);

        foreach ((string key, Type type) in recorded.Registered)
        {
            Assert.True(windows.CanShow(key), $"The Mac App can't show the '{key}' window.");
            try
            {
                windows.ShowManagedWindow(key);
                await Ui.PumpUntilAsync(
                    () => windows.OpenWindow(key) is { } window && window.DataContext?.GetType() == type && Ui.Find<UserControl>(window) is not null,
                    $"the '{key}' window to show its view");
            }
            finally
            {
                windows.OpenWindow(key)?.Close();
            }
            await Ui.PumpUntilAsync(() => windows.OpenWindow(key) is null, $"the '{key}' window to close");
        }
    }

    /// <summary>Core's view models that the Windows XAML templates or designs a view for.</summary>
    private static HashSet<Type> WpfViewModels()
    {
        HashSet<Type> types = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "ViewSources", "Wpf"), "*.xaml", SearchOption.AllDirectories))
        {
            // Comments drop out with the parse, as some templates are commented out.
            foreach (XElement element in XDocument.Load(file).Descendants())
            {
                foreach (XAttribute attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                {
                    foreach (Match match in WpfTypeReference().Matches(attribute.Value))
                    {
                        if (CoreType(element, match.Groups["prefix"].Value, match.Groups["name"].Value, file) is { } type)
                            types.Add(type);
                    }
                }
            }
        }
        return types;
    }

    /// <summary>Core's view models that the Mac App shows.</summary>
    private static HashSet<Type> AvaloniaViewModels()
    {
        HashSet<Type> types = [.. ViewLocator.ViewModelTypes, .. ViewLocator.DialogViewModelTypes, .. ShownInCode.Keys];
        XName dataType = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "DataType";
        foreach (string file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "ViewSources", "Avalonia"), "*.axaml", SearchOption.AllDirectories))
        {
            foreach (XElement element in XDocument.Load(file).Descendants())
            {
                if (element.Attribute(dataType)?.Value.Split(':') is [string prefix, string name]
                    && CoreType(element, prefix, name, file) is { } type)
                    types.Add(type);
            }
        }
        return types;
    }

    /// <summary>
    /// The type <paramref name="prefix"/>:<paramref name="name"/> names, when it is in Skua.Core. WPF names the assembly, so a WPF reference
    /// to Skua.Core must be a type there; Avalonia's <c>using:</c> doesn't, so its types in other assemblies are skipped.
    /// </summary>
    private static Type? CoreType(XElement element, string prefix, string name, string file)
    {
        string uri = element.GetNamespaceOfPrefix(prefix)?.NamespaceName ?? throw new InvalidDataException($"{file}: no xmlns:{prefix}.");
        Match clr = ClrNamespace().Match(uri);
        if (!clr.Success)
            return null;
        string fullName = $"{clr.Groups["ns"].Value}.{name}";
        if (!clr.Groups["assembly"].Success)
            return typeof(MainViewModel).Assembly.GetType(fullName);
        if (clr.Groups["assembly"].Value != "Skua.Core")
            return null;
        return typeof(MainViewModel).Assembly.GetType(fullName) ?? throw new InvalidDataException($"{file}: {fullName} isn't a type in Skua.Core.");
    }

    /// <summary><c>{x:Type p:Name}</c>, <c>{d:DesignInstance p:Name}</c> and <c>{d:DesignInstance Type=p:Name}</c>.</summary>
    [GeneratedRegex(@"\{(?:x:Type|d:DesignInstance)\s+(?:Type=)?(?<prefix>\w+):(?<name>\w+)")]
    private static partial Regex WpfTypeReference();

    /// <summary>WPF's <c>clr-namespace:N;assembly=A</c> and Avalonia's <c>using:N</c>.</summary>
    [GeneratedRegex(@"^(?:clr-namespace:(?<ns>[\w.]+)(?:;assembly=(?<assembly>[\w.]+))?|using:(?<ns>[\w.]+))$")]
    private static partial Regex ClrNamespace();

    private sealed class RecordingWindowService : IWindowService
    {
        public Dictionary<string, Type> Registered { get; } = [];

        public void RegisterManagedWindow<TViewModel>(string key, TViewModel viewModel) where TViewModel : class, IManagedWindow =>
            Registered[key] = viewModel.GetType();

        public void ShowManagedWindow(string key) => throw new NotSupportedException();

        public void ShowWindow<TViewModel>(int width, int height) where TViewModel : class => throw new NotSupportedException();

        public void ShowWindow<TViewModel>() where TViewModel : class => throw new NotSupportedException();

        public void ShowWindow<TViewModel>(TViewModel viewModel) where TViewModel : class => throw new NotSupportedException();
    }

    /// <summary>The app's container, with another window service.</summary>
    private sealed class WithWindowService(IServiceProvider services, IWindowService windows) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IWindowService) ? windows : services.GetService(serviceType);
    }
}
