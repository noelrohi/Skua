using Microsoft.Extensions.DependencyInjection;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.Plugins;
using Skua.Core.ViewModels;
using Skua.Engine;

namespace Skua.Avalonia.Services;

public static class ConfigureServices
{
    /// <summary>
    /// Registers the Avalonia implementations of Core's platform services and the main app's view models; the counterpart of
    /// <c>AddWindowsServices</c> plus <c>AddSkuaMainAppViewModels</c>. The Mac App adds them through the Engine's host hook, after the
    /// Engine's own services, so they win.
    /// </summary>
    public static IServiceCollection AddAvaloniaServices(this IServiceCollection services)
    {
        services.AddSingleton<IDispatcherService, AvaloniaDispatcherService>();
        services.AddSingleton<AvaloniaWindowService>();
        services.AddSingleton<IWindowService>(s => s.GetRequiredService<AvaloniaWindowService>());
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<IFileDialogService, AvaloniaFileDialogService>();
        // Message boxes stay Script Dialogs of the Engine's one broker, which the window shows and answers (ADR 0006).
        services.AddSingleton<AvaloniaDialogService>();
        services.AddSingleton<IDialogService>(s => s.GetRequiredService<AvaloniaDialogService>());
        services.AddSingleton<ScriptDialogsViewModel>();
        services.AddSingleton<ISoundService, MacSoundService>();
        services.AddTransient<IScriptOptionContainer, AvaloniaScriptOptionContainer>();
        services.AddSingleton<IProcessService, MacProcessService>();
        services.AddSingleton<IThemeService, AvaloniaThemeService>();
        services.AddSingleton<GameOptionEdits>();
        services.AddSingleton<IHotKeyService, AvaloniaHotKeyService>();
        // Plugins load into the Engine's container as on Windows; one that fails, such as one that needs WPF, logs why instead.
        services.AddSingleton<PluginManager>();
        services.AddSingleton<IPluginManager, AppPluginManager>();
        services.AddSingleton<IPluginHelper, AppPluginHelper>();

        services.AddSkuaMainAppViewModels();
        // The Scripts panel starts and stops Scripts as script_start and script_stop do, so the Engine's runs say who stopped one.
        services.AddSingleton(s => ActivatorUtilities.CreateInstance<ScriptLoaderViewModel>(s, s.GetRequiredService<EngineScripts>().ScriptManager));
        // The Packet Interceptor lists packets through the synchronization context it is made on; made on this one, it lists them on the UI
        // thread whichever thread resolves it first.
        ServiceDescriptor interceptor = services.Last(d => d.ServiceType == typeof(PacketInterceptorViewModel));
        services.AddSingleton(s => UiThreadContext.Run(() => (PacketInterceptorViewModel)interceptor.ImplementationFactory!(s)));

        return services;
    }
}
