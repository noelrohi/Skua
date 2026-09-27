using StreamJsonRpc;

namespace Skua.Control;

/// <summary>
/// One open JSON-RPC connection to an Engine, after the <c>hello</c> handshake.
/// </summary>
public sealed class EngineConnection : IDisposable
{
    private readonly JsonRpc _rpc;
    private readonly IEngineRpc _proxy;

    internal EngineConnection(JsonRpc rpc, IEngineRpc proxy, HelloResult hello)
    {
        _rpc = rpc;
        _proxy = proxy;
        Hello = hello;
    }

    public HelloResult Hello { get; }

    public bool IsCompatible => Hello.Protocol == ControlProtocol.Version;

    /// <exception cref="ControlException"><see cref="ErrorCode.ProtocolMismatch"/> when the Engine speaks another protocol version.</exception>
    public void EnsureCompatible()
    {
        if (!IsCompatible)
            throw new ControlException(ErrorCode.ProtocolMismatch,
                $"The running Engine '{Hello.EngineName}' (build {Hello.Build}) speaks protocol {Hello.Protocol}, but this skua speaks {ControlProtocol.Version}. Run 'skua engine stop', then try again.");
    }

    public Task<StatusDto> StatusAsync(CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.StatusAsync(cancellationToken));

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        CallAsync(async rpc => { await rpc.ShutdownAsync(cancellationToken); return true; });

    public Task<ScriptsSearchResult> ScriptsSearchAsync(string query, string? tag = null, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptsSearchAsync(query, tag, cancellationToken));

    public Task<ScriptsUpdateResult> ScriptsUpdateAsync(CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptsUpdateAsync(cancellationToken));

    /// <summary>Calls the Engine and turns its errors into <see cref="ControlException"/>.</summary>
    public async Task<T> CallAsync<T>(Func<IEngineRpc, Task<T>> call)
    {
        try
        {
            return await call(_proxy);
        }
        catch (RemoteInvocationException e) when (ErrorCodes.FromWire(e.ErrorCode) is ErrorCode code)
        {
            throw new ControlException(code, e.Message, e);
        }
        catch (ConnectionLostException e)
        {
            throw new ControlException(ErrorCode.EngineUnavailable, "The connection to the Engine was lost.", e);
        }
    }

    public void Dispose() => _rpc.Dispose();
}
