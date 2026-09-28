using Skua.Control;

namespace Skua.Engine;

/// <summary>
/// The one slot that serializes the Engine's state-changing commands (login, logout, script_options and a Script's start):
/// a second one while the slot is taken fails with <see cref="ErrorCode.Busy"/>.
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
