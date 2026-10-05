using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Engine.Game;

/// <summary><c>quest_complete</c>: a turn-in, as Core's <c>Complete</c> sends it, answered with the game server's reply.</summary>
internal sealed class QuestOperations
{
    private readonly IFlashUtil _flash;
    private readonly IScriptWait _wait;
    private readonly QuestTurnIns _turnIns;
    private readonly GameActionSlot _slot;

    public QuestOperations(IFlashUtil flash, IScriptWait wait, QuestTurnIns turnIns, GameActionSlot slot)
    {
        _flash = flash;
        _wait = wait;
        _turnIns = turnIns;
        _slot = slot;
    }

    public async Task<QuestCompleteResult> CompleteAsync(int id, int? rewardId, int? timeoutSec, CancellationToken cancellationToken)
    {
        if (id < 1)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"'{id}' isn't a quest ID; 'skua quests' lists them.");
        if (timeoutSec < 1)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"timeoutSec must be at least 1, not {timeoutSec}.");
        TimeSpan timeout = timeoutSec is { } seconds ? TimeSpan.FromSeconds(seconds) : QuestTurnIns.DefaultTimeout;

        using IDisposable lease = await _slot.BeginAsync("turn in a quest");
        _slot.EnsurePlaying("turn in a quest");
        Task<QuestCompleteResult> answer = _turnIns.AnswerAsync(id, timeout, cancellationToken);
        await Task.Run(() =>
        {
            _wait.ForActionCooldown(GameActions.TryQuestComplete);
            _flash.CallGameFunction("world.tryQuestComplete", id, rewardId ?? -1, false);
        }, cancellationToken);
        return await answer;
    }
}
