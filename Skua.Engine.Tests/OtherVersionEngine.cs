using System.Net.Sockets;
using Skua.Control;
using StreamJsonRpc;

namespace Skua.Engine.Tests;

/// <summary>
/// Stands in for an Engine from another build: it holds the lock, serves the socket and answers <c>hello</c> with another build and,
/// by default, another protocol version. It answers nothing else but <c>shutdown</c> and <c>shutdown_if_idle</c>.
/// </summary>
public sealed class OtherVersionEngine : IEngineRpc, IAsyncDisposable
{
    public const int OtherProtocol = ControlProtocol.Version + 1;

    public const string OtherBuild = "0.0.0-other";

    private readonly EngineEndpoint _endpoint;
    private readonly int _protocol;
    private readonly bool _scriptRunning;
    private readonly bool _predatesShutdownIfIdle;
    private readonly EngineLock _lock;
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    /// <param name="scriptRunning">Whether <c>shutdown_if_idle</c> refuses, as while a Script runs.</param>
    /// <param name="predatesShutdownIfIdle">Whether it is an Engine from before <c>shutdown_if_idle</c>, which doesn't have the method.</param>
    public OtherVersionEngine(EngineSandbox sandbox, int protocol = OtherProtocol, bool scriptRunning = false, bool predatesShutdownIfIdle = false)
    {
        _endpoint = sandbox.Endpoint;
        _protocol = protocol;
        _scriptRunning = scriptRunning;
        _predatesShutdownIfIdle = predatesShutdownIfIdle;
        Directory.CreateDirectory(_endpoint.EnginesDir);
        _lock = EngineLock.TryAcquire(_endpoint.LockPath)!;
        _listener.Bind(new UnixDomainSocketEndPoint(_endpoint.SocketPath));
        _listener.Listen();
        _accepting = AcceptAsync();
    }

    public bool StatusCalled { get; private set; }

    public bool ShutdownRequested => _stop.IsCancellationRequested;

    public Task<HelloResult> HelloAsync(int protocol, CancellationToken cancellationToken) =>
        Task.FromResult(new HelloResult(_protocol, OtherBuild, _endpoint.Name, Environment.ProcessId));

    public Task<StatusDto> StatusAsync(CancellationToken cancellationToken)
    {
        StatusCalled = true;
        throw new InvalidOperationException("A client called status after a protocol mismatch.");
    }

    public Task<ScriptsSearchResult> ScriptsSearchAsync(string query, string? tag, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called scripts_search after a protocol mismatch.");

    public Task<ScriptsUpdateResult> ScriptsUpdateAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called scripts_update after a protocol mismatch.");

    public Task<ScriptsListResult> ScriptsListAsync(string? folder, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called scripts_list after a protocol mismatch.");

    public Task<ScriptsNewResult> ScriptsNewAsync(string? since, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called scripts_new after a protocol mismatch.");

    public Task<ScreenshotResult> ScreenshotAsync(int? maxWidth, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called screenshot after a protocol mismatch.");

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        return Task.CompletedTask;
    }

    public Task ShutdownIfIdleAsync(CancellationToken cancellationToken)
    {
        if (_predatesShutdownIfIdle)
            throw new LocalRpcException("Method not found.") { ErrorCode = (int)StreamJsonRpc.Protocol.JsonRpcErrorCode.MethodNotFound };
        if (_scriptRunning)
            throw new LocalRpcException("Can't replace the Engine while a Script is running.")
            {
                ErrorCode = ErrorCodes.ToWire(ErrorCode.ScriptRunning),
            };
        return ShutdownAsync(cancellationToken);
    }

    public Task<LogPage> LogsAsync(LogKind kind, string? after, int? max, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called logs after a protocol mismatch.");

    public IAsyncEnumerable<LogPage> SubscribeAsync(LogKind[] kinds, string? after, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called subscribe after a protocol mismatch.");

    public Task<ServersResult> ServersAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called servers after a protocol mismatch.");

    public Task<LoginResult> LoginAsync(string? server, int? timeoutSec, bool asAgent, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called login after a protocol mismatch.");

    public Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called logout after a protocol mismatch.");

    public Task<LocationResult> JoinAsync(string map, string? cell, string? pad, int? timeoutSec, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called join after a protocol mismatch.");

    public Task<LocationResult> JumpAsync(string cell, string? pad, int? timeoutSec, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called jump after a protocol mismatch.");

    public Task<InventoryResult> InventoryAsync(InventoryKind kind, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called inventory after a protocol mismatch.");

    public Task<QuestsResult> QuestsAsync(QuestFilter filter, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called quests after a protocol mismatch.");

    public Task<MapDto> MapAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called map after a protocol mismatch.");

    public Task<DropsResult> DropsAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called drops after a protocol mismatch.");
    public Task<ScriptOptionsResult> ScriptOptionsAsync(string script, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called script_options after a protocol mismatch.");

    public Task<ScriptStartResult> ScriptStartAsync(
        string script, IReadOnlyDictionary<string, string>? options, DialogMode? dialogs, int? dialogTimeoutSec, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called script_start after a protocol mismatch.");

    public Task<ScriptStopResult> ScriptStopAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called script_stop after a protocol mismatch.");

    public Task<ScriptStatusDto> ScriptStatusAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called script_status after a protocol mismatch.");

    public Task<ScriptWaitResult> ScriptWaitAsync(int? timeoutSec, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called script_wait after a protocol mismatch.");

    public Task<DialogsResult> DialogsAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called dialogs after a protocol mismatch.");

    public Task<DialogAnswerResult> DialogAnswerAsync(int id, string choice, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called dialog_answer after a protocol mismatch.");

    public Task<EvalResult> EvalAsync(string code, int? timeoutSec, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A client called eval after a protocol mismatch.");

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
