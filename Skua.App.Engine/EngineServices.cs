using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.MacOS;
using Skua.MacOS.GameHost;

namespace Skua.App.Engine;

/// <summary>The Engine's composition root: Core's services with the macOS platform services.</summary>
internal static class EngineServices
{
    public static ServiceProvider Build(GameHostLaunch gameHost)
    {
        IServiceCollection services = new ServiceCollection();

        services.AddSingleton<ISettingsService, EngineSettingsService>();

        services.AddCommonServices();

        services.AddScriptableObjects();

        services.AddCompiler();

        services.AddMacServices(gameHost);

        ServiceProvider provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        return provider;
    }
}
