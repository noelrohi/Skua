using Skua.Engine.Scripts;
using Skua.Control;

namespace Skua.Engine.Game;

/// <summary>
/// A game action (a login, logout, join or jump) takes the Engine's slot, so it never overlaps another state change, and none runs beside a
/// Script; and the checks that the game can be driven, which the queries share.
/// </summary>
internal sealed class GameActionSlot
{
    private readonly GameStateTracker _tracker;
    private readonly ScriptRuns _runs;
    private readonly ActionSlot _slot;

    public GameActionSlot(GameStateTracker tracker, ScriptRuns runs, ActionSlot slot)
    {
        _tracker = tracker;
        _runs = runs;
        _slot = slot;
    }

    /// <summary>Checks that the game can be driven now, and takes the slot until the lease is disposed.</summary>
    /// <param name="action">What the caller does, for the error messages, e.g. <c>log in</c>.</param>
    public Task<IDisposable> BeginAsync(string action)
    {
        EnsureReady(action);
        _runs.EnsureIdle(action);
        return Task.FromResult(_slot.Take(action));
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
}
