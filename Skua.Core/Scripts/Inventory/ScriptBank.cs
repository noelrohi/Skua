using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models.Items;

namespace Skua.Core.Scripts;

public partial class ScriptBank : IScriptBank
{
    public ScriptBank(
        Lazy<IFlashUtil> flash,
        Lazy<IScriptSend> send,
        Lazy<IScriptMap> map,
        Lazy<IScriptWait> wait,
        Lazy<IScriptInventory> inventory,
        Lazy<IScriptOption> options,
        Lazy<IScriptManager> manager,
        Lazy<IScriptPlayer> player)
    {
        _lazyFlash = flash;
        _lazySend = send;
        _lazyMap = map;
        _lazyWait = wait;
        _lazyInventory = inventory;
        _lazyOptions = options;
        _lazyManager = manager;
        _lazyPlayer = player;

        StrongReferenceMessenger.Default.Register<ScriptBank, BankLoadedMessage>(this, (r, m) => r.Loaded = true);
        StrongReferenceMessenger.Default.Register<ScriptBank, LogoutMessage>(this, (r, m) => r.Loaded = false);
    }

    private readonly Lazy<IFlashUtil> _lazyFlash;
    private readonly Lazy<IScriptSend> _lazySend;
    private readonly Lazy<IScriptMap> _lazyMap;
    private readonly Lazy<IScriptWait> _lazyWait;
    private readonly Lazy<IScriptInventory> _lazyInventory;
    private readonly Lazy<IScriptOption> _lazyOptions;
    private readonly Lazy<IScriptManager> _lazyManager;
    private readonly Lazy<IScriptPlayer> _lazyPlayer;

    private IFlashUtil Flash => _lazyFlash.Value;
    private IScriptSend Send => _lazySend.Value;
    private IScriptMap Map => _lazyMap.Value;
    private IScriptWait Wait => _lazyWait.Value;
    private IScriptInventory Inventory => _lazyInventory.Value;
    private IScriptOption Options => _lazyOptions.Value;
    private IScriptManager Manager => _lazyManager.Value;
    private IScriptPlayer Player => _lazyPlayer.Value;

    public bool Loaded { get; set; } = false;

    [ObjectBinding("world.bankinfo.items", Default = "new()")]
    private List<InventoryItem> _items = new();

    [ObjectBinding("world.myAvatar.objData.iBankSlots")]
    private int _slots;

    [ObjectBinding("world.myAvatar.iBankCount")]
    private int _usedSlots;

    [MethodCallBinding("world.toggleBank", RunMethodPre = true, GameFunction = true)]
    private void _open()
    {
        if (Flash.GetGameObject("ui.mcPopup.currentLabel") == "Bank")
            return;
    }

    /// <summary>How many times each of <see cref="Load"/>'s waits sleeps for <see cref="IScriptWait.WAIT_SLEEP"/>: 20 s by default.</summary>
    private const int LoadWaitSleeps = 200;

    public void Load(bool waitForLoad = true)
    {
        // Once the bank has arrived the game keeps it current through every transfer, so it isn't asked for again: getBank's answer holds
        // the bank as it was when the request left, and adds an item it lists without removing any, so an answer landing after a transfer
        // lists the item again.
        if (Flash.GetGameObject("ui.mcPopup.currentLabel") == "Bank" || Arrived())
            return;
        // This loads the bank as the game itself does, with getBank over HTTP: the game server no longer answers the loadBank packet.
        // getBank needs the character's data, which the game has once the inventory has loaded.
        bool InventoryLoaded() => Flash.GetGameObject<bool>("world.myAvatar.invLoaded");
        if (waitForLoad ? !Wait.ForTrue(InventoryLoaded, LoadWaitSleeps) : !InventoryLoaded())
            return;
        Flash.CallGameFunction("getBank");
        if (waitForLoad)
            Wait.ForBankLoad(LoadWaitSleeps);
    }

    /// <summary>
    /// Whether the bank holds items, at least the login's bank count of them, as <see cref="IScriptWait.ForBankLoad"/> waits for. That
    /// count leaves out AC items, so a bank of only AC items counts 0; an empty bank is asked for each time, which an answer can't harm.
    /// </summary>
    private bool Arrived() =>
        Flash.GetGameObject<int?>("world.myAvatar.iBankCount") is int count
        && Flash.GetGameObject<int?>("world.bankinfo.BankArray.length") is int held
        && held > 0 && held >= count;

    public bool Swap(string invItem, string bankItem)
    {
        if (((IScriptBank)this).TryGetItem(bankItem, out InventoryItem? bank) && Inventory.TryGetItem(invItem, out InventoryItem? inv))
            Swap(inv!, bank!);
        return false;
    }

    public bool Swap(int invItem, int bankItem)
    {
        return ((IScriptBank)this).TryGetItem(bankItem, out InventoryItem? bank) && Inventory.TryGetItem(invItem, out InventoryItem? inv) && Swap(inv!, bank!);
    }

    public bool Swap(InventoryItem invItem, InventoryItem bankItem)
    {
        Send.Packet($"%xt%zm%bankSwapInv%{Map.RoomID}%{invItem.ID}%{invItem.CharItemID}%{bankItem.ID}%{bankItem.CharItemID}%");
        Wait.ForInventoryToBank(invItem.ID);
        return Inventory.Contains(bankItem.ID);
    }

    public bool EnsureSwap(string invItem, string bankItem, bool loadBank = true)
    {
        if (loadBank)
            Load();
        if (!((IScriptBank)this).TryGetItem(bankItem, out InventoryItem? bank) || !Inventory.TryGetItem(invItem, out InventoryItem? inv))
            return false;
        if (inv is null || bank is null)
            return false;
        int i = 0;
        while (!Swap(inv, bank) && !Manager.ShouldExit && Player.Playing && ++i < Options.MaximumTries)
            Thread.Sleep(Options.ActionDelay);

        return Inventory.Contains(bankItem);
    }

    public bool EnsureSwap(int invItem, int bankItem, bool loadBank = true)
    {
        if (loadBank)
            Load();
        if (!((IScriptBank)this).TryGetItem(bankItem, out InventoryItem? bank) || !Inventory.TryGetItem(invItem, out InventoryItem? inv))
            return false;
        if (inv is null || bank is null)
            return false;
        int i = 0;
        while (!Swap(invItem, bankItem) && !Manager.ShouldExit && Player.Playing && ++i < Options.MaximumTries)
            Thread.Sleep(Options.ActionDelay);

        return Inventory.Contains(invItem);
    }

    public bool ToInventory(InventoryItem item)
    {
        Send.Packet($"%xt%zm%bankToInv%{Map.RoomID}%{item.ID}%{item.CharItemID}%");
        Wait.ForBankToInventory(item!.Name);
        return Inventory.Contains(item.ID);
    }

    public bool EnsureToInventory(string name, bool loadBank = true)
    {
        if (loadBank)
            Load();
        if (!((IScriptBank)this).TryGetItem(name, out InventoryItem? item))
            return false;
        int i = 0;
        while (!((IScriptBank)this).ToInventory(item) && !Manager.ShouldExit && Player.Playing && ++i < Options.MaximumTries)
            Thread.Sleep(Options.ActionDelay);

        return Inventory.Contains(name);
    }

    public bool EnsureToInventory(int id, bool loadBank = true)
    {
        if (loadBank)
            Load();
        if (!((IScriptBank)this).TryGetItem(id, out InventoryItem? item))
            return false;
        int i = 0;
        while (!((IScriptBank)this).ToInventory(item) && !Manager.ShouldExit && Player.Playing && ++i < Options.MaximumTries)
            Thread.Sleep(Options.ActionDelay);

        return Inventory.Contains(id);
    }
}