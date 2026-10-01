using Microsoft.Extensions.DependencyInjection;
using Skua.Avalonia.Services;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Services;
using Skua.Core.ViewModels;
using Skua.Core.ViewModels.Manager;

namespace Skua.Avalonia.Manager;

public static class ManagerServices
{
    /// <summary>
    /// The Skua Manager's container on macOS, the counterpart of <c>Skua.Manager</c>'s: Core's account list and goals, with the Mac's settings,
    /// dialogs, launching, running list and updates in place of the Windows ones. Windows' launcher, client updates and options (release zips,
    /// themes to sync) have no macOS counterpart, so they aren't registered.
    /// </summary>
    public static IServiceCollection AddManagerServices(this IServiceCollection services, string skuaDir)
    {
        services.AddSingleton(new ManagerAccounts(skuaDir));
        services.AddSingleton(new AppInstances(skuaDir));
        services.AddSingleton<ManagerSettingsService>();
        services.AddSingleton<ISettingsService>(s => s.GetRequiredService<ManagerSettingsService>());
        services.AddSingleton<IDialogService, ManagerDialogService>();
        services.AddSingleton<IDispatcherService, AvaloniaDispatcherService>();
        services.AddSingleton<Foreground>();
        services.AddSingleton<AvaloniaWindowService>();
        services.AddSingleton<IWindowService>(s => s.GetRequiredService<AvaloniaWindowService>());
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<IFileDialogService, AvaloniaFileDialogService>();
        services.AddSingleton<IProcessService, ProcessStartService>();

        services.AddSingleton<AccountManagerViewModel>();
        services.AddSingleton<ManagerAccountsViewModel>();
        services.AddSingleton<RunningViewModel>();
        services.AddSingleton<UpdatesViewModel>();
        services.AddSingleton<GoalsViewModel>();
        services.AddSingleton(s => new ManagerMainViewModel(
            [
                new TabItemViewModel("Accounts", s.GetRequiredService<ManagerAccountsViewModel>()),
                new TabItemViewModel("Running", s.GetRequiredService<RunningViewModel>()),
                new TabItemViewModel("Updates", s.GetRequiredService<UpdatesViewModel>()),
                new TabItemViewModel("Goals", s.GetRequiredService<GoalsViewModel>()),
            ],
            s.GetRequiredService<IDialogService>(),
            s.GetRequiredService<ISettingsService>())
        {
            Title = "Skua Manager",
        });
        return services;
    }
}
