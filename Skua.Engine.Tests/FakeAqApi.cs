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
/// Stands in for macOS's <c>security</c> tool, so the Engine and the CLI use Keychain items these tests choose and never the real Keychain.
/// </summary>
/// <remarks>
/// It keeps its generic passwords in files in the sandbox, and runs <c>find-generic-password</c> (with or without <c>-g</c>),
/// <c>add-generic-password</c> and <c>delete-generic-password</c>, also as a command read from its standard input with <c>-i</c>.
/// </remarks>
public sealed class FakeKeychain
{
    public const string DefaultService = "skua-test-account";

    /// <param name="sandbox">The data folder the tool and its items live in.</param>
    /// <param name="username">The account the simulated game accepts, with <paramref name="password"/>.</param>
    /// <param name="password">The account's password.</param>
    /// <param name="service">The service it is stored under, or null to start with no item.</param>
    public FakeKeychain(EngineSandbox sandbox, string username = "SkuaTester", string password = "hunter2-Sekrit!", string? service = DefaultService)
    {
        Username = username;
        Password = password;
        Tool = Path.Combine(sandbox.SkuaDir, "fake-security");
        Log = Path.Combine(sandbox.SkuaDir, "fake-security.log");
        Items = Path.Combine(sandbox.SkuaDir, "fake-security-items");
        Directory.CreateDirectory(Items);
        File.WriteAllText(Tool, Script(Items, Log));
        File.SetUnixFileMode(Tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (service is not null)
            Add(service, username, password);
    }

    public string Username { get; }

    public string Password { get; }

    public string Tool { get; }

    /// <summary>One line per run of the tool, with its arguments.</summary>
    public string Log { get; }

    /// <summary>The folder holding one folder per item, named by its service.</summary>
    public string Items { get; }

    /// <summary>How many times a password was read, with <c>find-generic-password -s &lt;service&gt; -g</c>.</summary>
    public int Reads => File.Exists(Log) ? File.ReadAllLines(Log).Count(l => l.StartsWith("find-generic-password ", StringComparison.Ordinal) && l.EndsWith(" -g", StringComparison.Ordinal)) : 0;

    public IDictionary<string, string> Environment() => new Dictionary<string, string> { ["SKUA_SECURITY_TOOL"] = Tool };

    /// <summary>Stores an item as <c>security add-generic-password</c> would.</summary>
    public void Add(string service, string account, string password, string comment = "")
    {
        string item = Directory.CreateDirectory(Path.Combine(Items, service)).FullName;
        File.WriteAllText(Path.Combine(item, "acct"), account);
        File.WriteAllText(Path.Combine(item, "comment"), comment);
        File.WriteAllText(Path.Combine(item, "password"), PasswordLine(password) + "\n");
    }

    /// <summary>The account and password stored under <paramref name="service"/>, or null when there is no such item.</summary>
    public (string Account, string PasswordLine)? Find(string service)
    {
        string item = Path.Combine(Items, service);
        return Directory.Exists(item) ? (File.ReadAllText(Path.Combine(item, "acct")), File.ReadAllText(Path.Combine(item, "password")).TrimEnd('\n')) : null;
    }

    /// <summary>The comment stored under <paramref name="service"/>, or null when there is no such item.</summary>
    public string? Comment(string service)
    {
        string file = Path.Combine(Items, service, "comment");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

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

    /// <summary>
    /// The output of the real tool: for <c>find-generic-password</c> the attributes on stdout and, with <c>-g</c>, the password on stderr;
    /// exit code 44 for a missing item. With <c>-i</c> it reads one command, quoted as security's interactive mode reads it.
    /// </summary>
    private static string Script(string items, string log) => $$"""
        #!/bin/sh
        echo "$*" >> '{{log}}'
        if [ "$1" = -i ]; then
            IFS= read -r line || exit 1
            eval "set -- $line"
        fi
        command=$1
        shift
        service= account= password= comment= reveal=
        while [ $# -gt 0 ]; do
            case $1 in
                -s) service=$2; shift 2 ;;
                -a) account=$2; shift 2 ;;
                -w) password=$2; shift 2 ;;
                -j) comment=$2; shift 2 ;;
                -l) shift 2 ;;
                -U) shift ;;
                -g) reveal=1; shift ;;
                *) echo "security: $command: unknown option $1" >&2; exit 2 ;;
            esac
        done
        item='{{items}}'/"$service"
        missing() {
            echo "security: SecKeychainSearchCopyNext: The specified item could not be found in the keychain." >&2
            exit 44
        }
        case $command in
            find-generic-password)
                [ -n "$service" ] && [ -d "$item" ] || missing
                icmt='<NULL>'
                [ -s "$item/comment" ] && icmt="\"$(cat "$item/comment")\""
                cat <<EOF
        keychain: "/Users/tester/Library/Keychains/login.keychain-db"
        version: 512
        class: "genp"
        attributes:
            0x00000007 <blob>="$service"
            0x00000008 <blob>=<NULL>
            "acct"<blob>="$(cat "$item/acct")"
            "icmt"<blob>=$icmt
            "cdat"<timedate>=0x32303236303932363030303030305A00  "20260926000000Z\000"
            "svce"<blob>="$service"
        EOF
                [ -z "$reveal" ] || cat "$item/password" >&2
                ;;
            add-generic-password)
                [ -n "$service" ] && [ -n "$account" ] && [ -n "$password" ] || { echo "security: add-generic-password: missing an argument" >&2; exit 2; }
                mkdir -p "$item"
                printf '%s' "$account" > "$item/acct"
                printf '%s' "$comment" > "$item/comment"
                printf 'password: "%s"\n' "$password" > "$item/password"
                ;;
            delete-generic-password)
                [ -n "$service" ] && [ -d "$item" ] || missing
                rm -rf "$item"
                echo "password has been deleted."
                ;;
            *)
                echo "security: unknown command $command" >&2
                exit 2
                ;;
        esac
        """;

    // security quotes a printable password, and shows any other as hex, then its escaped text.
    private static string PasswordLine(string password) => password.All(char.IsAscii)
        ? $"password: \"{password}\""
        : $"password: 0x{Convert.ToHexString(Encoding.UTF8.GetBytes(password))}  \"{string.Concat(Encoding.UTF8.GetBytes(password).Select(b => b < 128 ? ((char)b).ToString() : $"\\{Convert.ToString(b, 8)}"))}\"";
}
