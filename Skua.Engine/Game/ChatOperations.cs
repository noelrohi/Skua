using Skua.Control;
using Skua.Core.Interfaces;

namespace Skua.Engine.Game;

/// <summary><c>chat_send</c>: zone chat and whispers, as the game's own chat sends them.</summary>
internal sealed class ChatOperations
{
    private readonly IScriptSend _send;
    private readonly IScriptMap _map;
    private readonly GameActionSlot _slot;

    public ChatOperations(IScriptSend send, IScriptMap map, GameActionSlot slot)
    {
        _send = send;
        _map = map;
        _slot = slot;
    }

    /// <summary>Takes no slot and never waits for a Script: it doesn't move the player.</summary>
    public Task<ChatSendResult> SendAsync(string text, string? to, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('%') || text.Contains('\n'))
            throw RpcErrors.Of(ErrorCode.InvalidArgument, "A message can't be blank, span lines or contain '%'.");
        if (to is not null && (string.IsNullOrWhiteSpace(to) || to.Contains('%') || to.Contains('\n')))
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"'{to}' isn't a player name.");
        _slot.EnsurePlaying(to is null ? "chat" : "whisper");
        return Task.Run(() =>
        {
            if (to is null)
                _send.Packet($"%xt%zm%message%{_map.RoomID}%{text}%zone%");
            else
                _send.Whisper(to, text);
            return new ChatSendResult(to is null ? "zone" : "whisper", to, text);
        }, cancellationToken);
    }
}
