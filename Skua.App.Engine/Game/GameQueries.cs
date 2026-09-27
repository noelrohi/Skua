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

    private readonly IScriptInterface _bot;
    private readonly IFlashUtil _flash;
    private readonly GameStateTracker _tracker;
    private readonly SemaphoreSlim _bankLoad = new(1, 1);
    private volatile bool _bankLoaded;
    private volatile TaskCompletionSource? _bankArrived;

    public GameQueries(IScriptInterface bot, IFlashUtil flash, GameStateTracker tracker)
    {
        _bot = bot;
        _flash = flash;
        _tracker = tracker;
        // Each login starts a new world, whose bank the game server hasn't sent yet.
        tracker.Playing += () => _bankLoaded = false;
        StrongReferenceMessenger.Default.Register<GameQueries, BankLoadedMessage, int>(this, (int)MessageChannels.GameEvents, static (r, _) => r._bankArrived?.TrySetResult());
    }

    public async Task<InventoryResult> InventoryAsync(InventoryKind kind, CancellationToken cancellationToken)
    {
        EnsurePlaying("list the items");
        if (kind == InventoryKind.Bank)
            await LoadBankAsync(cancellationToken);
        return await Task.Run(() => kind switch
        {
            InventoryKind.Bank => new InventoryResult(kind, _bot.Bank.UsedSlots, _bot.Bank.Slots, _bot.Bank.Items.Select(ToDto).ToList()),
            InventoryKind.Temp => new InventoryResult(kind, _bot.TempInv.Items.Count, null, _bot.TempInv.Items.Select(ToTempDto).ToList()),
            InventoryKind.House => new InventoryResult(kind, _bot.House.UsedSlots, _bot.House.Slots, _bot.House.Items.Select(ToDto).ToList()),
            _ => new InventoryResult(kind, _bot.Inventory.UsedSlots, _bot.Inventory.Slots, _bot.Inventory.Items.Select(ToDto).ToList()),
        }, cancellationToken);
    }

    public Task<QuestsResult> QuestsAsync(QuestFilter filter, CancellationToken cancellationToken)
    {
        EnsurePlaying("list the quests");
        return Task.Run(() =>
        {
            List<Quest> quests = _bot.Quests.Tree;
            if (filter == QuestFilter.Active)
                quests = quests.FindAll(q => q.Active);
            // What the player has counts toward a requirement from the inventory, or the temporary inventory for a temporary item.
            Dictionary<int, int> inventory = Quantities(_bot.Inventory.Items);
            Dictionary<int, int> temp = Quantities(_bot.TempInv.Items);
            return new QuestsResult(filter, quests.Select(q => ToDto(q, inventory, temp)).ToList());
        }, cancellationToken);
    }

    public Task<MapDto> MapAsync(CancellationToken cancellationToken)
    {
        EnsurePlaying("read the map");
        return Task.Run(() => new MapDto(
            _bot.Map.Name,
            _bot.Map.RoomID,
            _bot.Map.Cells,
            // Core's Map.Players reads a field its binding never fills, so this reads the same game object itself.
            (_flash.GetGameObject<Dictionary<string, PlayerInfo>>("world.uoTree") ?? []).Values.Select(p => new MapPlayerDto(p.Name ?? "", p.Level, p.Cell ?? "", p.Pad ?? "", p.HP, p.MaxHP, p.AFK)).ToList(),
            _bot.Monsters.MapMonsters.Select(m => new MonsterDto(m.ID, m.MapID, m.Name ?? "", m.Cell ?? "", m.HP, m.MaxHP, m.Alive)).ToList()),
            cancellationToken);
    }

    public Task<DropsResult> DropsAsync(CancellationToken cancellationToken)
    {
        EnsurePlaying("list the drops");
        return Task.FromResult(new DropsResult(_bot.Drops.CurrentDropInfos.Select(d => new DropDto(d.ID, d.Name.Trim(), d.Quantity)).ToList()));
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
        IScriptPlayer player = _bot.Player;
        int state = player.State;
        // Not Core's CurrentClass, which is null while the player is dead.
        string? playerClass = _bot.Inventory.Items.Find(i => i is { Equipped: true, Category: ItemCategory.Class })?.Name;
        return new PlayerDto(player.Username ?? "", player.Level, playerClass, player.Health, player.MaxHealth, player.Mana, player.MaxMana, player.Gold,
            _bot.Map.Name, player.Cell, player.Pad, Alive: state > 0, InCombat: state == 2);
    }

    /// <summary>
    /// Asks the game server for the bank once per login, since the game has none until it does, and waits for it. Core's own
    /// <c>Load</c> doesn't ask while the bank is open in the game, and its wait for the bank never ends early.
    /// </summary>
    private async Task LoadBankAsync(CancellationToken cancellationToken)
    {
        if (_bankLoaded)
            return;
        await _bankLoad.WaitAsync(cancellationToken);
        try
        {
            if (_bankLoaded)
                return;
            TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _bankArrived = arrived;
            await Task.Run(() => _bot.Send.Packet($"%xt%zm%loadBank%{_bot.Map.RoomID}%All%"), cancellationToken);
            try
            {
                await arrived.Task.WaitAsync(BankLoadTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw RpcErrors.Of(ErrorCode.Timeout, $"The game server didn't send the bank within {BankLoadTimeout.TotalSeconds:0} s; try again.");
            }
            _bankLoaded = true;
        }
        finally
        {
            _bankArrived = null;
            _bankLoad.Release();
        }
    }

    private void EnsurePlaying(string action)
    {
        if (!_tracker.Ready)
            throw RpcErrors.Of(ErrorCode.GameHostDown, $"Can't {action}: the Game Client hasn't loaded in a running Game Host; see 'skua status'.");
        if (_tracker.State != GameState.Playing)
            throw RpcErrors.Of(ErrorCode.NotLoggedIn, $"Can't {action}: the Test Account isn't playing; run 'skua login' first.");
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
