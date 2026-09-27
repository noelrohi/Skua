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
    public void An_override_moves_the_socket_and_its_lock()
    {
        EngineEndpoint endpoint = EngineEndpoint.Resolve("default", "/tmp/skua", "/tmp/other/e.sock");

        Assert.Equal("/tmp/other/e.sock", endpoint.SocketPath);
        Assert.Equal("/tmp/other/e.lock", endpoint.LockPath);
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
    public void A_socket_path_over_103_bytes_fails_fast()
    {
        string skuaDir = "/tmp/" + new string('s', 103 - "/tmp/".Length - "/engines/default.sock".Length);
        Assert.Equal(103, EngineEndpoint.Resolve("default", skuaDir).SocketPath.Length);

        ControlException error = Assert.Throws<ControlException>(() => EngineEndpoint.Resolve("default", skuaDir + "s"));

        Assert.Equal(ErrorCode.InvalidArgument, error.Code);
        Assert.Contains(EngineEndpoint.SocketVariable, error.Message);
    }
}
