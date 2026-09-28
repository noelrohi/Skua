using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Control;

namespace Skua.Avalonia.Tests;

/// <summary>The Engine the Mac App hosts serves the normal socket, and takes the host's services.</summary>
/// <remarks>In the Game View tests' collection, as one of those restarts the Game Host.</remarks>
[Collection(nameof(GameViewTests))]
public sealed class AppEngineTests(AppEngine app)
{
    [Fact]
    public async Task Skua_status_connects_to_the_app_hosted_Engine()
    {
        ProcessStartInfo startInfo = new(Path.Combine(AppContext.BaseDirectory, "skua"), ["status", "--json"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment[EngineEndpoint.SkuaDirVariable] = AppEngine.SkuaDir;
        // No auto-start: the app's Engine is the only one.
        startInfo.Environment[EngineClientOptions.EngineExecutableVariable] = "/usr/bin/false";
        using Process cli = Process.Start(startInfo)!;
        string stdout = await cli.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        string stderr = await cli.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await cli.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(cli.ExitCode == 0, $"skua status exited with {cli.ExitCode}: {stderr}");
        using JsonDocument status = JsonDocument.Parse(stdout);
        JsonElement engine = status.RootElement.GetProperty("engine");
        Assert.Equal(EngineName.Default, engine.GetProperty("name").GetString());
        Assert.Equal(Environment.ProcessId, engine.GetProperty("pid").GetInt32());
        Assert.Equal(ControlProtocol.Build, engine.GetProperty("build").GetString());
        Assert.True(status.RootElement.GetProperty("game").GetProperty("gameHostUp").GetBoolean());
    }

    [Fact]
    public void The_host_adds_its_services_to_the_container_Ioc_Default_serves()
    {
        Assert.NotNull(Ioc.Default.GetService<HostMarker>());
        Assert.Same(app.Engine.Services.GetService(typeof(HostMarker)), Ioc.Default.GetService<HostMarker>());
    }
}
