namespace Skua.Engine.Tests;

public sealed partial class FakeKeychain
{
    /// <param name="sandbox">The sandbox whose data folder the tool and its items live in.</param>
    public FakeKeychain(EngineSandbox sandbox, string username = "SkuaTester", string password = "hunter2-Sekrit!", string? service = DefaultService)
        : this(sandbox.SkuaDir, username, password, service)
    {
    }
}
