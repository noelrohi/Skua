using Skua.Control;
using Skua.Core.Interfaces;

namespace Skua.Engine.Game;

/// <summary>
/// Asks the game server again to respawn a player it left dead (#153). The Game Client asks once, when its 10-second countdown ends,
/// and the server ignores a request within 8 seconds of the death. The pinned Ruffle starts a Timer from the previous tick's start, so
/// in a busy Game Host the countdown ends seconds early and the player stays dead. Until the Ruffle fix ships, the Engine asks again.
/// </summary>
/// <remarks>
/// It polls the player rather than waiting for Core's death message, which comes through the Bridge's dispatch thread and can arrive a
/// minute or more late in a busy Game Host.
/// </remarks>
internal sealed class RespawnWatch : IDisposable
{
    /// <summary>How long the player stays dead before the Engine asks for the respawn, and again between its requests: 15000 ms unless set; 0 turns the watch off.</summary>
    public const string AfterVariable = "SKUA_RESPAWN_AFTER_MS";

    private const int MaxRequests = 3;

    private readonly IFlashUtil _flash;
    private readonly IScriptPlayer _player;
    private readonly IScriptMap _map;
    private readonly IScriptSend _send;
    private readonly GameStateTracker _tracker;
    private readonly TimeSpan _after;
    private readonly Timer? _poll;
    private int _polling;
    private DateTime? _deadSince;
    private DateTime _lastRequest;
    private int _requests;

    public RespawnWatch(IFlashUtil flash, IScriptPlayer player, IScriptMap map, IScriptSend send, GameStateTracker tracker)
    {
        _flash = flash;
        _player = player;
        _map = map;
        _send = send;
        _tracker = tracker;
        _after = TimeSpan.FromMilliseconds(
            int.TryParse(Environment.GetEnvironmentVariable(AfterVariable), out int ms) && ms >= 0 ? ms : 15_000);
        if (_after == TimeSpan.Zero)
            return;
        TimeSpan interval = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(5).Ticks, _after.Ticks / 3));
        _poll = new Timer(_ => Poll(), null, interval, interval);
    }

    private void Poll()
    {
        // A slow Game Host can hold a read past the next poll.
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;
        try
        {
            if (_tracker.State != GameState.Playing)
            {
                _deadSince = null;
                return;
            }
            // Null when the read fails, which says nothing either way.
            int? state = _flash.GetGameObject<int?>("world.myAvatar.dataLeaf.intState");
            if (state is null)
                return;
            DateTime now = DateTime.UtcNow;
            if (state != 0)
            {
                _deadSince = null;
                return;
            }
            if (_deadSince is null)
            {
                (_deadSince, _lastRequest, _requests) = (now, now, 0);
                return;
            }
            if (_requests >= MaxRequests || now - _lastRequest < _after)
                return;
            (int room, int player) = (_map.RoomID, _player.ID);
            if (room == 0 || player == 0)
                return;
            _requests++;
            _lastRequest = now;
            EngineLog.Write($"The player has been dead for at least {(now - _deadSince.Value).TotalSeconds:0} s; asking the game server to respawn them.");
            _send.Packet($"%xt%zm%resPlayerTimed%{room}%{player}%");
        }
        catch (Exception e)
        {
            EngineLog.Write($"Couldn't ask the game server to respawn the player: {e.Message}");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    public void Dispose() => _poll?.Dispose();
}
