using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

/// <summary>
/// The AQW game as skua.swf exposes it over the Bridge, simulated just far enough for the Engine to log in, play and lose the connection:
/// the login screen, the account login, connecting to a server, the world, the connection message and the kick warning.
/// </summary>
/// <remarks>
/// Replies are what skua.swf returns: strings, with game objects as JSON. Anything it doesn't simulate is left to the scenario's replies.
/// </remarks>
internal sealed class FakeGame
{
    private readonly object _lock = new();
    private readonly Action<string> _invoke;
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
    private string _cell = "Enter";
    private bool _brokenLogin;

    /// <summary>Whether the lag killer hides the world, as <c>killLag</c> last set it.</summary>
    public bool LagKilled { get; private set; }

    /// <param name="invoke">Sends one <c>ExternalInterface.call</c> from the Game Client, as invoke XML.</param>
    public FakeGame(string username, string password, Action<string> invoke)
    {
        _username = username;
        _password = password;
        _invoke = invoke;
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
                    Pext(new JsonObject { ["cmd"] = "ct", ["p"] = new JsonObject { [_username.ToLowerInvariant()] = new JsonObject { ["intHP"] = 0 } } });
                    return true;
                case ["afk"]:
                    PextStr(["uotls", "-1", _username, "afk:true"]);
                    return true;
                case ["join", string map]:
                    Join(map);
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
        "world.myAvatar.dataLeaf.intState" => _world ? 1 : null,
        "world.myAvatar.objData.iUpgDays" => _world ? -1 : null,
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
                Join("battleon");
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
    }

    private void Join(string map)
    {
        _cell = "Enter";
        _roomId++;
        JsonArray users = [new JsonObject { ["uoName"] = _username.ToLowerInvariant(), ["strUsername"] = _username, ["strFrame"] = _cell, ["strPad"] = "Spawn" }];
        Pext(new JsonObject { ["cmd"] = "moveToArea", ["strMapName"] = map, ["areaId"] = _roomId, ["strMapFileName"] = $"{map}.swf", ["uoBranch"] = users });
    }

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
