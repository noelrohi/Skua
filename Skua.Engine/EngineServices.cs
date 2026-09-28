using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Skua.Engine.Logging;
using Skua.Core.AppStartup;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Services;
using Skua.MacOS;
using Skua.MacOS.GameHost;

namespace Skua.Engine;

/// <summary>The Engine's composition root: Core's services with the macOS platform services and the Engine's log sink.</summary>
internal static class EngineServices
{
    /// <param name="configure">Adds the host's services last, so they win.</param>
    public static ServiceProvider Build(GameHostLaunch gameHost, EngineLogs logs, Action<IServiceCollection>? configure)
    {
        IServiceCollection services = new ServiceCollection();

        services.AddSingleton<ISettingsService, EngineSettingsService>();

        services.AddCommonServices();
        // Upstream's CoreBots.cs doesn't compile on macOS, so the Engine's default Script Source is the Mac-ready fork.
        services.AddSingleton<IGetScriptsService>(s => new GetScriptsService(
            s.GetRequiredService<IDialogService>(), s.GetRequiredService<ISettingsService>(), ScriptSourceSetting.Default.ToCore()));

        services.AddScriptableObjects();

        services.AddCompiler();

        services.AddMacServices(gameHost);

        services.AddSingleton<ILogService>(new EngineLogService(logs));
        services.AddSingleton<EngineScripts>();

        configure?.Invoke(services);

        ServiceProvider provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        return provider;
    }
}
