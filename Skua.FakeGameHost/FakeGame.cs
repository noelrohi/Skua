using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

/// <summary>
/// The AQW game as skua.swf exposes it over the Bridge, simulated just far enough for the Engine to log in, play and lose the connection:
/// the login screen, the account login, connecting to a server, the world, the connection message and the kick warning; and to move and
/// look around: map transfers and jumps, the player, the item stores, the quest tree, the map's players and monsters, and drops. Its
/// <c>connectTo</c> connects over TCP to a game server on this Mac only (a loopback address), as the Packet Interceptor has the game do.
/// </summary>
/// <remarks>
/// Replies are what skua.swf returns: strings, with game objects as JSON. Anything it doesn't simulate is left to the scenario's replies.
/// </remarks>
internal sealed class FakeGame
{
    private readonly object _lock = new();
    private readonly Action<string> _invoke;
    private readonly Action<string> _note;
    private string _username;
    private readonly Dictionary<string, string> _accounts = new(StringComparer.OrdinalIgnoreCase);
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
    private bool _blip;
    private int _blipsRead;
    private string? _connDetail;
    private string? _server;
    private string _map = "battleon";
    private string _cell = "Enter";
    private string _pad = "Spawn";
    private bool _loading;
    private int _hp = MaxHp;
    private int _state = 1;
    private DateTime _diedAt;
    private int _level = 10;
    private int _xp = 1500;
    private int _gold = 5000;
    private bool _bankLoaded;
    private bool _bankOpen;
    private DateTime _inventoryAt;
    private int _inventoryDelay = 500;
    private int _bagSlots = 40;
    private readonly HashSet<string> _lockedMaps = new(StringComparer.OrdinalIgnoreCase);
    private bool _brokenLogin;
    /// <summary>The type of the text field the stage's focus is on (<c>input</c> for chat's), or null when it isn't on one.</summary>
    private string? _focus;
    /// <summary>The game server <c>connectTo</c> connected to, which the game's packets go to until it closes.</summary>
    private NetworkStream? _socket;

    private const int MaxHp = 1000;
    private const int RequiredXp = 4000;
    private const int PlayerId = 1;
    private static readonly TimeSpan RespawnMinimum = TimeSpan.FromSeconds(2);

    /// <summary>Whether the lag killer hides the world, as <c>killLag</c> last set it.</summary>
    public bool LagKilled { get; private set; }

    /// <param name="invoke">Sends one <c>ExternalInterface.call</c> from the Game Client, as invoke XML.</param>
    /// <param name="note">Records what the game did for a test to check, e.g. <c>tfer yulgar Enter Spawn</c>.</param>
    public FakeGame(string username, string password, Action<string> invoke, Action<string> note)
    {
        _username = username;
        _accounts[username] = password;
        _invoke = invoke;
        _note = note;
    }

    /// <summary>Another account the game accepts; the player is the account that last logged in.</summary>
    public void Account(string username, string password) => _accounts[username] = password;

    /// <summary>The server list the game shows once the account has logged in, as the servers API's JSON.</summary>
    public void Servers(string json) => _servers = json;

    public void ConnectDelay(int milliseconds) => _connectDelay = milliseconds;

    /// <summary>How long after the world the inventory arrives; until then the game refuses map transfers.</summary>
    public void InventoryDelay(int milliseconds) => _inventoryDelay = milliseconds;

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
                ("isLoggedIn", _) => Str(_connected && !_blip),
                ("isKicked", _) => Str(ReadKicked()),
                ("isNull", [string path]) => Str(Get(path) is null),
                ("getGameObject", [string path]) => Json(Get(path)),
                ("getGameObjectS", [string path]) => Json(GetStatic(path)),
                ("getGameObjectKey", ["world.uoTree", string player]) => Json(_world ? Players()[player] : null),
                ("callGameFunction" or "callGameFunction0", [string path, .. string[] rest]) => Call(path, rest),
                ("connectToServer", [string server]) => ConnectToServer(server),
                ("clickServer", [string serverName]) => ClickServer(serverName),
                ("killLag", [string enable]) => KillLag(enable == "true"),
                ("jumpCorrectRoom", [string cell, string pad, ..]) => Jump(cell, pad),
                ("selectArrayObjects", ["world.map.currentScene.labels", "name"]) => _world ? Str(new JsonArray([.. Cells(_map).Select(c => JsonValue.Create(c))]).ToJsonString()) : "<undefined/>",
                ("getMonsters", _) => Str((_world ? Monsters(_map) : []).ToJsonString()),
                ("sendClientPacket", [string packet, string type]) => ClientPacket(packet, type),
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
                case ["blip"]:
                    // The connection flag drops for one of the Engine's game state readings, as a slow poll can read it.
                    _blip = true;
                    return true;
                case ["broken-login"]:
                    _brokenLogin = true;
                    return true;
                case ["login-response"]:
                    // The last login's response again, as the Engine sees one that reaches it late.
                    LoginAccepted();
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
                    _diedAt = DateTime.UtcNow;
                    Pext(new JsonObject { ["cmd"] = "ct", ["p"] = new JsonObject { [_username.ToLowerInvariant()] = new JsonObject { ["intHP"] = 0 } } });
                    return true;
                case ["afk"]:
                    PextStr(["uotls", "-1", _username, "afk:true"]);
                    return true;
                case ["respawn-request"]:
                    // The Game Client's own request when its respawn countdown ends.
                    string request = $"%xt%zm%resPlayerTimed%{_roomId}%{PlayerId}%";
                    Packet(request);
                    _note($"send {request}");
                    RespawnRequested();
                    return true;
                case ["combat"]:
                    _state = 2;
                    return true;
                case ["gain", string rest] when rest.Split(' ') is [string xp, string gold]:
                    // Reaching the required XP levels up, as the game says with levelUp, and the next level's XP starts from what is left over.
                    _xp += int.Parse(xp);
                    _gold += int.Parse(gold);
                    int before = _level;
                    for (; _xp >= RequiredXp; _xp -= RequiredXp)
                        _level++;
                    if (_level > before)
                        Pext(new JsonObject { ["cmd"] = "levelUp", ["intLevel"] = _level, ["intExpToLevel"] = RequiredXp });
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
                case ["bag-slots", string slots]:
                    _bagSlots = int.Parse(slots);
                    return true;
                case ["pickup", string id]:
                    Pext(new JsonObject { ["cmd"] = "getDrop", ["ItemID"] = int.Parse(id), ["bSuccess"] = 1, ["iQty"] = 1, ["iQtyNow"] = 1, ["bBank"] = false });
                    return true;
                case ["focus", "none"]:
                    _focus = null;
                    return true;
                case ["focus", string type]:
                    _focus = type;
                    return true;
                case ["cell", string cell]:
                    _cell = cell;
                    Packet($"%xt%zm%moveToCell%{_roomId}%{cell}%Spawn%");
                    return true;
                case ["packet", string packet]:
                    // The game's packet call, as for each packet it sends.
                    Packet(packet);
                    return true;
                case ["server-packet", string packet]:
                    // A string packet from the game server, e.g. %xt%chatm%-1%zone~hi%Bob%, as SmartFox hands it to the game: its fields after xt.
                    PextStr(packet.Trim('%').Split('%')[1..]);
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// The kick flag, which the Engine's game state tracker reads only once it has read the connection flag as dropped, so it ends a
    /// <c>blip</c> there: Core's timer reads the connection flag too, and must not take the tracker's reading. The call log records
    /// <c>blip read &lt;n&gt;</c> when it has.
    /// </summary>
    private bool ReadKicked()
    {
        if (_blip)
        {
            _blip = false;
            _note($"blip read {++_blipsRead}");
        }
        return _kicked;
    }

    private object? Get(string path) => path switch
    {
        "world" => _world ? new JsonObject() : null,
        "world.myAvatar" => _world ? new JsonObject() : null,
        "world.myAvatar.uid" => _world ? PlayerId : null,
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
        // The game's uoTree is a flash.utils.Dictionary, whose toJSON gives "Dictionary"; the room's names are in areaUsers.
        "world.uoTree" => _world ? "Dictionary" : null,
        "world.areaUsers" => _world ? new JsonArray([.. Players().Select(p => JsonValue.Create(p.Key))]) : null,
        "world.myAvatar.dataLeaf.intState" => _world ? _state : null,
        "world.myAvatar.dataLeaf.intHP" => _world ? _hp : null,
        "world.myAvatar.dataLeaf.intHPMax" => _world ? MaxHp : null,
        "world.myAvatar.dataLeaf.intMPMax" => _world ? 100 : null,
        "world.myAvatar.dataLeaf.intLevel" => _world ? _level : null,
        "world.myAvatar.objData.intExp" => _world ? _xp : null,
        "world.myAvatar.objData.intExpToLevel" => _world ? RequiredXp : null,
        "world.myAvatar.objData.intMP" => _world ? 80 : null,
        "world.myAvatar.objData.intGold" => _world ? _gold : null,
        "world.myAvatar.objData.iUpgDays" => _world ? -1 : null,
        "world.myAvatar.objData.strUsername" => _world ? _username : null,
        "world.myAvatar.items" => _world ? (InventoryLoaded ? Inventory() : []) : null,
        "world.myAvatar.items.length" => _world ? (InventoryLoaded ? Inventory().Count : 0) : null,
        "world.myAvatar.objData.iBagSlots" => _world ? _bagSlots : null,
        "world.bankinfo.items" => _world ? (_bankLoaded ? Bank() : []) : null,
        "world.bankinfo.BankArray.length" => _world ? (_bankLoaded ? Bank().Count : 0) : null,
        "world.myAvatar.invLoaded" => InventoryLoaded,
        "world.myAvatar.objData.iBankSlots" => _world ? 10 : null,
        // The login's bank count, known before the bank loads.
        "world.myAvatar.iBankCount" => InventoryLoaded ? Bank().Count : null,
        "world.myAvatar.tempitems" => _world ? TempItems() : null,
        "world.myAvatar.houseitems" => _world ? HouseItems() : null,
        "world.myAvatar.houseitems.length" => _world ? HouseItems().Count : null,
        "world.myAvatar.objData.iHouseSlots" => _world ? 20 : null,
        "world.questTree" => _world ? QuestTree() : null,
        "stage.focus.text" => _focus is null ? null : "",
        "stage.focus.type" => _focus,
        "ui.mcPopup.currentLabel" => _world && _bankOpen ? "Bank" : null,
        _ => null,
    };

    private bool InventoryLoaded => _world && DateTime.UtcNow >= _inventoryAt;

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
                _account = _accounts.TryGetValue(username, out string? accepted) && accepted == password;
                if (_account)
                    _username = username;
                _kicked = false;
                break;
            case "logout":
            case "sfc.disconnect":
                _connected = false;
                _world = false;
                _server = null;
                _bankOpen = false;
                CloseSocket();
                break;
            case "connectTo" when args is [string ip, string port]:
                ConnectTo(ip, int.Parse(port));
                break;
            case "gotoAndPlay" when args is ["Login"]:
                ToLoginScreen();
                break;
            case "getBank":
                // The game loads the bank over HTTP, not from the game server; it needs the character's data from the inventory.
                _note("getBank");
                if (InventoryLoaded)
                    Task.Delay(100).ContinueWith(_ =>
                    {
                        lock (_lock)
                            _bankLoaded = _world;
                    });
                break;
            case "world.toggleBank":
                // The game's bank panel opens, or closes if it was open.
                _bankOpen = _world && !_bankOpen;
                _note($"toggleBank {(_bankOpen ? "open" : "closed")}");
                break;
            case "world.sendLoadShopRequest" when args is [string shop]:
                _note($"loadShop {shop}");
                break;
            case "world.showQuests" when args is [string quests, ..]:
                _note($"showQuests {quests}");
                break;
            case "world.goto" when args is [string player]:
                // The /goto command; the player stays where it is.
                _note($"goto {player}");
                break;
            case "world.myAvatar.pMC.artLoaded":
                return Str("true");
            case "sfc.sendString" when args is [string packet]:
                _note($"send {packet}");
                SendString(packet);
                WriteSocket(packet);
                break;
            case "sfc.sendJson" when args is [string packet]:
                _note($"sendJson {packet}");
                WriteSocket(packet);
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
                LoginAccepted();
                Join("battleon", "Enter", "Spawn");
                _world = true;
                _connDetail = null;
                // The inventory, and with it the bank count, arrives a moment after the world.
                _inventoryAt = DateTime.UtcNow.AddMilliseconds(_inventoryDelay);
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

    /// <summary>
    /// The game's <c>connectTo</c>, for a game server on this Mac: it connects to it, sends SmartFoxServer's version check and, once the
    /// server answers, its login, and enters the world once the server accepts it. Messages both ways are null-terminated; the login carries
    /// the username alone. The server closing the connection loses it. Any address other than a loopback one is refused without connecting,
    /// so the fake never reaches a real game server.
    /// </summary>
    private void ConnectTo(string ip, int port)
    {
        if (!_account)
            return;
        CloseSocket();
        if (!IPAddress.TryParse(ip, out IPAddress? address) || !IPAddress.IsLoopback(address))
        {
            _note($"connectTo {ip} {port} refused");
            _connDetail = "Connection failed";
            return;
        }
        _connDetail = "Connecting to game server...";
        string username = _username;
        Task.Run(async () =>
        {
            using TcpClient client = new() { NoDelay = true };
            try
            {
                await client.ConnectAsync(address, port);
            }
            catch (SocketException ex)
            {
                _note($"connectTo {ip} {port} failed");
                lock (_lock)
                    _connDetail = $"Connection failed: {ex.SocketErrorCode}";
                return;
            }
            NetworkStream stream = client.GetStream();
            lock (_lock)
                _socket = stream;
            _note($"connectTo {ip} {port}");
            WriteSocket("<msg t='sys'><body action='verChk' r='0'><ver v='165' /></body></msg>");
            List<byte> message = [];
            byte[] buffer = new byte[4096];
            try
            {
                for (int read; (read = await stream.ReadAsync(buffer)) > 0;)
                {
                    foreach (byte b in buffer.AsSpan(0, read))
                    {
                        if (b != 0)
                        {
                            message.Add(b);
                            continue;
                        }
                        string text = Encoding.UTF8.GetString([.. message]);
                        message.Clear();
                        if (text.Contains("action='apiOK'", StringComparison.Ordinal))
                            WriteSocket($"<msg t='sys'><body action='login' r='0'><login z='zone_master'><nick><![CDATA[{username}]]></nick><pword><![CDATA[]]></pword></login></body></msg>");
                        else if (text.StartsWith("%xt%loginResponse%-1%true%", StringComparison.Ordinal))
                            ServerAccepted(stream, ip);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
            lock (_lock)
            {
                if (_socket != stream)
                    return;
                _socket = null;
                _connected = false;
                _connDetail = "Connection lost";
            }
        });
    }

    private void ServerAccepted(NetworkStream stream, string ip)
    {
        lock (_lock)
        {
            if (_socket != stream)
                return;
            _connected = true;
            _server = ip;
            LoginAccepted();
            Join("battleon", "Enter", "Spawn");
            _world = true;
            _connDetail = null;
            _inventoryAt = DateTime.UtcNow.AddMilliseconds(_inventoryDelay);
        }
    }

    /// <summary>Sends a message to the game server <c>connectTo</c> connected to, if any.</summary>
    private void WriteSocket(string text)
    {
        lock (_lock)
        {
            try
            {
                _socket?.Write([.. Encoding.UTF8.GetBytes(text), 0]);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private void CloseSocket()
    {
        _socket?.Dispose();
        _socket = null;
    }

    /// <summary>The game handles a packet as if the server had sent it; this records it as <c>clientPacket &lt;type&gt; &lt;packet&gt;</c>.</summary>
    private string ClientPacket(string packet, string type)
    {
        _note($"clientPacket {type} {packet}");
        return "<undefined/>";
    }

    private void ToLoginScreen()
    {
        CloseSocket();
        _connected = false;
        _world = false;
        _server = null;
        _account = false;
        _connDetail = null;
        _bankLoaded = false;
        _bankOpen = false;
        _hp = MaxHp;
        _state = 1;
    }

    /// <summary>A packet the Engine sends the game server: a map transfer, or the old bank load, which gets no reply.</summary>
    private void SendString(string packet)
    {
        switch (packet.Split('%', StringSplitOptions.RemoveEmptyEntries))
        {
            case ["xt", "zm", "cmd", _, "tfer", _, string map, string cell, string pad]:
                _note($"tfer {map} {cell} {pad}");
                string name = map.Split('-')[0].ToLowerInvariant();
                // Until the inventory has loaded, the game refuses with "Character Inventory is being loaded. Please wait..."
                if (!_connected || _lockedMaps.Contains(name) || !InventoryLoaded)
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
            case ["xt", "zm", "resPlayerTimed", ..]:
                RespawnRequested();
                return;
            case ["xt", "zm", "loadBank", ..]:
                // The game server no longer answers it (#49).
                _note("loadBank");
                return;
        }
    }

    /// <summary>
    /// The game server's <c>resPlayerTimed</c>: it respawns a dead player at the map's entrance with <c>resTimed</c>, and the game moves
    /// them there; it ignores a request that comes too soon after the death (the real server's 8 s, scaled down here).
    /// The call log records <c>respawn</c> or <c>respawn ignored</c>.
    /// </summary>
    private void RespawnRequested()
    {
        if (_state != 0 || DateTime.UtcNow - _diedAt < RespawnMinimum)
        {
            _note("respawn ignored");
            return;
        }
        _note("respawn");
        _state = 1;
        _hp = MaxHp;
        PextStr(["resTimed", "-1", "Enter", "Spawn"]);
        _cell = "Enter";
        _pad = "Spawn";
        Packet($"%xt%zm%moveToCell%{_roomId}%Enter%Spawn%");
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
                ["uoName"] = me, ["strUsername"] = _username, ["intLevel"] = _level, ["strFrame"] = _cell, ["strPad"] = _pad, ["intHP"] = _hp, ["intHPMax"] = MaxHp,
                ["intMP"] = 80, ["afk"] = false, ["intState"] = _state, ["entID"] = PlayerId,
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
        ["1001"] = Quest(1001, "Slime Time", "p", member: false, gold: 100, xp: 50,
            new JsonObject { ["itemsS"] = new JsonObject { ["3"] = Item(3, "Treasure Chest", 1, 1000, "Item") } }, (Item(20, "Slime Sample", 1, 10, "Quest Item", temp: true), 5)),
        ["1002"] = Quest(1002, "Chest Hoarder", "c", member: true, gold: 0, xp: 0, new JsonObject(), (Item(3, "Treasure Chest", 1, 1000, "Item"), 5)),
        // Shaped like 10238: oItems in the order the server adds them, which Ruffle keeps and Flash doesn't.
        ["1003"] = Quest(1003, "Not Yet", null, member: false, gold: 10, xp: 10, new JsonObject(),
            (Item(93555, "Undead Vaughn", 1, 6, "Quest Item", temp: true), 6), (Item(93556, "Wraith's Loyalty", 1, 9, "Quest Item", temp: true), 9)),
    };

    private static JsonObject Quest(int id, string name, string? status, bool member, int gold, int xp, JsonObject rewards, params (JsonObject Item, int Qty)[] requirements)
    {
        JsonObject items = [];
        JsonArray turnin = [];
        foreach ((JsonObject item, int qty) in requirements)
        {
            int itemId = (int)item["ItemID"]!;
            items[itemId.ToString()] = item;
            turnin.Add(new JsonObject { ["ItemID"] = itemId, ["iQty"] = qty });
        }
        return new JsonObject
        {
            ["QuestID"] = id, ["sName"] = name, ["status"] = status, ["bUpg"] = member ? "1" : "0", ["iGold"] = gold, ["iExp"] = xp, ["bOnce"] = "0",
            ["oItems"] = items, ["turnin"] = turnin, ["oRewards"] = rewards,
        };
    }

    /// <summary>An item as the game sends it, with its wire names and its flags as <c>"0"</c> or <c>"1"</c>.</summary>
    private static JsonObject Item(int id, string name, int qty, int maxStack, string category, bool equipped = false, int enhancement = 0, bool temp = false) => new()
    {
        ["ItemID"] = id, ["CharItemID"] = id + 100, ["sName"] = name, ["sDesc"] = "", ["iQty"] = qty, ["iStk"] = maxStack, ["sType"] = category,
        ["bEquip"] = equipped ? "1" : "0", ["bTemp"] = temp ? "1" : "0", ["bUpg"] = "0", ["bCoins"] = "0", ["EnhLvl"] = enhancement, ["iLvl"] = 1,
    };

    private void Pext(JsonObject data) => Send("pext", new JsonObject { ["params"] = new JsonObject { ["type"] = "json", ["dataObj"] = data } }.ToJsonString());

    private void LoginAccepted() => PextStr(["loginResponse", "-1", "true", "1", _username, "Welcome"]);

    private void PextStr(string[] data) =>
        Send("pext", new JsonObject { ["params"] = new JsonObject { ["type"] = "str", ["dataObj"] = new JsonArray([.. data.Select(d => JsonValue.Create(d))]) } }.ToJsonString());

    private void Packet(string packet) => Send("packet", packet);

    private void Send(string function, string argument) =>
        _invoke($"<invoke name=\"{function}\" returntype=\"xml\"><arguments><string>{SecurityElement.Escape(argument)}</string></arguments></invoke>");

    private static string Str(object value) => $"<string>{SecurityElement.Escape(value is bool b ? (b ? "true" : "false") : value.ToString())}</string>";

    /// <summary>What JSON.stringify returns: undefined for a missing object, else its JSON.</summary>
    private static string Json(object? value) => value is null ? "<undefined/>" : Str(JsonSerializer.Serialize(value));
}
