using Skua.Control;
using Skua.Core.Interfaces;

namespace Skua.App.Engine.Game;

/// <summary>The one slot for a game action (a login, logout, join or jump), so two never overlap, and none runs beside a Script.</summary>
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
        if (!_tracker.Ready)
            throw RpcErrors.Of(ErrorCode.GameHostDown, $"Can't {action}: the Game Client hasn't loaded in a running Game Host; see 'skua status'.");
        if (_scripts.ScriptRunning)
            throw RpcErrors.Of(ErrorCode.ScriptRunning, $"Can't {action} while a Script runs; stop it first.");
        if (!await _busy.WaitAsync(0))
            throw RpcErrors.Of(ErrorCode.Busy, $"Can't {action}: another login, logout, join or jump is running.");
        return new Lease(_busy);
    }

    private sealed class Lease(SemaphoreSlim busy) : IDisposable
    {
        public void Dispose() => busy.Release();
    }
}
