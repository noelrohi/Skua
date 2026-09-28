using CommunityToolkit.Mvvm.Messaging;
using Skua.Engine.Logging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;

namespace Skua.Engine;

/// <summary>
/// Tells a host when what <c>status</c> reports may have changed, so a status view refreshes on game events instead of polling: each event
/// the Engine records (the game state, a map join, a Script starting or stopping, …), and the moves between cells and the level-ups, which
/// record none.
/// </summary>
/// <remarks>
/// <see cref="Changed"/> is raised on the thread that saw the change, which may hold a lock of its own, so handlers only schedule work.
/// A spurious raise costs a handler one extra read of <c>status</c>.
/// </remarks>
internal sealed class StatusChanges
{
    private StatusChanges()
    {
    }

    public event Action? Changed;

    public static StatusChanges Start(EngineLogs logs, IFlashUtil flash)
    {
        StatusChanges changes = new();
        logs.EventRecorded += changes.Raise;
        StrongReferenceMessenger.Default.Register<StatusChanges, CellChangedMessage, int>(
            changes, (int)MessageChannels.GameEvents, static (c, _) => c.Raise());
        // The game's levelUp extension packet: {"cmd":"levelUp","intLevel":…}. Matching the name alone is enough for a hint.
        flash.FlashCall += (function, args) =>
        {
            if (function == "pext" && args is [string packet] && packet.Contains("\"levelUp\"", StringComparison.Ordinal))
                changes.Raise();
        };
        return changes;
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            // A host's handler mustn't break the game-state tracking or logging that raised it.
            EngineLog.Write($"A status-change handler failed: {e}");
        }
    }
}
