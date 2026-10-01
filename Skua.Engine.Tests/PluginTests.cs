using Skua.Control;

namespace Skua.Engine.Tests;

public class PluginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_windowless_Engine_loads_the_plugins_in_its_data_folder_and_logs_one_that_fails()
    {
        await using EngineSandbox sandbox = new();
        string plugins = Directory.CreateDirectory(Path.Combine(sandbox.SkuaDir, "plugins")).FullName;
        File.WriteAllBytes(Path.Combine(plugins, "Hello.dll"), HelloPlugin());
        File.WriteAllText(Path.Combine(plugins, "Broken.dll"), "not an assembly");

        (_, EngineConnection connection) = await sandbox.StartEngineAsync();
        using (connection)
        {
            await connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text == "hello from a plugin");
            string failed = (await connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text!.Contains("'Broken.dll'", StringComparison.Ordinal)))[0].Text!;
            Assert.StartsWith("The plugin 'Broken.dll' didn't load: ", failed);

            // The Engine runs on.
            StatusDto status = await connection.StatusAsync(Ct);
            Assert.Equal(EngineHost.Engine, status.Engine.Host);
        }
    }

    /// <summary>A plugin that logs "hello from a plugin" to the debug log as it loads.</summary>
    private static byte[] HelloPlugin()
    {
        const string source = """
            #nullable disable
            using System;
            using System.Collections.Generic;
            using Skua.Core.Interfaces;

            public sealed class HelloPlugin : ISkuaPlugin
            {
                public string Name => "Hello";
                public string Author => "Tester";
                public string Description => "A test's plugin.";
                public List<IOption> Options => null;

                public void Load(IServiceProvider provider, IPluginHelper helper) =>
                    ((ILogService)provider.GetService(typeof(ILogService))).DebugLog("hello from a plugin");

                public void Unload() { }
            }
            """;
        return PluginCompiler.Compile("Hello", source);
    }
}
