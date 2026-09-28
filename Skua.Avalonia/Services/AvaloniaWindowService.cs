using System.Diagnostics;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;

namespace Skua.Avalonia.Services;

/// <summary>
/// Core's windows as Avalonia windows: managed windows by key, as <c>ManagedWindows</c> registers them, each shown in one
/// <see cref="HostWindow"/> that a later show brings to the front, and made again once closed.
/// </summary>
/// <remarks>A view model without a view (<see cref="ViewLocator"/>) is never shown; its ticket brings the view.</remarks>
public sealed class AvaloniaWindowService : IWindowService
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, IManagedWindow> _managed = [];
    private readonly Dictionary<string, HostWindow> _open = [];

    public AvaloniaWindowService(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Runs on each window this service makes, before it shows; the app gives each its native menu.</summary>
    public Action<Window>? WindowCreated { get; set; }

    /// <summary>Whether <see cref="ShowManagedWindow"/> shows a window for <paramref name="key"/>: it is registered and has a view.</summary>
    public bool CanShow(string key)
    {
        lock (_managed)
            return _managed.TryGetValue(key, out IManagedWindow? viewModel) && ViewLocator.HasView(viewModel);
    }

    /// <summary>Whether <paramref name="key"/> is a managed window's; a menu item that isn't one has its own command, such as Bank.</summary>
    public bool IsManaged(string key)
    {
        lock (_managed)
            return _managed.ContainsKey(key);
    }

    /// <summary>The open window for <paramref name="key"/>, if any.</summary>
    public HostWindow? OpenWindow(string key) => _open.GetValueOrDefault(key);

    public void RegisterManagedWindow<TViewModel>(string key, TViewModel viewModel) where TViewModel : class, IManagedWindow
    {
        lock (_managed)
            _managed.TryAdd(key, viewModel);
    }

    public void ShowManagedWindow(string key) => UiThread.Post(() =>
    {
        IManagedWindow? viewModel;
        lock (_managed)
            _managed.TryGetValue(key, out viewModel);
        if (viewModel is null || !ViewLocator.HasView(viewModel))
        {
            Trace.WriteLine($"The Mac App has no view for '{key}' yet.");
            return;
        }

        if (!_open.TryGetValue(key, out HostWindow? window))
        {
            window = Create(viewModel);
            _open[key] = window;
            window.Closed += (_, _) =>
            {
                _open.Remove(key);
                // As on Windows, where closing hides it: the view model stops its work, and CoreBots saves its options, unless the Bot
                // Window shows it too.
                if (viewModel is ObservableRecipient closed)
                    PanelActivity.Hidden(closed);
            };
            window.Show();
            // As on Windows: a view model starts its work (the Script Source's list, for one) once its window first shows.
            if (viewModel is ObservableRecipient shown)
                PanelActivity.Shown(shown);
        }
        else if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
    });

    public void ShowWindow<TViewModel>() where TViewModel : class => Show(_services.GetService<TViewModel>(), null);

    public void ShowWindow<TViewModel>(int width, int height) where TViewModel : class => Show(_services.GetService<TViewModel>(), (width, height));

    public void ShowWindow<TViewModel>(TViewModel viewModel) where TViewModel : class => Show(viewModel, null);

    private void Show(object? viewModel, (int Width, int Height)? size) => UiThread.Post(() =>
    {
        if (viewModel is null || !ViewLocator.HasView(viewModel))
        {
            Trace.WriteLine($"The Mac App has no view for {viewModel?.GetType().Name ?? "a missing view model"} yet.");
            return;
        }
        HostWindow window = Create(viewModel);
        if (size is { } s)
        {
            window.Width = s.Width;
            window.Height = s.Height;
        }
        window.Show();
    });

    private HostWindow Create(object viewModel)
    {
        HostWindow window = new(viewModel);
        WindowCreated?.Invoke(window);
        return window;
    }
}
