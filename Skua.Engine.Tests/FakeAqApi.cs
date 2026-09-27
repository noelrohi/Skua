using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Skua.Core.Scripts;

namespace Skua.Engine.Tests;

/// <summary>A game server as the servers API and the game's own server list describe it.</summary>
public sealed record FakeServer(string Name, int Count = 100, int Max = 1000, bool Online = true, bool Member = false, string Lang = "en")
{
    public JsonObject ToJson() => new()
    {
        ["sName"] = Name,
        ["sIP"] = $"{Name.ToLowerInvariant()}.fake.aq.com",
        ["iPort"] = 5588,
        ["iChat"] = 2,
        ["bOnline"] = Online ? 1 : 0,
        ["sLang"] = Lang,
        ["iCount"] = Count,
        ["bUpg"] = Member ? 1 : 0,
        ["iMax"] = Max,
        ["iLevel"] = 0,
    };

    public static string ListJson(IEnumerable<FakeServer> servers) => new JsonArray([.. servers.Select(s => s.ToJson())]).ToJsonString();
}

/// <summary>Stands in for content.aq.com's servers API, which the Engine reads through Core's <c>GetServers</c>.</summary>
public sealed class FakeAqApi : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _serving;

    public FakeAqApi(params FakeServer[] servers)
    {
        Servers = servers;
        BaseUrl = $"http://127.0.0.1:{FreePort()}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _serving = ServeAsync();
    }

    public string BaseUrl { get; }

    /// <summary>What the API returns; a test may change it between requests.</summary>
    public FakeServer[] Servers { get; set; }

    /// <summary>Answers every request with a 500 while set.</summary>
    public bool Down { get; set; }

    public IDictionary<string, string> Environment() => new Dictionary<string, string>
    {
        [ScriptServers.ServersUrlEnvironmentVariable] = BaseUrl + "game/api/data/servers",
    };

    public async ValueTask DisposeAsync()
    {
        // Close alone, as in FakeGitHub: Stop then Close can bind the port again on macOS.
        _listener.Close();
        try
        {
            await _serving;
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
        {
        }
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using HttpListenerResponse response = context.Response;
            if (Down || context.Request.Url!.AbsolutePath != "/game/api/data/servers")
            {
                response.StatusCode = Down ? 500 : 404;
                continue;
            }
            byte[] body = Encoding.UTF8.GetBytes(FakeServer.ListJson(Servers));
            response.ContentType = "application/json";
            await response.OutputStream.WriteAsync(body);
        }
    }

    private static int FreePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}

/// <summary>
/// Stands in for macOS's <c>security</c> tool, so the Engine reads a Test Account these tests choose and never the real Keychain.
/// </summary>
public sealed class FakeKeychain
{
    public const string DefaultService = "skua-test-account";

    public FakeKeychain(EngineSandbox sandbox, string username = "SkuaTester", string password = "hunter2-Sekrit!", string service = DefaultService)
    {
        Username = username;
        Password = password;
        Tool = Path.Combine(sandbox.SkuaDir, "fake-security");
        Log = Path.Combine(sandbox.SkuaDir, "fake-security.log");
        File.WriteAllText(Tool, Script(service, username, password, Log));
        File.SetUnixFileMode(Tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string Username { get; }

    public string Password { get; }

    public string Tool { get; }

    /// <summary>One line per run of the tool, with its arguments.</summary>
    public string Log { get; }

    public int Reads => File.Exists(Log) ? File.ReadAllLines(Log).Length : 0;

    public IDictionary<string, string> Environment() => new Dictionary<string, string> { ["SKUA_SECURITY_TOOL"] = Tool };

    /// <summary>A tool that finds no item, which every test uses unless it sets up a <see cref="FakeKeychain"/>.</summary>
    public static string Empty(string directory)
    {
        string tool = Path.Combine(directory, "fake-security-empty");
        File.WriteAllText(tool, """
            #!/bin/sh
            echo "security: SecKeychainSearchCopyNext: The specified item could not be found in the keychain." >&2
            exit 44
            """);
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return tool;
    }

    // The same output as `security find-generic-password -s <service> [-w]`, and its exit code 44 for a missing item.
    private static string Script(string service, string username, string password, string log) => $$"""
        #!/bin/sh
        echo "$*" >> '{{log}}'
        [ "$1" = find-generic-password ] && [ "$2" = -s ] && [ "$3" = '{{service}}' ] || {
            echo "security: SecKeychainSearchCopyNext: The specified item could not be found in the keychain." >&2
            exit 44
        }
        if [ "$4" = -w ]; then
            printf '%s\n' '{{password}}'
            exit 0
        fi
        cat <<'EOF'
        keychain: "/Users/tester/Library/Keychains/login.keychain-db"
        version: 512
        class: "genp"
        attributes:
            0x00000007 <blob>="{{service}}"
            0x00000008 <blob>=<NULL>
            "acct"<blob>="{{username}}"
            "cdat"<timedate>=0x32303236303932363030303030305A00  "20260926000000Z\000"
            "svce"<blob>="{{service}}"
        EOF
        """;
}
