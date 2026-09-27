using CommunityToolkit.Mvvm.Messaging;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models.Items;
using Skua.Core.Models.Players;
using Skua.Core.Models.Quests;

namespace Skua.App.Engine.Game;

/// <summary>
/// The typed state queries and the <c>status</c> player summary: they read the game through Core's Script API, as a Script would, and map
/// Core's models by hand onto the Control Surface DTOs, so the game's wire names never cross the wire.
/// </summary>
internal sealed class GameQueries
{
    private static readonly TimeSpan BankLoadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long <c>status</c> waits for the player summary before leaving it out, since <c>status</c> never fails.</summary>
    private static readonly TimeSpan PlayerTimeout = TimeSpan.FromSeconds(3);

    private readonly IScriptInterface _api;
    private readonly IFlashUtil _flash;
    private readonly GameStateTracker _tracker;
    private readonly GameActionSlot _slot;
    private readonly DropTracker _drops;
    private readonly SemaphoreSlim _bankLoad = new(1, 1);

    /// <summary>Counts the worlds the game has entered; one per login or relogin.</summary>
    private int _world;

    /// <summary>The world whose bank the game server has sent, or 0 for none; worlds count from 1.</summary>
    private int _bankWorld;

    private volatile TaskCompletionSource? _bankArrived;

    public GameQueries(IScriptInterface api, IFlashUtil flash, GameStateTracker tracker, GameActionSlot slot)
    {
        _api = api;
        _flash = flash;
        _tracker = tracker;
        _slot = slot;
        _drops = new DropTracker(flash, tracker);
        // Each login starts a new world, whose bank the game server hasn't sent yet.
        tracker.Playing += () => Interlocked.Increment(ref _world);
        StrongReferenceMessenger.Default.Register<GameQueries, BankLoadedMessage, int>(this, (int)MessageChannels.GameEvents, static (r, _) => r._bankArrived?.TrySetResult());
    }

    public async Task<InventoryResult> InventoryAsync(InventoryKind kind, CancellationToken cancellationToken)
    {
        _slot.EnsurePlaying("list the items");
        if (kind == InventoryKind.Bank)
            await LoadBankAsync(cancellationToken);
        return await Task.Run(() => kind switch
        {
            InventoryKind.Bank => new InventoryResult(kind, _api.Bank.UsedSlots, _api.Bank.Slots, _api.Bank.Items.Select(ToDto).ToList()),
            InventoryKind.Temp => new InventoryResult(kind, _api.TempInv.Items.Count, null, _api.TempInv.Items.Select(ToTempDto).ToList()),
            InventoryKind.House => new InventoryResult(kind, _api.House.UsedSlots, _api.House.Slots, _api.House.Items.Select(ToDto).ToList()),
            InventoryKind.Inventory => new InventoryResult(kind, _api.Inventory.UsedSlots, _api.Inventory.Slots, _api.Inventory.Items.Select(ToDto).ToList()),
            _ => throw RpcErrors.Of(ErrorCode.InvalidArgument, $"'{kind}' isn't a kind of item store: inventory, bank, temp or house."),
        }, cancellationToken);
    }

    public Task<QuestsResult> QuestsAsync(QuestFilter filter, CancellationToken cancellationToken)
    {
        _slot.EnsurePlaying("list the quests");
        return Task.Run(() =>
        {
            List<Quest> quests = _api.Quests.Tree;
            if (filter == QuestFilter.Active)
                quests = quests.FindAll(q => q.Active);
            // What the player has counts toward a requirement from the inventory, or the temporary inventory for a temporary item.
            Dictionary<int, int> inventory = Quantities(_api.Inventory.Items);
            Dictionary<int, int> temp = Quantities(_api.TempInv.Items);
            return new QuestsResult(filter, quests.Select(q => ToDto(q, inventory, temp)).ToList());
        }, cancellationToken);
    }

    public Task<MapDto> MapAsync(CancellationToken cancellationToken)
    {
        _slot.EnsurePlaying("read the map");
        return Task.Run(() => new MapDto(
            _api.Map.Name,
            _api.Map.RoomID,
            _api.Map.Cells,
            // Core's Map.Players reads a field its binding never fills, so this reads the same game object itself.
            (_flash.GetGameObject<Dictionary<string, PlayerInfo>>("world.uoTree") ?? []).Values.Select(p => new MapPlayerDto(p.Name ?? "", p.Level, p.Cell ?? "", p.Pad ?? "", p.HP, p.MaxHP, p.AFK)).ToList(),
            _api.Monsters.MapMonsters.Select(m => new MonsterDto(m.ID, m.MapID, m.Name ?? "", m.Cell ?? "", m.HP, m.MaxHP, m.Alive)).ToList()),
            cancellationToken);
    }

    public Task<DropsResult> DropsAsync(CancellationToken cancellationToken)
    {
        _slot.EnsurePlaying("list the drops");
        return Task.FromResult(new DropsResult(_drops.Drops));
    }

    /// <summary>The player while playing, else null; also null when the game doesn't answer in time or the reading fails.</summary>
    public async Task<PlayerDto?> PlayerAsync()
    {
        if (_tracker.State != GameState.Playing)
            return null;
        try
        {
            return await Task.Run(ReadPlayer).WaitAsync(PlayerTimeout);
        }
        catch (Exception e)
        {
            EngineLog.Write($"Couldn't read the player for status: {e.Message}");
            return null;
        }
    }

    private PlayerDto ReadPlayer()
    {
        IScriptPlayer player = _api.Player;
        int state = player.State;
        // The character's name as the game shows it; Core's Username is what was typed on the login screen.
        string? name = _flash.GetGameObject<string>("world.myAvatar.objData.strUsername");
        // Not Core's CurrentClass, which is null while the player is dead.
        string? playerClass = _api.Inventory.Items.Find(i => i is { Equipped: true, Category: ItemCategory.Class })?.Name;
        return new PlayerDto(name ?? player.Username ?? "", player.Level, playerClass, player.Health, player.MaxHealth, player.Mana, player.MaxMana, player.Gold,
            _api.Map.Name, player.Cell, player.Pad, Alive: state > 0, InCombat: state == 2);
    }

    /// <summary>
    /// Asks the game server for the bank once per login, since the game has none until it does, and waits for it. Core's own
    /// <c>Load</c> doesn't ask while the bank is open in the game, and its wait for the bank never ends early.
    /// </summary>
    private async Task LoadBankAsync(CancellationToken cancellationToken)
    {
        int world = Volatile.Read(ref _world);
        if (Volatile.Read(ref _bankWorld) == world)
            return;
        await _bankLoad.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _bankWorld) == world)
                return;
            TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _bankArrived = arrived;
            await Task.Run(() => _api.Send.Packet($"%xt%zm%loadBank%{_api.Map.RoomID}%All%"), cancellationToken);
            try
            {
                await arrived.Task.WaitAsync(BankLoadTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw RpcErrors.Of(ErrorCode.Timeout, $"The game server didn't send the bank within {BankLoadTimeout.TotalSeconds:0} s; try again.");
            }
            // A relogin while it loaded makes a new world, which still needs its bank.
            Volatile.Write(ref _bankWorld, world);
        }
        finally
        {
            _bankArrived = null;
            _bankLoad.Release();
        }
    }


    private static ItemDto ToDto(InventoryItem item) =>
        new(item.ID, item.Name, item.Quantity, item.MaxStack, item.CategoryString ?? "", item.Equipped, item.EnhancementLevel);

    /// <summary>A temporary item, which can't be equipped or enhanced.</summary>
    private static ItemDto ToTempDto(ItemBase item) =>
        new(item.ID, item.Name, item.Quantity, item.MaxStack, item.CategoryString ?? "", Equipped: false, EnhancementLevel: null);

    private static QuestDto ToDto(Quest quest, Dictionary<int, int> inventory, Dictionary<int, int> temp)
    {
        QuestStatus status = quest.Status switch
        {
            null => QuestStatus.NotAccepted,
            "c" => QuestStatus.Completable,
            _ => QuestStatus.InProgress,
        };
        return new QuestDto(quest.ID, quest.Name, status, quest.Upgrade, quest.Gold, quest.XP,
            quest.Requirements.Select(r => new QuestRequirementDto(r.ID, r.Name, r.Quantity, (r.Temp ? temp : inventory).GetValueOrDefault(r.ID), r.Temp)).ToList(),
            quest.Rewards.Select(r => new QuestRewardDto(r.ID, r.Name, r.Quantity)).ToList());
    }

    private static Dictionary<int, int> Quantities(IEnumerable<ItemBase> items) =>
        items.GroupBy(i => i.ID).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
}
