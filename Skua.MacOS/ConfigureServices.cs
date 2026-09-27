using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.MacOS.Services;

namespace Skua.MacOS;

public static class ConfigureServices
{
    /// <summary>
    /// Registers the macOS implementations of Core's platform services; the headless counterpart of <c>AddWindowsServices</c>.
    /// Call it after <c>AddCommonServices</c>, so its registrations win.
    /// </summary>
    public static IServiceCollection AddMacServices(this IServiceCollection services)
    {
        services.AddSingleton<IDialogService, HeadlessDialogService>();
        services.AddSingleton<IFileDialogService, HeadlessFileDialogService>();
        services.AddSingleton<IProcessService, HeadlessProcessService>();

        return services;
    }
}
