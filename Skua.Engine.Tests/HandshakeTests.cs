using Skua.Control;

namespace Skua.Engine.Tests;

public class HandshakeTests
{
    [Fact]
    public async Task An_Engine_on_another_protocol_version_is_refused_with_a_stop_hint()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox);

        ControlException error = await Assert.ThrowsAsync<ControlException>(() => sandbox.ConnectAsync());

        Assert.Equal(ErrorCode.ProtocolMismatch, error.Code);
        Assert.Contains("run 'skua engine stop'", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(other.StatusCalled);
    }

    [Fact]
    public async Task Engine_stop_stops_an_Engine_on_another_protocol_version()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox);

        bool stopped = await EngineClient.StopAsync(sandbox.Endpoint, EngineSandbox.StopTimeout, TestContext.Current.CancellationToken);

        Assert.True(stopped);
        Assert.False(EngineLock.IsHeld(sandbox.Endpoint.LockPath));
    }
}
