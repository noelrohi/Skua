using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using Skua.Core.Models.Quests;
using Skua.Engine.Logging;
using Skua.Engine.Scripts;

namespace Skua.Engine.Game;

/// <summary>
/// Watches the loaded quests' requirements while playing, so a Control Surface can tell how long an account has gone without
/// progress: when each requirement's count last rose and how fast it rises, and how long the quests a run farms have gone without
/// progress. A requirement's count is what the player owns of it: the inventory and bank's, or the temporary inventory's for a temporary
/// item. So banking an item is no progress, and a count that falls (a turn-in, or temporary items lost to a relogin) is none either. A
/// quest progresses when it is accepted, when one of its requirements rises while it is, and when it is turned in. It records
/// <see cref="EventTypes.QuestStalled"/> once the quests a run farms have gone without progress for the stall time, and accepts the quests
/// it lists again; and accepts again, within seconds, a quest whose accept the game server refused.
/// </summary>
/// <remarks>What it remembers is the account's: another account logging in starts it afresh.</remarks>
internal sealed class QuestProgress : IDisposable
{
    /// <summary>How often it reads the quests while playing: 5000 ms unless set.</summary>
    public const string SampleVariable = "SKUA_QUEST_SAMPLE_MS";

    /// <summary>How long the quests a run farms go without progress before <see cref="EventTypes.QuestStalled"/>: 600 s unless set.</summary>
    public const string StallVariable = "SKUA_QUEST_STALL_SEC";

    /// <summary>
    /// Whether the Engine accepts the quests <see cref="EventTypes.QuestStalled"/> lists again, and those whose accept the game server
    /// refused: on unless set; 0 turns it off.
    /// </summary>
    public const string ReacceptVariable = "SKUA_QUEST_STALL_REACCEPT";

    /// <summary>The window <see cref="QuestRequirementDto.GainPerHour"/> counts over, and how long it watches before it says.</summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan RateMinimum = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long the player's quest packets pause before the Engine sends an accept: longer than a Script takes from a turn-in to its own
    /// accept (about 1.4 s), plus the game server's spacing of the player's actions (about 0.5 s), which refuses the later of two.
    /// </summary>
    private static readonly TimeSpan QuietFor = TimeSpan.FromSeconds(3);

    /// <summary>How long the Engine waits for that pause before it gives up on its accepts.</summary>
    private static readonly TimeSpan QuietWait = TimeSpan.FromSeconds(30);

    /// <summary>How long a refused accept stays unanswered before the Engine takes it as refused.</summary>
    private static readonly TimeSpan RefusalSettled = TimeSpan.FromSeconds(1);

    private readonly IScriptInterface _api;
    private readonly GameStateTracker _tracker;
    private readonly QuestTurnIns _turnIns;
    private readonly QuestTraffic _traffic;
    private readonly ScriptRuns _runs;
    private readonly EngineLogs _logs;
    private readonly CombatTally _tally;
    private readonly ScriptGoal _goal;
    private readonly TimeSpan _stallAfter;
    private readonly bool _reacceptOnStall;
    private readonly Timer _poll;
    private readonly object _lock = new();
    private readonly Dictionary<(int Id, bool Temp), Watch> _watches = [];

    /// <summary>When each quest last progressed.</summary>
    private readonly Dictionary<int, DateTime> _progressed = [];

    /// <summary>Whether each quest was accepted when last read.</summary>
    private readonly Dictionary<int, bool> _wasActive = [];

    /// <summary>The newest of <see cref="_progressed"/>, or null before any quest has.</summary>
    private DateTime? _newestProgress;

    /// <summary>The quests the run farms, as last read.</summary>
    private HashSet<int> _farmed = [];
    private int _polling;
    private string? _account;

    /// <summary>Whether this login's bank has arrived: the game has none until a Script or <c>inventory</c> loads it.</summary>
    private bool _bankSeen;

    /// <summary>When a quest last progressed, or null when the run farms none.</summary>
    private DateTime? _questsIdleSince;

    /// <summary>The run and idle start <see cref="EventTypes.QuestStalled"/> was last recorded for, so it is recorded once per stall.</summary>
    private (int Run, DateTime Since)? _recordedStall;

    public QuestProgress(IScriptInterface api, GameStateTracker tracker, QuestTurnIns turnIns, QuestTraffic traffic, ScriptRuns runs, EngineLogs logs, CombatTally tally,
        ScriptGoal goal)
    {
        _turnIns = turnIns;
        _traffic = traffic;
        _goal = goal;
        _tally = tally;
        _api = api;
        _tracker = tracker;
        _runs = runs;
        _logs = logs;
        _stallAfter = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable(StallVariable), out int sec) && sec > 0 ? sec : 600);
        _reacceptOnStall = Environment.GetEnvironmentVariable(ReacceptVariable) != "0";
        TimeSpan interval = TimeSpan.FromMilliseconds(
            int.TryParse(Environment.GetEnvironmentVariable(SampleVariable), out int ms) && ms > 0 ? ms : 5_000);
        _poll = new Timer(_ => Poll(), null, interval, interval);
        // Each login starts with no bank again.
        tracker.Playing += () =>
        {
            lock (_lock)
                _bankSeen = false;
        };
        turnIns.Completed += id =>
        {
            lock (_lock)
                Progressed(id, DateTime.UtcNow);
        };
    }

    /// <summary>
    /// The quests as <c>quests</c> lists them, read now, with what it has watched of each requirement, a repeating quest's completion and
    /// each quest's last refused turn-in.
    /// </summary>
    public List<QuestDto> Read(QuestFilter filter)
    {
        (List<Quest> quests, Stores stores) = ReadGame();
        List<Quest> listed = filter == QuestFilter.Active ? quests.FindAll(q => q.Active) : quests;
        Dictionary<int, bool?> repeatDone = listed.Where(q => RepeatOf(q) is not null).ToDictionary(q => q.ID, RepeatDone);
        Dictionary<int, QuestRejectionDto> rejections = _turnIns.Rejections();
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            Observe(quests, stores, now);
            return listed
                .Select(q => ToDto(q, stores, now) with { Repeat = RepeatOf(q), RepeatDone = repeatDone.GetValueOrDefault(q.ID), LastRejection = rejections.GetValueOrDefault(q.ID) })
                .ToList();
        }
    }

    /// <summary>How often the quest repeats, from its achievement field as the game reads it: <c>iw…</c> weekly, <c>im…</c> monthly, any other daily.</summary>
    private static QuestRepeat? RepeatOf(Quest quest) => quest.Field switch
    {
        null or "" => null,
        ['i', 'w', ..] => QuestRepeat.Weekly,
        ['i', 'm', ..] => QuestRepeat.Monthly,
        _ => QuestRepeat.Daily,
    };

    /// <summary>
    /// Whether the repeating quest's achievement bit is set, as Core's <c>IsDailyComplete</c> reads it; null when the game has no such field
    /// for the player, where its <c>getAchievement</c> answers -1.
    /// </summary>
    private bool? RepeatDone(Quest quest) => _api.Flash.CallGameFunction<int>("world.getAchievement", quest.Field, quest.Index) switch
    {
        < 0 => null,
        var bit => bit > 0,
    };

    /// <summary><paramref name="run"/> with how long the quests it farms have gone without progress, at most as long as it has run.</summary>
    public ScriptRunDto? WithQuestIdle(ScriptRunDto? run)
    {
        if (run is null)
            return null;
        lock (_lock)
            return run with { QuestIdleSec = IdleSec(run, DateTime.UtcNow) };
    }

    public void Dispose() => _poll.Dispose();

    private void Poll()
    {
        // A slow Game Host can hold a read past the next poll.
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;
        try
        {
            if (_tracker.State != GameState.Playing)
                return;
            (List<Quest> quests, Stores stores) = ReadGame();
            // A run still compiling hasn't started: its clock restarts as it does, so it would stall on time it never ran.
            ScriptStatusDto status = _runs.Status();
            ScriptRunDto? run = status.State == ScriptState.Compiling ? null : _tally.WithTally(status.Run);
            Stall? stall;
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;
                Observe(quests, stores, now);
                stall = run is null ? null : Stalled(run, quests, stores, now);
            }
            if (stall is not null)
            {
                _logs.Event(EventTypes.QuestStalled, stall.Data);
                Reaccept(stall.ReacceptIds, "the stalled quest");
            }
            // A quest the client shows not accepted, the Script accepts again itself.
            List<int> refused = _traffic.TakeRefused(RefusalSettled);
            if (run is not null && _reacceptOnStall)
                Reaccept(refused.Where(id => quests.Exists(q => q.ID == id && q.Active)).ToArray(), "the refused quest");
        }
        catch (Exception e)
        {
            EngineLog.Write($"Couldn't read the quests' progress: {e.Message}");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private (List<Quest> Quests, Stores Stores) ReadGame()
    {
        string? account = _api.Player.Username;
        lock (_lock)
        {
            if (account is not null && !string.Equals(account, _account, StringComparison.OrdinalIgnoreCase))
            {
                _account = account;
                _watches.Clear();
                _progressed.Clear();
                _wasActive.Clear();
                _newestProgress = null;
                _questsIdleSince = null;
                _recordedStall = null;
                _bankSeen = false;
            }
        }
        List<InventoryItem> inventory = _api.Inventory.Items;
        List<ItemBase> temp = _api.TempInv.Items;
        List<InventoryItem> bank = _api.Bank.Items;
        // What the player owns by name, for the Script's goal, which names its items.
        _goal.Sample(inventory.Cast<ItemBase>().Concat(temp).Concat(bank)
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity), StringComparer.OrdinalIgnoreCase));
        return (_api.Quests.Tree, new Stores(Quantities(inventory), Quantities(temp), Quantities(bank)));
    }

    private void Observe(List<Quest> quests, Stores stores, DateTime now)
    {
        // The bank arriving after the login is no rise: what it holds was owned already.
        bool bankArrived = !_bankSeen && stores.Bank.Count > 0;
        _bankSeen |= bankArrived;
        foreach (ItemBase requirement in quests.SelectMany(q => q.Requirements))
        {
            int owned = stores.Owned(requirement);
            if (!_watches.TryGetValue((requirement.ID, requirement.Temp), out Watch? watch))
                _watches[(requirement.ID, requirement.Temp)] = new Watch(owned, now);
            else if (bankArrived)
                watch.Rebase(owned);
            else
                watch.Saw(owned, now);
        }
        foreach (Quest quest in quests)
        {
            // Seen accepted for the first time, or again.
            if (quest.Active && !_wasActive.GetValueOrDefault(quest.ID))
                Progressed(quest.ID, now);
            // The rise that meets a requirement counts too, as most 1/1 drops do at once.
            if (quest.Active)
                foreach (ItemBase requirement in quest.Requirements)
                    Progressed(quest.ID, _watches[(requirement.ID, requirement.Temp)].IdleSince);
            _wasActive[quest.ID] = quest.Active;
        }
        // The accepted quests with a requirement unmet, but not those left behind, as a Script leaves an earlier step's quest accepted when it
        // moves on: another quest has progressed a whole stall time after it last did. Once it has left all of them behind and progresses on no
        // other, it farms them all again, as a rare drop's quest farmed beside another is left behind until it drops.
        List<int> unmet = quests.Where(q => q.Active && q.Requirements.Any(r => stores.Owned(r) < r.Quantity)).Select(q => q.ID).ToList();
        List<int> farmed = unmet.FindAll(id => _newestProgress - _progressed[id] < _stallAfter);
        _farmed = [.. farmed.Count > 0 ? farmed : unmet];
        // A turned-in quest's progress counts until the Script accepts it again, so the moment between is no stall.
        _questsIdleSince = _farmed.Count > 0 ? _newestProgress : null;
    }

    /// <summary>Records that the quest progressed at <paramref name="at"/>, unless it has since; call it under the lock.</summary>
    private void Progressed(int id, DateTime at)
    {
        if (_progressed.TryGetValue(id, out DateTime last) && last >= at)
            return;
        _progressed[id] = at;
        if (_newestProgress is not { } newest || at > newest)
            _newestProgress = at;
    }

    /// <summary>
    /// Accepts the stalled quests, or those whose accept the game server refused, again (#224, #236). The game server can drop a quest the
    /// client still shows accepted, and stop counting its requirements, which a Script that accepts a quest only once the client shows it
    /// not in progress never notices; an accept of a quest the server still has changes nothing. Each accept waits for the player's quest
    /// packets to pause, so the game server refuses neither it nor a Script's. It stops when the player isn't playing, as after a logout.
    /// </summary>
    /// <param name="what">The quests, as the Engine log names one: <c>the stalled quest</c>.</param>
    private void Reaccept(int[] questIds, string what)
    {
        List<string> accepted = [];
        try
        {
            foreach (int id in questIds)
            {
                if (!WaitForQuiet())
                {
                    EngineLog.Write($"Didn't accept {what} {id} again: the player's quest packets didn't pause for {QuietFor.TotalSeconds:0} s within {QuietWait.TotalSeconds:0} s.");
                    break;
                }
                if (_tracker.State != GameState.Playing)
                {
                    EngineLog.Write($"Didn't accept {what} {id} again: not playing.");
                    break;
                }
                accepted.Add($"{id} {(_api.Quests.Accept(id) ? "in progress" : "not in progress")}");
            }
        }
        catch (Exception e)
        {
            EngineLog.Write($"Couldn't accept {what}s again: {e.Message}");
        }
        if (accepted.Count > 0)
            EngineLog.Write($"Accepted {what}s again: {string.Join(", ", accepted)}.");
    }

    /// <summary>Waits until the player has sent no quest packet for <see cref="QuietFor"/>; false if that can't be within <see cref="QuietWait"/>.</summary>
    private bool WaitForQuiet()
    {
        for (DateTime giveUp = DateTime.UtcNow + QuietWait; ;)
        {
            TimeSpan left = _traffic.LastSent + QuietFor - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
                return true;
            if (DateTime.UtcNow + left > giveUp)
                return false;
            Thread.Sleep(left);
        }
    }

    /// <summary>
    /// The <see cref="EventTypes.QuestStalled"/> event's data when a run's quests have just stalled, with the quests to accept again, else
    /// null.
    /// </summary>
    private Stall? Stalled(ScriptRunDto run, List<Quest> quests, Stores stores, DateTime now)
    {
        if (_questsIdleSince is not { } since || IdleSec(run, now) is not { } idleSec || idleSec < _stallAfter.TotalSeconds
            || _recordedStall == (run.Number, since))
            return null;
        _recordedStall = (run.Number, since);
        var stalledQuests = quests
            .Where(q => _farmed.Contains(q.ID))
            .Select(q => new
            {
                id = q.ID,
                name = q.Name,
                requirements = q.Requirements
                    .Where(r => stores.Owned(r) < r.Quantity)
                    .Select(r => new { itemId = r.ID, name = r.Name, have = stores.Have(r), inBank = stores.InBank(r), qty = r.Quantity })
                    .ToList(),
            })
            .ToList();
        int[] reacceptIds = _reacceptOnStall ? stalledQuests.Select(q => q.id).ToArray() : [];
        return new Stall(new
        {
            run = run.Number,
            script = run.Script,
            idleSec,
            killsPerMin = run.KillsPerMin,
            quests = stalledQuests,
            reaccepted = reacceptIds,
        }, reacceptIds);
    }

    private double? IdleSec(ScriptRunDto run, DateTime now) =>
        _questsIdleSince is { } since ? Math.Round(Math.Min((now - since).TotalSeconds, run.ElapsedSec), 1) : null;

    private QuestDto ToDto(Quest quest, Stores stores, DateTime now)
    {
        QuestStatus status = quest.Status switch
        {
            null => QuestStatus.NotAccepted,
            "c" => QuestStatus.Completable,
            _ => QuestStatus.InProgress,
        };
        return new QuestDto(quest.ID, quest.Name, status, quest.Upgrade, quest.Gold, quest.XP,
            quest.Requirements.Select(r =>
            {
                Watch watch = _watches[(r.ID, r.Temp)];
                return new QuestRequirementDto(r.ID, r.Name, r.Quantity, stores.Have(r), r.Temp,
                    Math.Round((now - watch.IdleSince).TotalSeconds, 1), watch.GainPerHour(now), stores.InBank(r));
            }).ToList(),
            quest.Rewards.Select(r => new QuestRewardDto(r.ID, r.Name, r.Quantity)).ToList());
    }

    /// <summary>A <see cref="EventTypes.QuestStalled"/> event's data, and the quests it says the Engine accepts again.</summary>
    private sealed record Stall(object Data, int[] ReacceptIds);

    /// <summary>What the player holds, by item ID: the bank's only once the game has loaded it.</summary>
    private sealed record Stores(Dictionary<int, int> Inventory, Dictionary<int, int> Temp, Dictionary<int, int> Bank)
    {
        /// <summary>What counts toward the turn-in: the inventory's, or the temporary inventory's for a temporary item.</summary>
        public int Have(ItemBase requirement) => (requirement.Temp ? Temp : Inventory).GetValueOrDefault(requirement.ID);

        /// <summary>The bank's, which a Script takes out for the turn-in; temporary items are never banked.</summary>
        public int InBank(ItemBase requirement) => requirement.Temp ? 0 : Bank.GetValueOrDefault(requirement.ID);

        public int Owned(ItemBase requirement) => Have(requirement) + InBank(requirement);
    }

    private static Dictionary<int, int> Quantities(IEnumerable<ItemBase> items) =>
        items.GroupBy(i => i.ID).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

    /// <summary>One requirement's count as the Engine has seen it.</summary>
    private sealed class Watch(int have, DateTime now)
    {
        private readonly DateTime _since = now;
        private readonly Queue<(DateTime At, int Gain)> _gains = new();
        private int _have = have;

        /// <summary>When the count last rose, or when the watch began if it hasn't.</summary>
        public DateTime IdleSince { get; private set; } = now;

        /// <summary>Takes <paramref name="have"/> as the count without calling it a rise or a fall.</summary>
        public void Rebase(int have) => _have = have;

        public void Saw(int have, DateTime now)
        {
            if (have > _have)
            {
                IdleSince = now;
                _gains.Enqueue((now, have - _have));
            }
            _have = have;
            while (_gains.TryPeek(out var gain) && now - gain.At > RateWindow)
                _gains.Dequeue();
        }

        /// <summary>How much the count rose per hour over the last hour watched; null until it has watched for <see cref="RateMinimum"/>.</summary>
        public double? GainPerHour(DateTime now)
        {
            TimeSpan watched = now - _since;
            if (watched < RateMinimum)
                return null;
            TimeSpan window = watched < RateWindow ? watched : RateWindow;
            int gained = _gains.Where(g => now - g.At <= window).Sum(g => g.Gain);
            return Math.Round(gained / window.TotalHours, 1);
        }
    }
}
