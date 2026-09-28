using Skua.Control;

namespace Skua.Engine;

/// <summary>
/// A slot that serializes a set of the Engine's commands: a second one while the slot is taken fails with <see cref="ErrorCode.Busy"/>.
/// The Engine has two. Its action slot serializes the state-changing commands (login, logout, join, jump, script_options and a Script's
/// start); its Scripts slot keeps a Scripts update or a change of Script Source apart from a Script's compile (script_options and a start),
/// so a login never waits for an update and a Script never compiles against a half-finished one.
/// </summary>
internal sealed class ActionSlot
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private volatile string _holder = "";

    /// <param name="action">What the caller does, e.g. "log in", for both its own refusal and a later caller's.</param>
    /// <exception cref="StreamJsonRpc.LocalRpcException"><see cref="ErrorCode.Busy"/> while another command holds the slot.</exception>
    public IDisposable Take(string action)
    {
        if (!_slot.Wait(0))
            throw RpcErrors.Of(ErrorCode.Busy, $"Can't {action}: the Engine is busy with another command ({_holder}); try again when it's done.");
        _holder = action;
        return new Lease(_slot);
    }

    private sealed class Lease(SemaphoreSlim slot) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                slot.Release();
        }
    }
}
