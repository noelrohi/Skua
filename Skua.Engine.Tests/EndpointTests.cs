using Skua.Control;

namespace Skua.Engine.Tests;

public class EndpointTests
{
    [Fact]
    public void The_socket_lives_under_the_data_folder()
    {
        EngineEndpoint endpoint = EngineEndpoint.Resolve("default", "/tmp/skua");

        Assert.Equal("/tmp/skua/engines/default.sock", endpoint.SocketPath);
        Assert.Equal("/tmp/skua/engines/default.lock", endpoint.LockPath);
    }

    [Fact]
    public void An_override_moves_only_the_socket()
    {
        EngineEndpoint endpoint = EngineEndpoint.Resolve("default", "/tmp/skua", "/tmp/other/e.sock");

        Assert.Equal("/tmp/other/e.sock", endpoint.SocketPath);
        Assert.Equal("/tmp/skua/engines/default.lock", endpoint.LockPath);
        Assert.Equal("/tmp/skua/engines/game-storage/default", endpoint.GameStorageDir);
    }

    [Fact]
    public void Each_Engine_Name_keeps_its_own_game_storage_under_the_data_folder()
    {
        Assert.Equal("/tmp/skua/engines/game-storage/farm", EngineEndpoint.Resolve("farm", "/tmp/skua").GameStorageDir);
        Assert.Equal("/tmp/skua/engines/game-storage/alt1", EngineEndpoint.Resolve("alt1", "/tmp/skua").GameStorageDir);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Default")]
    [InlineData("a_b")]
    [InlineData("seventeen-chars-x")]
    public void An_invalid_Engine_Name_is_refused(string name)
    {
        ControlException error = Assert.Throws<ControlException>(() => EngineEndpoint.Resolve(name, "/tmp/skua"));

        Assert.Equal(ErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public void The_data_folders_Engines_are_its_sockets_with_a_valid_Engine_Name_by_name()
    {
        string skuaDir = Directory.CreateDirectory(Path.Combine("/tmp", "skua-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            Assert.Empty(EngineEndpoint.InDataFolder(skuaDir));

            string engines = Directory.CreateDirectory(Path.Combine(skuaDir, "engines")).FullName;
            foreach (string file in (string[])["farm.sock", "alpha.sock", "Not Valid.sock", "alpha.lock", "beta.log"])
                File.WriteAllText(Path.Combine(engines, file), "");

            Assert.Equal(["alpha", "farm"], EngineEndpoint.InDataFolder(skuaDir).Select(e => e.Name));
            Assert.Equal(Path.Combine(engines, "alpha.sock"), EngineEndpoint.InDataFolder(skuaDir)[0].SocketPath);
        }
        finally
        {
            EngineSandbox.DeleteFolder(skuaDir);
        }
    }

    [Fact]
    public void A_socket_path_over_103_bytes_fails_fast()
    {
        string skuaDir = "/tmp/" + new string('s', 103 - "/tmp/".Length - "/engines/default.sock".Length);
        Assert.Equal(103, EngineEndpoint.Resolve("default", skuaDir).SocketPath.Length);

        ControlException error = Assert.Throws<ControlException>(() => EngineEndpoint.Resolve("default", skuaDir + "s"));

        Assert.Equal(ErrorCode.InvalidArgument, error.Code);
        Assert.Contains(EngineEndpoint.SocketVariable, error.Message);
    }
}
