using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Skua.Core.Utils;
using WebClient = Skua.Core.Utils.WebClient;

namespace Skua.Avalonia.Tests;

/// <summary>
/// Stands in for github.com's device flow and for the pages About and Change Logs fetch from raw.githubusercontent.com (Change Logs from upstream and from this fork), which Core requests
/// through its own static clients at fixed URLs. From the start of the test process Core's clients answer those requests here, so no test
/// ever reaches GitHub's sign-in or those pages; any other request goes on as before.
/// </summary>
/// <remarks>
/// Another github.com request fails with 404, never reaching GitHub. The user's client, which a sign-in makes with the token, isn't replaced:
/// a test that signs in resets it in its finally, and no request goes through it meanwhile.
/// </remarks>
public static class FakeGitHubWeb
{
    public const string Readme = """
        <div align="center">

        ## [Usage](./usage.md) | [Build Guide](./BUILD.md)

        </div>

        ### About the fake Skua

        Skua is **a fake** readme, served by the tests' *stand-in* for GitHub.

        - Story scripts in the `Story` folder.
        - [Skua Discord](https://discord.com/invite/fake)

        ```txt
        UserID: null
        ```
        """;

    public const string ChangeLogs = """
        # Change Logs

        ## 1.2.3

        - Fixed the fake bug. See [Usage](./usage.md).
        """;

    /// <summary>The Mac App's own change log, which the Mac App's Change Logs shows before <see cref="ChangeLogs"/>.</summary>
    public const string MacChangeLogs = """
        # Skua for Mac

        ## 9.8.7

        - The fake Mac fix. See [Build Guide](./BUILD.md#install-on-macos).
        """;

    private static readonly ConcurrentQueue<string> s_requests = new();

    /// <summary>The device code, user code and token the next sign-in gets.</summary>
    public static (string DeviceCode, string UserCode, string Token) DeviceFlow { get; set; } = ("device", "USER-CODE", "token");

    /// <summary>Every github.com request answered here, as <c>METHOD path</c>, and each raw page served, as <c>GET raw/path</c>.</summary>
    public static IReadOnlyList<string> Requests => [.. s_requests];

    [ModuleInitializer]
    internal static void Install()
    {
        Replace(nameof(HttpClients.GitHubClient), new FakeGitHubClient());
        HttpClient raw = HttpClients.GitHubRaw;
        HttpClient fakeRaw = new(new RawHandler()) { BaseAddress = raw.BaseAddress, Timeout = raw.Timeout };
        foreach ((string name, IEnumerable<string> values) in raw.DefaultRequestHeaders)
            fakeRaw.DefaultRequestHeaders.TryAddWithoutValidation(name, values);
        Replace(nameof(HttpClients.GitHubRaw), fakeRaw);
    }

    /// <summary>Sets one of <see cref="HttpClients"/>' clients, whose setters Core keeps private.</summary>
    private static void Replace(string property, HttpClient client) =>
        typeof(HttpClients).GetProperty(property)!.SetValue(null, client);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>The device flow's two endpoints, as GitHub answers them with JSON; any other github.com request is a 404.</summary>
    private static HttpResponseMessage GitHub(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath;
        s_requests.Enqueue($"{request.Method} {path}");
        (string deviceCode, string userCode, string token) = DeviceFlow;
        return (request.Method.Method, path) switch
        {
            ("POST", "/login/device/code") => Json($$"""
                {"device_code":"{{deviceCode}}","user_code":"{{userCode}}","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}
                """),
            ("POST", "/login/oauth/access_token") => Json($$"""{"access_token":"{{token}}","token_type":"bearer","scope":"public_repo"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static bool IsGitHub(HttpRequestMessage request) =>
        request.RequestUri is { } uri && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Core's GitHub client, a <see cref="WebClient"/> whose handler Core makes itself, so the fake answers in its place.</summary>
    private sealed class FakeGitHubClient() : WebClient(true)
    {
        public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            IsGitHub(request) ? Task.FromResult(GitHub(request)) : base.SendAsync(request, cancellationToken);
    }

    private sealed class RawHandler() : DelegatingHandler(new HttpClientHandler { MaxConnectionsPerServer = 10, UseCookies = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? page = request.RequestUri is { Host: "raw.githubusercontent.com" } uri
                ? uri.AbsolutePath switch
                {
                    "/auqw/Skua/refs/heads/master/readme.md" => Readme,
                    "/auqw/Skua/refs/heads/master/changelogs.md" => ChangeLogs,
                    "/noelrohi/Skua/refs/heads/master/changelogs-mac.md" => MacChangeLogs,
                    _ => null,
                }
                : null;
            if (page is null)
                return base.SendAsync(request, cancellationToken);
            s_requests.Enqueue($"GET raw{request.RequestUri!.AbsolutePath}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page) });
        }
    }
}
