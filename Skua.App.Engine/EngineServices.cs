using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.AppStartup;
using Skua.MacOS;

namespace Skua.App.Engine;

/// <summary>The Engine's composition root: Core's services with the macOS platform services.</summary>
internal static class EngineServices
{
    public static ServiceProvider Build()
    {
        IServiceCollection services = new ServiceCollection();

        services.AddCommonServices();

        services.AddScriptableObjects();

        services.AddCompiler();

        services.AddMacServices();

        ServiceProvider provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        return provider;
    }
}
