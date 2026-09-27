using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

/// <summary>
/// The AQW game as skua.swf exposes it over the Bridge, simulated just far enough for the Engine to log in, play and lose the connection:
/// the login screen, the account login, connecting to a server, the world, the connection message and the kick warning; and to move and
/// look around: map transfers and jumps, the player, the item stores, the quest tree, the map's players and monsters, and drops.
/// </summary>
/// <remarks>
/// Replies are what skua.swf returns: strings, with game objects as JSON. Anything it doesn't simulate is left to the scenario's replies.
/// </remarks>
internal sealed class FakeGame
{
    private readonly object _lock = new();
    private readonly Action<string> _invoke;
    private readonly Action<string> _note;
    private readonly string _username;
    private readonly string _password;
    private readonly Dictionary<string, string> _rejections = new(StringComparer.OrdinalIgnoreCase);
    private string _servers = "[]";
    private int _connectDelay = 300;
    private int _roomId = 1000;

    private bool _account;
    private string? _loginName;
    private string? _loginPassword;
    private bool _connected;
    private bool _world;
    private bool _kicked;
    private string? _connDetail;
    private string? _server;
    private string _map = "battleon";
    private string _cell = "Enter";
    private string _pad = "Spawn";
    private bool _loading;
    private int _hp = MaxHp;
    private int _state = 1;
    private bool _bankLoaded;
    private readonly HashSet<string> _lockedMaps = new(StringComparer.OrdinalIgnoreCase);
    private bool _brokenLogin;

    private const int MaxHp = 1000;

    /// <summary>Whether the lag killer hides the world, as <c>killLag</c> last set it.</summary>
    public bool LagKilled { get; private set; }

    /// <param name="invoke">Sends one <c>ExternalInterface.call</c> from the Game Client, as invoke XML.</param>
    /// <param name="note">Records what the game did for a test to check, e.g. <c>tfer yulgar Enter Spawn</c>.</param>
    public FakeGame(string username, string password, Action<string> invoke, Action<string> note)
    {
        _username = username;
        _password = password;
        _invoke = invoke;
        _note = note;
    }

    /// <summary>The server list the game shows once the account has logged in, as the servers API's JSON.</summary>
    public void Servers(string json) => _servers = json;

    public void ConnectDelay(int milliseconds) => _connectDelay = milliseconds;

    /// <summary>Connecting to <paramref name="server"/> fails with this connection message, as a full server does.</summary>
    public void Reject(string server, string message) => _rejections[server] = message;

    /// <summary>Answers a call the game simulates, or returns null for the scenario to answer.</summary>
    public string? Answer(string request)
    {
        XElement invoke = XElement.Parse(request);
        string name = invoke.Attribute("name")!.Value;
        string[] args = invoke.Element("arguments")?.Elements().Select(e => e.Value).ToArray() ?? [];
        lock (_lock)
        {
            return (name, args) switch
            {
                ("isLoggedIn", _) => Str(_connected),
                ("isKicked", _) => Str(_kicked),
                ("isNull", [string path]) => Str(Get(path) is null),
                ("getGameObject", [string path]) => Json(Get(path)),
                ("getGameObjectS", [string path]) => Json(GetStatic(path)),
                ("callGameFunction" or "callGameFunction0", [string path, .. string[] rest]) => Call(path, rest),
                ("connectToServer", [string server]) => ConnectToServer(server),
                ("clickServer", [string serverName]) => ClickServer(serverName),
                ("killLag", [string enable]) => KillLag(enable == "true"),
                ("jumpCorrectRoom", [string cell, string pad, ..]) => Jump(cell, pad),
                ("selectArrayObjects", ["world.map.currentScene.labels", "name"]) => _world ? Str(new JsonArray([.. Cells(_map).Select(c => JsonValue.Create(c))]).ToJsonString()) : "<undefined/>",
                ("getMonsters", _) => Str((_world ? Monsters(_map) : []).ToJsonString()),
                _ => null,
            };
        }
    }

    /// <summary>Runs a directive that changes the game while it runs; returns false for one it doesn't know.</summary>
    public bool Run(string line)
    {
        lock (_lock)
        {
            switch (line.Split(' ', 2))
            {
                case ["lose-connection", string message]:
                    _connected = false;
                    _connDetail = message;
                    return true;
                case ["connection-message", string message]:
                    // Only the message: the connection flag may lag behind it.
                    _connDetail = message;
                    return true;
                case ["blip", string ms]:
                    // The connection flag drops for a moment, as a slow poll can read it.
                    _connected = false;
                    Task.Delay(int.Parse(ms)).ContinueWith(_ =>
                    {
                        lock (_lock)
                            _connected = _server is not null;
                    });
                    return true;
                case ["broken-login"]:
                    _brokenLogin = true;
                    return true;
                case ["kick"]:
                    ToLoginScreen();
                    _kicked = true;
                    return true;
                case ["logout-button"]:
                    Packet("%xt%zm%cmd%1%logout%");
                    ToLoginScreen();
                    return true;
                case ["die"]:
                    _hp = 0;
                    _state = 0;
                    Pext(new JsonObject { ["cmd"] = "ct", ["p"] = new JsonObject { [_username.ToLowerInvariant()] = new JsonObject { ["intHP"] = 0 } } });
                    return true;
                case ["afk"]:
                    PextStr(["uotls", "-1", _username, "afk:true"]);
                    return true;
                case ["combat"]:
                    _state = 2;
                    return true;
                case ["join", string map]:
                    Join(map, "Enter", "Spawn");
                    return true;
                case ["lock-map", string map]:
                    // The game ignores a transfer to it, as it does for a map the player may not enter.
                    _lockedMaps.Add(map);
                    return true;
                case ["drop", string rest] when rest.Split(' ', 3) is [string id, string qty, string name]:
                    JsonObject item = Item(int.Parse(id), name, int.Parse(qty), 10, "Item");
                    Pext(new JsonObject { ["cmd"] = "dropItem", ["items"] = new JsonObject { [id] = item } });
                    return true;
                case ["cell", string cell]:
                    _cell = cell;
                    Packet($"%xt%zm%moveToCell%{_roomId}%{cell}%Spawn%");
                    return true;
                default:
                    return false;
            }
        }
    }

    private object? Get(string path) => path switch
    {
        "world" => _world ? new JsonObject() : null,
        "world.myAvatar" => _world ? new JsonObject() : null,
        "sfc" => new JsonObject(),
        "sfc.isConnected" => _connected,
        "mcConnDetail.stage" => _connDetail is null ? null : new JsonObject(),
        "mcConnDetail.txtDetail.text" => _connDetail,
        "mcLogin.warning.visible" => _kicked,
        "mcLogin.visible" => !_connected,
        "mcLogin.sl.iList" => _account ? new JsonObject() : null,
        "mcLogin.sl.iList.numChildren" => _account ? JsonNode.Parse(_servers)!.AsArray().Count : null,
        "serialCmd.servers" => _account ? JsonNode.Parse(_servers) : new JsonArray(),
        "objServerInfo" => _server is null ? null : new JsonObject { ["sName"] = _server },
        "objServerInfo.sName" => _server,
        "world.strFrame" => _world ? _cell : null,
        "world.strPad" => _world ? _pad : null,
        "world.strMapName" => _world ? _map : null,
        "world.mapLoadInProgress" => _world ? _loading : null,
        "world.curRoom" => _world ? _roomId : null,
        "world.lock.tfer" => _world ? new JsonObject { ["cd"] = 3000, ["ts"] = 0 } : null,
        "world.uoTree" => _world ? Players() : null,
        "world.myAvatar.dataLeaf.intState" => _world ? _state : null,
        "world.myAvatar.dataLeaf.intHP" => _world ? _hp : null,
        "world.myAvatar.dataLeaf.intHPMax" => _world ? MaxHp : null,
        "world.myAvatar.dataLeaf.intMPMax" => _world ? 100 : null,
        "world.myAvatar.dataLeaf.intLevel" => _world ? 10 : null,
        "world.myAvatar.objData.intMP" => _world ? 80 : null,
        "world.myAvatar.objData.intGold" => _world ? 5000 : null,
        "world.myAvatar.objData.iUpgDays" => _world ? -1 : null,
        "world.myAvatar.items" => _world ? Inventory() : null,
        "world.myAvatar.items.length" => _world ? Inventory().Count : null,
        "world.myAvatar.objData.iBagSlots" => _world ? 40 : null,
        "world.bankinfo.items" => _world ? (_bankLoaded ? Bank() : []) : null,
        "world.myAvatar.objData.iBankSlots" => _world ? 10 : null,
        "world.myAvatar.iBankCount" => _world ? Bank().Count : null,
        "world.myAvatar.tempitems" => _world ? TempItems() : null,
        "world.myAvatar.houseitems" => _world ? HouseItems() : null,
        "world.myAvatar.houseitems.length" => _world ? HouseItems().Count : null,
        "world.myAvatar.objData.iHouseSlots" => _world ? 20 : null,
        "world.questTree" => _world ? QuestTree() : null,
        _ => null,
    };

    private object? GetStatic(string path) => path switch
    {
        "objLogin" => _account ? new JsonObject { ["iUpgDays"] = -1 } : null,
        "objLogin.iUpgDays" => _account ? -1 : null,
        "loginInfo.strUsername" => _loginName,
        "loginInfo.strPassword" => _loginPassword,
        _ => null,
    };

    private string Call(string path, string[] args)
    {
        switch (path)
        {
            case "login" when args is [string username, string password]:
                if (_brokenLogin)
                    return "<broken";
                _loginName = username;
                _loginPassword = password;
                _account = username == _username && password == _password;
                _kicked = false;
                break;
            case "logout":
            case "sfc.disconnect":
                _connected = false;
                _world = false;
                _server = null;
                break;
            case "gotoAndPlay" when args is ["Login"]:
                ToLoginScreen();
                break;
            case "world.myAvatar.pMC.artLoaded":
                return Str("true");
            case "sfc.sendString" when args is [string packet]:
                SendString(packet);
                break;
        }
        return "<undefined/>";
    }

    private string ConnectToServer(string json)
    {
        string name = JsonNode.Parse(json)!["sName"]!.GetValue<string>();
        if (!_account)
            return Str(true);

        _connDetail = "Connecting to game server...";
        Task.Delay(_connectDelay).ContinueWith(_ =>
        {
            lock (_lock)
            {
                if (_rejections.TryGetValue(name, out string? message))
                {
                    _connDetail = message;
                    PextStr(["loginResponse", "-1", "false", "-1", "", message]);
                    return;
                }
                _connected = true;
                _server = name;
                PextStr(["loginResponse", "-1", "true", "1", _username, "Welcome"]);
                Join("battleon", "Enter", "Spawn");
                _world = true;
                _connDetail = null;
            }
        });
        return Str(true);
    }

    private string ClickServer(string name)
    {
        if (!_account || JsonNode.Parse(_servers)!.AsArray().FirstOrDefault(s => s!["sName"]!.GetValue<string>().Contains(name, StringComparison.OrdinalIgnoreCase)) is not { } server)
            return Str(false);
        return ConnectToServer(server.ToJsonString());
    }

    private string KillLag(bool enable)
    {
        LagKilled = enable;
        return "<undefined/>";
    }

    private void ToLoginScreen()
    {
        _connected = false;
        _world = false;
        _server = null;
        _account = false;
        _connDetail = null;
        _bankLoaded = false;
        _hp = MaxHp;
        _state = 1;
    }

    /// <summary>A packet the Engine sends the game server: a map transfer or a bank load.</summary>
    private void SendString(string packet)
    {
        switch (packet.Split('%', StringSplitOptions.RemoveEmptyEntries))
        {
            case ["xt", "zm", "cmd", _, "tfer", _, string map, string cell, string pad]:
                _note($"tfer {map} {cell} {pad}");
                string name = map.Split('-')[0].ToLowerInvariant();
                if (!_connected || _lockedMaps.Contains(name))
                    return;
                _loading = true;
                Task.Delay(100).ContinueWith(_ =>
                {
                    lock (_lock)
                    {
                        // The game places the player in the cell only when the map has it.
                        bool known = Cells(name).Contains(cell);
                        Join(name, known ? cell : "Enter", known ? pad : "Spawn");
                        _loading = false;
                    }
                });
                return;
            case ["xt", "zm", "loadBank", _, "All"]:
                _note("loadBank");
                _bankLoaded = true;
                Pext(new JsonObject { ["cmd"] = "loadBank", ["items"] = Bank() });
                return;
        }
    }

    private void Join(string map, string cell, string pad)
    {
        _map = map;
        _cell = cell;
        _pad = pad;
        _roomId++;
        JsonArray users = [new JsonObject { ["uoName"] = _username.ToLowerInvariant(), ["strUsername"] = _username, ["strFrame"] = _cell, ["strPad"] = _pad }];
        Pext(new JsonObject { ["cmd"] = "moveToArea", ["strMapName"] = map, ["areaId"] = _roomId, ["strMapFileName"] = $"{map}.swf", ["uoBranch"] = users });
    }

    private string Jump(string cell, string pad)
    {
        _note($"jump {cell} {pad}");
        _cell = cell;
        _pad = pad;
        return "<undefined/>";
    }

    private static string[] Cells(string map) => map switch
    {
        "battleon" => ["Enter", "r2", "r3"],
        "yulgar" => ["Enter", "Upstairs", "Room"],
        _ => ["Enter"],
    };

    private static JsonArray Monsters(string map) => map == "battleon"
        ?
        [
            new JsonObject { ["MonID"] = 7, ["MonMapID"] = 1, ["strMonName"] = "Frogzard", ["sRace"] = "Dragonkin", ["strFrame"] = "r2", ["intHP"] = 500, ["intHPMax"] = 500, ["intState"] = 1 },
            new JsonObject { ["MonID"] = 7, ["MonMapID"] = 2, ["strMonName"] = "Frogzard", ["sRace"] = "Dragonkin", ["strFrame"] = "r2", ["intHP"] = 0, ["intHPMax"] = 500, ["intState"] = 0 },
        ]
        : [];

    private JsonObject Players()
    {
        string me = _username.ToLowerInvariant();
        return new JsonObject
        {
            [me] = new JsonObject
            {
                ["uoName"] = me, ["strUsername"] = _username, ["intLevel"] = 10, ["strFrame"] = _cell, ["strPad"] = _pad, ["intHP"] = _hp, ["intHPMax"] = MaxHp,
                ["intMP"] = 80, ["afk"] = false, ["intState"] = _state, ["entID"] = 1,
            },
            ["artixfan"] = new JsonObject
            {
                ["uoName"] = "artixfan", ["strUsername"] = "ArtixFan", ["intLevel"] = 42, ["strFrame"] = "r3", ["strPad"] = "Left", ["intHP"] = 800, ["intHPMax"] = 2000,
                ["intMP"] = 50, ["afk"] = true, ["intState"] = 1, ["entID"] = 2,
            },
        };
    }

    private static JsonArray Inventory() =>
    [
        Item(1, "Default Sword", 1, 1, "Sword", equipped: true, enhancement: 1),
        Item(2, "Healer", 1, 1, "Class", equipped: true),
        Item(3, "Treasure Chest", 5, 1000, "Item"),
    ];

    private static JsonArray Bank() => [Item(10, "Bank Relic", 2, 10, "Item")];

    private static JsonArray TempItems() => [Item(20, "Slime Sample", 3, 10, "Quest Item", temp: true)];

    private static JsonArray HouseItems() => [Item(30, "Wooden Chair", 1, 1, "Floor Item")];

    private static JsonObject QuestTree() => new()
    {
        ["1001"] = Quest(1001, "Slime Time", "p", member: false, gold: 100, xp: 50, (Item(20, "Slime Sample", 1, 10, "Quest Item", temp: true), 5),
            new JsonObject { ["itemsS"] = new JsonObject { ["3"] = Item(3, "Treasure Chest", 1, 1000, "Item") } }),
        ["1002"] = Quest(1002, "Chest Hoarder", "c", member: true, gold: 0, xp: 0, (Item(3, "Treasure Chest", 1, 1000, "Item"), 5), new JsonObject()),
        ["1003"] = Quest(1003, "Not Yet", null, member: false, gold: 10, xp: 10, (Item(3, "Treasure Chest", 1, 1000, "Item"), 1), new JsonObject()),
    };

    private static JsonObject Quest(int id, string name, string? status, bool member, int gold, int xp, (JsonObject Item, int Qty) requirement, JsonObject rewards)
    {
        int itemId = (int)requirement.Item["ItemID"]!;
        return new JsonObject
        {
            ["QuestID"] = id, ["sName"] = name, ["status"] = status, ["bUpg"] = member ? "1" : "0", ["iGold"] = gold, ["iExp"] = xp, ["bOnce"] = "0",
            ["oItems"] = new JsonObject { [itemId.ToString()] = requirement.Item },
            ["turnin"] = new JsonArray(new JsonObject { ["ItemID"] = itemId, ["iQty"] = requirement.Qty }),
            ["oRewards"] = rewards,
        };
    }

    /// <summary>An item as the game sends it, with its wire names and its flags as <c>"0"</c> or <c>"1"</c>.</summary>
    private static JsonObject Item(int id, string name, int qty, int maxStack, string category, bool equipped = false, int enhancement = 0, bool temp = false) => new()
    {
        ["ItemID"] = id, ["CharItemID"] = id + 100, ["sName"] = name, ["sDesc"] = "", ["iQty"] = qty, ["iStk"] = maxStack, ["sType"] = category,
        ["bEquip"] = equipped ? "1" : "0", ["bTemp"] = temp ? "1" : "0", ["bUpg"] = "0", ["bCoins"] = "0", ["EnhLvl"] = enhancement, ["iLvl"] = 1,
    };

    private void Pext(JsonObject data) => Send("pext", new JsonObject { ["params"] = new JsonObject { ["type"] = "json", ["dataObj"] = data } }.ToJsonString());

    private void PextStr(string[] data) =>
        Send("pext", new JsonObject { ["params"] = new JsonObject { ["type"] = "str", ["dataObj"] = new JsonArray([.. data.Select(d => JsonValue.Create(d))]) } }.ToJsonString());

    private void Packet(string packet) => Send("packet", packet);

    private void Send(string function, string argument) =>
        _invoke($"<invoke name=\"{function}\" returntype=\"xml\"><arguments><string>{SecurityElement.Escape(argument)}</string></arguments></invoke>");

    private static string Str(object value) => $"<string>{SecurityElement.Escape(value is bool b ? (b ? "true" : "false") : value.ToString())}</string>";

    /// <summary>What JSON.stringify returns: undefined for a missing object, else its JSON.</summary>
    private static string Json(object? value) => value is null ? "<undefined/>" : Str(JsonSerializer.Serialize(value));
}
