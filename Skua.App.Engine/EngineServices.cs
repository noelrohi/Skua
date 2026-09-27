using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Engine.Logging;
using Skua.Core.AppStartup;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Services;
using Skua.MacOS;
using Skua.MacOS.GameHost;

namespace Skua.App.Engine;

/// <summary>The Engine's composition root: Core's services with the macOS platform services and the Engine's log sink.</summary>
internal static class EngineServices
{
    public static ServiceProvider Build(GameHostLaunch gameHost, EngineLogs logs)
    {
        IServiceCollection services = new ServiceCollection();

        services.AddSingleton<ISettingsService, EngineSettingsService>();

        services.AddCommonServices();
        // Upstream's CoreBots.cs doesn't compile on macOS, so the Engine's default Script Source is the Mac-ready fork.
        services.AddSingleton<IGetScriptsService>(s => new GetScriptsService(
            s.GetRequiredService<IDialogService>(), s.GetRequiredService<ISettingsService>(), EngineSettingsService.ToCore(ScriptSourceSetting.Default)));

        services.AddScriptableObjects();

        services.AddCompiler();

        services.AddMacServices(gameHost);

        services.AddSingleton<ILogService>(new EngineLogService(logs));

        ServiceProvider provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        return provider;
    }
}
