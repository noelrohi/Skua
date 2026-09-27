using System.Diagnostics;
using System.Text.RegularExpressions;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.App.Engine.Game;

/// <summary>
/// <c>join</c> and <c>jump</c>: Core's transfer packet and jump, with the Engine's own waits, so a call fails with a reason and on time
/// where Core's <c>Join</c> returns silently and retries on its own schedule.
/// </summary>
internal sealed partial class MoveOperations
{
    public static readonly TimeSpan DefaultJoinTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultJumpTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a transfer may take before it is sent again, as Core's <c>Join</c> waits for each try.</summary>
    private static readonly TimeSpan TransferRetry = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan WaitStep = TimeSpan.FromMilliseconds(100);

    private const string DefaultCell = "Enter";
    private const string DefaultPad = "Spawn";

    private readonly IScriptMap _map;
    private readonly IScriptPlayer _player;
    private readonly IScriptWait _wait;
    private readonly GameStateTracker _tracker;
    private readonly GameActionSlot _slot;

    public MoveOperations(IScriptMap map, IScriptPlayer player, IScriptWait wait, GameStateTracker tracker, GameActionSlot slot)
    {
        _map = map;
        _player = player;
        _wait = wait;
        _tracker = tracker;
        _slot = slot;
    }

    public async Task<LocationResult> JoinAsync(string map, string? cell, string? pad, int? timeoutSec, CancellationToken cancellationToken)
    {
        if (map is null || !MapName().IsMatch(map))
            throw RpcErrors.Of(ErrorCode.InvalidArgument,
                $"'{map}' isn't a map name: letters, digits and underscores, optionally with a room number, e.g. battleon or battleon-1234.");
        CheckPlace("cell", cell);
        CheckPlace("pad", pad);
        TimeSpan timeout = Timeout(timeoutSec, DefaultJoinTimeout);

        using IDisposable lease = await _slot.BeginAsync("join a map");
        _slot.EnsurePlaying("join a map");
        return await Task.Run(() => Join(map.ToLowerInvariant(), cell, pad, timeout, cancellationToken), cancellationToken);
    }

    public async Task<LocationResult> JumpAsync(string cell, string? pad, int? timeoutSec, CancellationToken cancellationToken)
    {
        if (cell is null)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, "A cell is required; 'skua map' lists the map's cells.");
        CheckPlace("cell", cell);
        CheckPlace("pad", pad);
        TimeSpan timeout = Timeout(timeoutSec, DefaultJumpTimeout);

        using IDisposable lease = await _slot.BeginAsync("jump");
        _slot.EnsurePlaying("jump");
        return await Task.Run(() => Jump(cell, pad, Stopwatch.StartNew(), timeout, moved: false, cancellationToken), cancellationToken);
    }

    private LocationResult Join(string map, string? cell, string? pad, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Stopwatch waited = Stopwatch.StartNew();
        string name = map.Split('-')[0];
        // A room number can't be compared with the current room, so it always transfers.
        bool moved = map.Contains('-') || !string.Equals(_map.Name, name, StringComparison.OrdinalIgnoreCase);
        if (moved)
            Transfer(map, name, cell ?? DefaultCell, pad ?? DefaultPad, waited, timeout, cancellationToken);
        return cell is null ? Location(alreadyThere: !moved) : Jump(cell, pad, waited, timeout, moved, cancellationToken);
    }

    /// <summary>Sends Core's transfer packet once the game allows it, and again after <see cref="TransferRetry"/>, until the new room has loaded.</summary>
    private void Transfer(string map, string name, string cell, string pad, Stopwatch waited, TimeSpan timeout, CancellationToken cancellationToken)
    {
        int room = _map.RoomID;
        while (true)
        {
            // The game ignores a transfer sent during its cooldown.
            while (!_wait.IsActionAvailable(GameActions.Transfer))
                Step(Late, waited, timeout, cancellationToken);
            _map.JoinPacket(map, cell, pad);

            Stopwatch sent = Stopwatch.StartNew();
            while (sent.Elapsed < TransferRetry)
            {
                if (_map.RoomID != room && string.Equals(_map.Name, name, StringComparison.OrdinalIgnoreCase) && _map.Loaded)
                    return;
                Step(Late, waited, timeout, cancellationToken);
            }
        }

        string Late() =>
            $"The player wasn't on {name} after {timeout.TotalSeconds:0} s; still on {_map.Name}. The game ignores a transfer to a map the player may not enter, e.g. a member-only or locked map.";
    }

    /// <summary>Jumps to the cell on the current map unless the player is there; <paramref name="moved"/> says whether a transfer came first.</summary>
    private LocationResult Jump(string cell, string? pad, Stopwatch waited, TimeSpan timeout, bool moved, CancellationToken cancellationToken)
    {
        List<string> cells = _map.Cells;
        // An empty list means the game didn't answer, so the game gets to judge the cell.
        string target = cells.Find(c => string.Equals(c, cell, StringComparison.OrdinalIgnoreCase))
            ?? (cells.Count == 0 ? cell : throw RpcErrors.Of(ErrorCode.InvalidArgument, $"{_map.Name} has no cell '{cell}'; its cells are {string.Join(", ", cells)}."));
        if (There(target, pad))
            return Location(alreadyThere: !moved);

        _map.Jump(target, pad ?? DefaultPad);
        while (!There(target, pad ?? DefaultPad))
            Step(() => $"The player wasn't in {target} ({pad ?? DefaultPad}) after {timeout.TotalSeconds:0} s; still in {_player.Cell} ({_player.Pad}).",
                waited, timeout, cancellationToken);
        return Location(alreadyThere: false);
    }

    /// <summary>Whether the player is in the cell, and on the pad when one is given.</summary>
    private bool There(string cell, string? pad) =>
        string.Equals(_player.Cell, cell, StringComparison.OrdinalIgnoreCase) && (pad is null || string.Equals(_player.Pad, pad, StringComparison.OrdinalIgnoreCase));

    /// <summary>Waits a moment, failing once the player is no longer playing, the time is up (with <paramref name="late"/>'s message), or the call is cancelled.</summary>
    private void Step(Func<string> late, Stopwatch waited, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_tracker.State != GameState.Playing)
            throw RpcErrors.Of(ErrorCode.NotLoggedIn, "The Test Account stopped playing before the player got there; see 'skua status'.");
        if (waited.Elapsed > timeout)
            throw RpcErrors.Of(ErrorCode.Timeout, late());
        cancellationToken.WaitHandle.WaitOne(WaitStep);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private LocationResult Location(bool alreadyThere) => new(_map.Name, _player.Cell, _player.Pad, alreadyThere);

    private static TimeSpan Timeout(int? timeoutSec, TimeSpan byDefault)
    {
        if (timeoutSec < 1)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"timeoutSec must be at least 1, not {timeoutSec}.");
        return timeoutSec is { } seconds ? TimeSpan.FromSeconds(seconds) : byDefault;
    }

    /// <summary>A cell or pad goes into the transfer packet, whose fields '%' separates.</summary>
    private static void CheckPlace(string what, string? value)
    {
        if (value is not null && !Place().IsMatch(value))
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"'{value}' isn't a {what} name: it must be non-empty, without spaces or '%'.");
    }

    [GeneratedRegex("^[A-Za-z0-9_]+(-[A-Za-z0-9]+)?$")]
    private static partial Regex MapName();

    [GeneratedRegex(@"^[^%\s]+$")]
    private static partial Regex Place();
}
