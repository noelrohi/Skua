using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Plugins;

namespace Skua.Avalonia.Services;

/// <summary>
/// Core's plugin manager in the Mac App: plugins load from the data folder's <c>plugins</c> folder into the Engine's container, as on
/// Windows, and one that fails, such as one that needs WPF, is left out with an error in the debug log instead of taking the app down.
/// </summary>
/// <remarks>Core's manager returns a failed load's error, which its Plugins panel drops; this logs every one.</remarks>
public sealed class AppPluginManager : IPluginManager
{
    private static readonly string[] s_wpfAssemblies = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml", "System.Windows"];

    private readonly PluginManager _plugins;
    private readonly ILogService _log;

    public AppPluginManager(PluginManager plugins, ILogService log)
    {
        _plugins = plugins;
        _log = log;
    }

    public List<IPluginContainer> Containers => _plugins.Containers;

    /// <summary>Loads every plugin in the plugins folder, logging the ones that fail.</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(Path.Combine(ClientFileSources.SkuaPluginsDIR, "options"));
        foreach (string file in Directory.GetFiles(ClientFileSources.SkuaPluginsDIR, "*.dll").Order(StringComparer.Ordinal))
            Load(file);
    }

    public Exception? Load(string path)
    {
        Exception? error = _plugins.Load(path);
        if (error is not null)
            _log.DebugLog($"The plugin '{Path.GetFileName(path)}' didn't load: {Describe(error)}");
        return error;
    }

    public void Unload(ISkuaPlugin plugin) => Guard(plugin.Name, () => _plugins.Unload(plugin));

    public void Unload(string pluginName) => Guard(pluginName, () => _plugins.Unload(pluginName));

    public IPluginContainer? GetContainer(ISkuaPlugin plugin) => _plugins.GetContainer(plugin);

    public IPluginContainer? GetContainer(string pluginName) => _plugins.GetContainer(pluginName);

    public IPluginContainer GetContainer<T>() where T : ISkuaPlugin => _plugins.GetContainer<T>();

    /// <summary>
    /// A plugin's error as the log gives it: the innermost cause, and when that is a missing WPF assembly, that WPF is Windows-only.
    /// </summary>
    public static string Describe(Exception error)
    {
        Exception cause = Causes(error).LastOrDefault(e => e is FileNotFoundException or FileLoadException or TypeLoadException) ?? Innermost(error);
        // A missing assembly's message ends with a line break.
        string outer = error.Message.Trim();
        string inner = cause.Message.Trim();
        string message = outer.Contains(inner, StringComparison.Ordinal) ? outer : $"{outer} ({inner})";
        return NeedsWpf(error) ? $"{message} It needs WPF, which only Skua on Windows has." : message;
    }

    private void Guard(string? plugin, Action unload)
    {
        try
        {
            unload();
        }
        catch (Exception e)
        {
            _log.DebugLog($"The plugin '{plugin}' failed to unload: {Describe(e)}");
        }
    }

    private static bool NeedsWpf(Exception error) => Causes(error).Any(e =>
        (e as FileNotFoundException)?.FileName is { } file && s_wpfAssemblies.Any(a => file.StartsWith(a, StringComparison.Ordinal))
        || (e as FileLoadException)?.FileName is { } load && s_wpfAssemblies.Any(a => load.StartsWith(a, StringComparison.Ordinal))
        || (e as TypeLoadException)?.TypeName is { } type && type.StartsWith("System.Windows.", StringComparison.Ordinal));

    /// <summary>The error and everything under it: inner exceptions and a type load's loader exceptions.</summary>
    private static IEnumerable<Exception> Causes(Exception error)
    {
        yield return error;
        IEnumerable<Exception?> children = error switch
        {
            System.Reflection.ReflectionTypeLoadException load => load.LoaderExceptions,
            AggregateException aggregate => aggregate.InnerExceptions,
            _ => [error.InnerException],
        };
        foreach (Exception child in children.OfType<Exception>())
        {
            foreach (Exception cause in Causes(child))
                yield return cause;
        }
    }

    private static Exception Innermost(Exception error) => error.InnerException is { } inner ? Innermost(inner) : error;
}
