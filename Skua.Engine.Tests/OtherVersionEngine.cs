using System.Net.Sockets;
using Skua.Control;
using StreamJsonRpc;

namespace Skua.Engine.Tests;

/// <summary>
/// Stands in for an Engine from another build: it holds the lock, serves the socket and answers <c>hello</c> with another protocol version.
/// </summary>
public sealed class OtherVersionEngine : IEngineRpc, IAsyncDisposable
{
    public const int OtherProtocol = ControlProtocol.Version + 1;

    private readonly EngineEndpoint _endpoint;
    private readonly EngineLock _lock;
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public OtherVersionEngine(EngineSandbox sandbox)
    {
        _endpoint = sandbox.Endpoint;
        Directory.CreateDirectory(_endpoint.EnginesDir);
        _lock = EngineLock.TryAcquire(_endpoint.LockPath)!;
        _listener.Bind(new UnixDomainSocketEndPoint(_endpoint.SocketPath));
        _listener.Listen();
        _accepting = AcceptAsync();
    }

    public bool StatusCalled { get; private set; }

    public Task<HelloResult> HelloAsync(int protocol, CancellationToken cancellationToken) =>
        Task.FromResult(new HelloResult(OtherProtocol, "0.0.0-other", _endpoint.Name, Environment.ProcessId));

    public Task<StatusDto> StatusAsync(CancellationToken cancellationToken)
    {
        StatusCalled = true;
        throw new InvalidOperationException("A client called status after a protocol mismatch.");
    }

    public Task<ScriptsSearchResult> ScriptsSearchAsync(string query, string? tag, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called scripts_search after a protocol mismatch.");

    public Task<ScriptsUpdateResult> ScriptsUpdateAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called scripts_update after a protocol mismatch.");

    public Task<ScreenshotResult> ScreenshotAsync(int? maxWidth, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called screenshot after a protocol mismatch.");

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        return Task.CompletedTask;
    }

    public Task<LogPage> LogsAsync(LogKind kind, string? after, int? max, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called logs after a protocol mismatch.");

    public IAsyncEnumerable<LogPage> SubscribeAsync(LogKind[] kinds, string? after, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called subscribe after a protocol mismatch.");

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _accepting;
    }

    private async Task AcceptAsync()
    {
        List<JsonRpc> connections = [];
        try
        {
            while (true)
            {
                JsonRpc rpc = ControlJson.CreateRpc(await _listener.AcceptAsync(_stop.Token));
                rpc.AddLocalRpcTarget<IEngineRpc>(this, null);
                rpc.StartListening();
                connections.Add(rpc);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _listener.Dispose();
            foreach (JsonRpc rpc in connections)
                rpc.Dispose();
            _lock.Dispose();
        }
    }
}
