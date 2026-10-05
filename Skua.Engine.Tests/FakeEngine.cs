using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Skua.Control;
using StreamJsonRpc;

namespace Skua.Engine.Tests;

/// <summary>
/// Stands in for a running Engine of this protocol for what the Hook Runner calls: it holds the lock, serves the socket, answers <c>hello</c>,
/// <c>logs</c> (an empty page) and <c>subscribe</c> (the events <see cref="EmitAsync"/> pushes), and keeps each <c>hook_ran</c>. It records the
/// name of every call. Anything else fails.
/// </summary>
public sealed class FakeEngine : IEngineRpc, IAsyncDisposable
{
    private const long Epoch = 1;

    private readonly EngineEndpoint _endpoint;
    private readonly EngineLock _lock;
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private readonly Channel<LogEntryDto> _events = Channel.CreateUnbounded<LogEntryDto>();
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<HookRunDto> _runs = Channel.CreateUnbounded<HookRunDto>();
    private long _seq;

    public FakeEngine(EngineSandbox sandbox, string name)
    {
        _endpoint = EngineEndpoint.Resolve(name, sandbox.SkuaDir);
        Directory.CreateDirectory(_endpoint.EnginesDir);
        _lock = EngineLock.TryAcquire(_endpoint.LockPath)!;
        _listener.Bind(new UnixDomainSocketEndPoint(_endpoint.SocketPath));
        _listener.Listen();
        _accepting = AcceptAsync();
    }

    public EngineEndpoint Endpoint => _endpoint;

    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>The kinds and cursor of the first <c>subscribe</c>.</summary>
    public (LogKind[] Kinds, string? After)? Subscription { get; private set; }

    public Task Subscribed => _subscribed.Task;

    /// <summary>Records an event and pushes it to the subscriber; returns its seq.</summary>
    public long Emit(string type, object data)
    {
        long seq = Interlocked.Increment(ref _seq);
        _events.Writer.TryWrite(new LogEntryDto(seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), LogKind.Events, null, null, type,
            JsonSerializer.SerializeToElement(data, ControlJson.Options)));
        return seq;
    }

    /// <summary>The next <c>hook_ran</c> the Engine is told of.</summary>
    public async Task<HookRunDto> NextRunAsync(TimeSpan timeout)
    {
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(timeout);
        return await _runs.Reader.ReadAsync(cancel.Token);
    }

    /// <summary>Whether a <c>hook_ran</c> arrived that <see cref="NextRunAsync"/> hasn't returned.</summary>
    public bool HasRun => _runs.Reader.Count > 0;

    public Task<HelloResult> HelloAsync(int protocol, CancellationToken cancellationToken)
    {
        Calls.Enqueue("hello");
        return Task.FromResult(new HelloResult(ControlProtocol.Version, ControlProtocol.Build, _endpoint.Name, Environment.ProcessId, EngineHost.Engine));
    }

    public Task<LogPage> LogsAsync(LogKind kind, string? after, int? max, int? tail, CancellationToken cancellationToken)
    {
        Calls.Enqueue("logs");
        return Task.FromResult(new LogPage([], $"{Epoch}.{Interlocked.Read(ref _seq)}", false));
    }

    public async IAsyncEnumerable<LogPage> SubscribeAsync(
        LogKind[] kinds, string? after, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls.Enqueue("subscribe");
        Subscription ??= (kinds, after);
        _subscribed.TrySetResult();
        await foreach (LogEntryDto entry in _events.Reader.ReadAllAsync(cancellationToken))
            yield return new LogPage([entry], $"{Epoch}.{entry.Seq}", false);
    }

    public Task HookRanAsync(HookRunDto run, CancellationToken cancellationToken)
    {
        Calls.Enqueue("hook_ran");
        _runs.Writer.TryWrite(run);
        return Task.CompletedTask;
    }

    public Task<StatusDto> StatusAsync(CancellationToken cancellationToken) => Unexpected<StatusDto>("status");

    public Task ShutdownAsync(CancellationToken cancellationToken) => Unexpected<bool>("shutdown");

    public Task ShutdownIfIdleAsync(CancellationToken cancellationToken) => Unexpected<bool>("shutdown_if_idle");

    public Task<ScriptsSearchResult> ScriptsSearchAsync(string query, string? tag, CancellationToken cancellationToken) =>
        Unexpected<ScriptsSearchResult>("scripts_search");

    public Task<ScriptsUpdateResult> ScriptsUpdateAsync(CancellationToken cancellationToken) => Unexpected<ScriptsUpdateResult>("scripts_update");

    public Task<ScriptsListResult> ScriptsListAsync(string? folder, CancellationToken cancellationToken) => Unexpected<ScriptsListResult>("scripts_list");

    public Task<ScriptsNewResult> ScriptsNewAsync(string? since, CancellationToken cancellationToken) => Unexpected<ScriptsNewResult>("scripts_new");

    public Task<ScriptSourceResult> ScriptsSourceAsync(CancellationToken cancellationToken) => Unexpected<ScriptSourceResult>("scripts_source");

    public Task<ScriptSourceResult> ScriptsSourceSetAsync(string? source, CancellationToken cancellationToken) =>
        Unexpected<ScriptSourceResult>("scripts_source_set");

    public Task<ScreenshotResult> ScreenshotAsync(int? maxWidth, CancellationToken cancellationToken) => Unexpected<ScreenshotResult>("screenshot");

    public Task<ChatSendResult> ChatSendAsync(string text, string? to, CancellationToken cancellationToken) => Unexpected<ChatSendResult>("chat_send");

    public Task<ServersResult> ServersAsync(CancellationToken cancellationToken) => Unexpected<ServersResult>("servers");

    public Task<LoginResult> LoginAsync(string? server, int? timeoutSec, bool asAgent, string? account, CancellationToken cancellationToken) =>
        Unexpected<LoginResult>("login");

    public Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken) => Unexpected<LogoutResult>("logout");

    public Task<LocationResult> JoinAsync(string map, string? cell, string? pad, int? timeoutSec, CancellationToken cancellationToken) =>
        Unexpected<LocationResult>("join");

    public Task<LocationResult> JumpAsync(string cell, string? pad, int? timeoutSec, CancellationToken cancellationToken) =>
        Unexpected<LocationResult>("jump");

    public Task<InventoryResult> InventoryAsync(InventoryKind kind, CancellationToken cancellationToken) => Unexpected<InventoryResult>("inventory");

    public Task<QuestsResult> QuestsAsync(QuestFilter filter, CancellationToken cancellationToken) => Unexpected<QuestsResult>("quests");

    public Task<QuestCompleteResult> QuestCompleteAsync(int id, int? rewardId, int? timeoutSec, CancellationToken cancellationToken) =>
        Unexpected<QuestCompleteResult>("quest_complete");

    public Task<MapDto> MapAsync(CancellationToken cancellationToken) => Unexpected<MapDto>("map");

    public Task<DropsResult> DropsAsync(CancellationToken cancellationToken) => Unexpected<DropsResult>("drops");

    public Task<ScriptOptionsResult> ScriptOptionsAsync(string script, CancellationToken cancellationToken) =>
        Unexpected<ScriptOptionsResult>("script_options");

    public Task<ScriptStartResult> ScriptStartAsync(
        string script, IReadOnlyDictionary<string, string>? options, DialogMode? dialogs, int? dialogTimeoutSec, CancellationToken cancellationToken) =>
        Unexpected<ScriptStartResult>("script_start");

    public Task<ScriptStopResult> ScriptStopAsync(CancellationToken cancellationToken) => Unexpected<ScriptStopResult>("script_stop");

    public Task<ScriptStatusDto> ScriptStatusAsync(CancellationToken cancellationToken) => Unexpected<ScriptStatusDto>("script_status");

    public Task<ScriptWaitResult> ScriptWaitAsync(int? timeoutSec, CancellationToken cancellationToken) => Unexpected<ScriptWaitResult>("script_wait");

    public Task<DialogsResult> DialogsAsync(CancellationToken cancellationToken) => Unexpected<DialogsResult>("dialogs");

    public Task<DialogAnswerResult> DialogAnswerAsync(int id, string choice, CancellationToken cancellationToken) =>
        Unexpected<DialogAnswerResult>("dialog_answer");

    public Task<EvalResult> EvalAsync(string code, int? timeoutSec, CancellationToken cancellationToken) => Unexpected<EvalResult>("eval");

    private Task<T> Unexpected<T>(string method)
    {
        Calls.Enqueue(method);
        throw new InvalidOperationException($"The fake Engine doesn't serve {method}.");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _events.Writer.TryComplete();
        await _accepting;
        File.Delete(_endpoint.SocketPath);
        _lock.Dispose();
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
        }
    }
}
