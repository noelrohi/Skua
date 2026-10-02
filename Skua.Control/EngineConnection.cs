using System.Runtime.CompilerServices;
using System.Text.Json;
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
                $"The running Engine '{Hello.EngineName}' (build {Hello.Build}) speaks protocol {Hello.Protocol}, but this skua speaks {ControlProtocol.Version}. "
                + (Hello.Host == EngineHost.App ? "The Skua app hosts it: quit the app, then try again." : "Run 'skua engine stop', then try again."));
    }

    public Task<StatusDto> StatusAsync(CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.StatusAsync(cancellationToken));

    /// <exception cref="ControlException"><see cref="ErrorCode.EngineOwnedByApp"/> when the Mac App hosts the Engine.</exception>
    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        CallAsync(async rpc => { await rpc.ShutdownAsync(cancellationToken); return true; });

    /// <summary>Shuts the Engine down unless it is busy. Returns false for an Engine from before <c>shutdown_if_idle</c>, which it leaves running.</summary>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.ScriptRunning"/> or <see cref="ErrorCode.Busy"/> when the Engine is busy; <see cref="ErrorCode.EngineOwnedByApp"/> when the
    /// Mac App hosts it.
    /// </exception>
    public async Task<bool> ShutdownIfIdleAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await CallAsync(async rpc => { await rpc.ShutdownIfIdleAsync(cancellationToken); return true; });
        }
        catch (RemoteMethodNotFoundException)
        {
            return false;
        }
    }

    public Task<ScriptsSearchResult> ScriptsSearchAsync(string query, string? tag = null, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptsSearchAsync(query, tag, cancellationToken));

    public Task<ScriptsUpdateResult> ScriptsUpdateAsync(CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptsUpdateAsync(cancellationToken));

    public Task<ScriptsListResult> ScriptsListAsync(string? folder = null, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptsListAsync(folder, cancellationToken));

    public Task<ScriptsNewResult> ScriptsNewAsync(string? since = null, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptsNewAsync(since, cancellationToken));

    public Task<ScriptSourceResult> ScriptsSourceAsync(CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptsSourceAsync(cancellationToken));

    public Task<ScriptSourceResult> ScriptsSourceSetAsync(string? source, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptsSourceSetAsync(source, cancellationToken));

    public Task<LogPage> LogsAsync(LogKind kind = LogKind.All, string? after = null, int? max = null, CancellationToken cancellationToken = default) =>
        LogsAsync(kind, after, max, null, cancellationToken);

    /// <summary>A page as above, or with <paramref name="tail"/> the newest entries after the cursor; see <see cref="IEngineRpc.LogsAsync"/>.</summary>
    public Task<LogPage> LogsAsync(LogKind kind, string? after, int? max, int? tail, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.LogsAsync(kind, after, max, tail, cancellationToken));

    public Task<ServersResult> ServersAsync(CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ServersAsync(cancellationToken));

    public Task<LoginResult> LoginAsync(string? server = null, int? timeoutSec = null, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.LoginAsync(server, timeoutSec, asAgent: false, cancellationToken));

    /// <summary>An agent's login: with the Test Account, unless the active account allows agents.</summary>
    public Task<LoginResult> AgentLoginAsync(string? server = null, int? timeoutSec = null, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.LoginAsync(server, timeoutSec, asAgent: true, cancellationToken));

    public Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.LogoutAsync(cancellationToken));

    public Task<LocationResult> JoinAsync(string map, string? cell = null, string? pad = null, int? timeoutSec = null, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.JoinAsync(map, cell, pad, timeoutSec, cancellationToken));

    public Task<LocationResult> JumpAsync(string cell, string? pad = null, int? timeoutSec = null, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.JumpAsync(cell, pad, timeoutSec, cancellationToken));

    public Task<InventoryResult> InventoryAsync(InventoryKind kind = InventoryKind.Inventory, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.InventoryAsync(kind, cancellationToken));

    public Task<QuestsResult> QuestsAsync(QuestFilter filter = QuestFilter.Loaded, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.QuestsAsync(filter, cancellationToken));

    public Task<MapDto> MapAsync(CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.MapAsync(cancellationToken));

    public Task<DropsResult> DropsAsync(CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.DropsAsync(cancellationToken));
    public Task<ScriptOptionsResult> ScriptOptionsAsync(string script, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptOptionsAsync(script, cancellationToken));

    public Task<ScriptStartResult> ScriptStartAsync(
        string script, IReadOnlyDictionary<string, string>? options = null, DialogMode? dialogs = null, int? dialogTimeoutSec = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptStartAsync(script, options, dialogs, dialogTimeoutSec, cancellationToken));

    public Task<ScriptStopResult> ScriptStopAsync(CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptStopAsync(cancellationToken));

    public Task<ScriptStatusDto> ScriptStatusAsync(CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptStatusAsync(cancellationToken));

    public Task<ScriptWaitResult> ScriptWaitAsync(int? timeoutSec = null, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScriptWaitAsync(timeoutSec, cancellationToken));

    public Task<DialogsResult> DialogsAsync(CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.DialogsAsync(cancellationToken));

    public Task<DialogAnswerResult> DialogAnswerAsync(int id, string choice, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.DialogAnswerAsync(id, choice, cancellationToken));

    public Task<EvalResult> EvalAsync(string code, int? timeoutSec = null, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.EvalAsync(code, timeoutSec, cancellationToken));

    public Task<ChatSendResult> ChatSendAsync(string text, string? to = null, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ChatSendAsync(text, to, cancellationToken));

    /// <summary>Replays the entries after the cursor, then follows new ones until cancelled.</summary>
    public async IAsyncEnumerable<LogPage> SubscribeAsync(
        LogKind[] kinds, string? after = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<LogPage> pages = _proxy.SubscribeAsync(kinds, after, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await CallAsync(async _ => await pages.MoveNextAsync()))
                yield return pages.Current;
        }
        finally
        {
            // Disposing tells the Engine to end the subscription, which a lost connection has already done.
            try
            {
                await pages.DisposeAsync();
            }
            catch (ConnectionLostException)
            {
            }
        }
    }

    public Task<ScreenshotResult> ScreenshotAsync(int? maxWidth = null, CancellationToken cancellationToken = default) =>
        CallAsync(rpc => rpc.ScreenshotAsync(maxWidth, cancellationToken));

    /// <summary>Calls the Engine and turns its errors into <see cref="ControlException"/>.</summary>
    public async Task<T> CallAsync<T>(Func<IEngineRpc, Task<T>> call)
    {
        try
        {
            return await call(_proxy);
        }
        catch (RemoteInvocationException e) when (ErrorCodes.FromWire(e.ErrorCode) is ErrorCode code)
        {
            throw new ControlException(code, e.Message, e, Diagnostics(e.ErrorData));
        }
        catch (ConnectionLostException e)
        {
            throw new ControlException(ErrorCode.EngineUnavailable, "The connection to the Engine was lost.", e);
        }
    }

    public void Dispose() => _rpc.Dispose();

    /// <summary>The diagnostics in an error's <see cref="ErrorDataDto"/>, if it has any.</summary>
    private static IReadOnlyList<string>? Diagnostics(object? errorData) =>
        errorData is JsonElement { ValueKind: JsonValueKind.Object } data
        && data.TryGetProperty("diagnostics", out JsonElement diagnostics) && diagnostics.ValueKind == JsonValueKind.Array
            ? diagnostics.EnumerateArray().Select(d => d.GetString() ?? "").ToList()
            : null;
}
