using Microsoft.CodeAnalysis;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Engine.Tests;

namespace Skua.Avalonia.Tests;

/// <summary>
/// Plugins built for a test and written to the data folder's plugins folder: one that works, and two built against a stand-in for WPF's
/// <c>PresentationFramework</c>, which isn't there when they run, as on a Mac.
/// </summary>
internal static class TestPlugins
{
    /// <summary>
    /// Writes <c>Good{id}.dll</c>, whose menu item logs "hello from {id}"; <c>WpfWindow{id}.dll</c>, which opens a WPF window as it loads;
    /// and <c>WpfMenu{id}.dll</c>, whose menu item opens one.
    /// </summary>
    public static void Write(string id)
    {
        Directory.CreateDirectory(ClientFileSources.SkuaPluginsDIR);
        MetadataReference wpf = MetadataReference.CreateFromImage(PluginCompiler.Compile("PresentationFramework", """
            namespace System.Windows
            {
                public class Window
                {
                    public void Show() { }
                }
            }
            """, []));

        Emit($"Good{id}", Plugin($"Good {id}", $$"""
            private IPluginHelper? _helper;

            public void Load(IServiceProvider provider, IPluginHelper helper)
            {
                _helper = helper;
                ILogService log = (ILogService)provider.GetService(typeof(ILogService))!;
                helper.AddMenuButton("Hello {{id}}", () => log.DebugLog("hello from {{id}}"));
            }

            public void Unload() => _helper?.RemoveMenuButton("Hello {{id}}");
            """), []);
        Emit($"WpfWindow{id}", Plugin($"WPF window {id}", """
            public void Load(IServiceProvider provider, IPluginHelper helper) => new System.Windows.Window().Show();

            public void Unload() { }
            """), [wpf]);
        Emit($"WpfMenu{id}", Plugin($"WPF menu {id}", $$"""
            private IPluginHelper? _helper;

            public void Load(IServiceProvider provider, IPluginHelper helper)
            {
                _helper = helper;
                helper.AddMenuButton("Open WPF {{id}}", ShowWindow);
            }

            private static void ShowWindow() => new System.Windows.Window().Show();

            public void Unload() => _helper?.RemoveMenuButton("Open WPF {{id}}");
            """), [wpf]);
    }

    private static string Plugin(string name, string body) => $$"""
        #nullable enable
        using System;
        using System.Collections.Generic;
        using Skua.Core.Interfaces;

        public sealed class TestPlugin : ISkuaPlugin
        {
            public string Name => "{{name}}";
            public string Author => "Tester";
            public string Description => "A test's plugin.";
            public List<IOption>? Options => null;

            {{body}}
        }
        """;

    private static void Emit(string assembly, string source, MetadataReference[] references) =>
        File.WriteAllBytes(Path.Combine(ClientFileSources.SkuaPluginsDIR, assembly + ".dll"), PluginCompiler.Compile(assembly, source, references));
}
