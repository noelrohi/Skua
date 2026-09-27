using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.MacOS.GameHost;
using Skua.MacOS.Services;

namespace Skua.MacOS;

public static class ConfigureServices
{
    /// <summary>
    /// Registers the macOS implementations of Core's platform services; the headless counterpart of <c>AddWindowsServices</c>.
    /// Call it after <c>AddCommonServices</c>, so its registrations win.
    /// </summary>
    /// <param name="services">The Engine's services.</param>
    /// <param name="gameHost">The Game Host that <see cref="IFlashUtil.InitializeFlash"/> starts.</param>
    public static IServiceCollection AddMacServices(this IServiceCollection services, GameHostLaunch gameHost)
    {
        services.AddSingleton(gameHost);
        services.AddSingleton<BridgeFlashUtil>();
        services.AddSingleton<IFlashUtil>(s => s.GetRequiredService<BridgeFlashUtil>());

        services.AddSingleton<ScriptDialogBroker>();
        services.AddSingleton<IDialogService, HeadlessDialogService>();
        services.AddSingleton<IFileDialogService, HeadlessFileDialogService>();
        services.AddSingleton<IProcessService, HeadlessProcessService>();
        services.AddTransient<IScriptOptionContainer, HeadlessScriptOptionContainer>();

        return services;
    }
}
