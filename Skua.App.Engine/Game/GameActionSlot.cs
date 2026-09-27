using Skua.Control;
using Skua.Core.Interfaces;

namespace Skua.App.Engine.Game;

/// <summary>
/// The one slot for a game action (a login, logout, join or jump), so two never overlap, and none runs beside a Script; and the checks that
/// the game can be driven, which the queries share.
/// </summary>
internal sealed class GameActionSlot
{
    private readonly GameStateTracker _tracker;
    private readonly IScriptManager _scripts;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public GameActionSlot(GameStateTracker tracker, IScriptManager scripts)
    {
        _tracker = tracker;
        _scripts = scripts;
    }

    /// <summary>Checks that the game can be driven now, and takes the slot until the lease is disposed.</summary>
    /// <param name="action">What the caller does, for the error messages, e.g. <c>log in</c>.</param>
    public async Task<IDisposable> BeginAsync(string action)
    {
        EnsureReady(action);
        if (_scripts.ScriptRunning)
            throw RpcErrors.Of(ErrorCode.ScriptRunning, $"Can't {action} while a Script runs; stop it first.");
        if (!await _busy.WaitAsync(0))
            throw RpcErrors.Of(ErrorCode.Busy, $"Can't {action}: another login, logout, join or jump is running.");
        return new Lease(_busy);
    }

    /// <summary>Checks that the Test Account is playing, which a query needs but no slot.</summary>
    public void EnsurePlaying(string action)
    {
        EnsureReady(action);
        if (_tracker.State != GameState.Playing)
            throw RpcErrors.Of(ErrorCode.NotLoggedIn, $"Can't {action}: the Test Account isn't playing; run 'skua login' first.");
    }

    private void EnsureReady(string action)
    {
        if (!_tracker.Ready)
            throw RpcErrors.Of(ErrorCode.GameHostDown, $"Can't {action}: the Game Client hasn't loaded in a running Game Host; see 'skua status'.");
    }

    private sealed class Lease(SemaphoreSlim busy) : IDisposable
    {
        public void Dispose() => busy.Release();
    }
}
