using System.Net;
using System.Text.RegularExpressions;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Tests;

/// <summary>
/// The Mac App's Change Logs page: the Mac change log from this fork, then upstream's, each fetched from its own stand-in for GitHub's raw
/// file host, so nothing reaches GitHub or the app's shared state.
/// </summary>
public sealed class MacChangeLogsTests
{
    private const string Mac = """
        # Skua for Mac

        ## 9.8.7

        - The Mac fix, in the [Build Guide](./BUILD.md#install-on-macos) and [the plan](docs/plans/x.md#top).
        """;

    private const string Upstream = """
        # Skua 1.2.3

        - The shared fix, in [Usage](./usage.md).
        """;

    [Fact]
    public async Task Both_loaded_show_the_Mac_change_log_first_then_upstreams_under_its_own_heading()
    {
        using HttpClient raw = Raw(mac: Mac, upstream: Upstream);

        string page = await MacChangeLogs.GetAsync(raw);

        Assert.Equal($"""
            # Skua for Mac

            ## 9.8.7

            - The Mac fix, in the [Build Guide]({MacChangeLogs.MacBlob}BUILD.md#install-on-macos) and [the plan]({MacChangeLogs.MacBlob}docs/plans/x.md#top).

            ---

            {MacChangeLogs.UpstreamHeading}

            Skua's own releases, from auqw/Skua, which the Mac App is built on.

            {Upstream}
            """.ReplaceLineEndings("\n"), page);
    }

    [Fact]
    public async Task Without_the_Mac_change_log_upstreams_still_shows_after_a_note()
    {
        using HttpClient raw = Raw(mac: null, upstream: Upstream);

        string page = await MacChangeLogs.GetAsync(raw);

        Assert.StartsWith($"{MacChangeLogs.MacHeading}\n\n{MacChangeLogs.MacMissing}\n\n---\n\n{MacChangeLogs.UpstreamHeading}\n\n", page);
        Assert.EndsWith(Upstream, page);
        Assert.Contains("[Usage](./usage.md)", page);
    }

    [Fact]
    public async Task Without_upstreams_change_log_the_Mac_one_still_shows_before_a_note()
    {
        using HttpClient raw = Raw(mac: Mac, upstream: null);

        string page = await MacChangeLogs.GetAsync(raw);

        Assert.StartsWith("# Skua for Mac\n\n## 9.8.7\n\n", page);
        Assert.Contains($"[Build Guide]({MacChangeLogs.MacBlob}BUILD.md#install-on-macos)", page);
        Assert.EndsWith($"\n\n---\n\n{MacChangeLogs.UpstreamHeading}\n\nSkua's own releases, from auqw/Skua, which the Mac App is built on.\n\n{MacChangeLogs.UpstreamMissing}", page);
    }

    [Fact]
    public async Task With_neither_the_page_says_no_content_was_found_as_on_Windows()
    {
        using HttpClient raw = Raw(mac: null, upstream: null);

        Assert.Equal("### No content found. Please check your internet connection.", await MacChangeLogs.GetAsync(raw));
    }

    [Fact]
    public async Task An_empty_change_log_counts_as_not_loaded()
    {
        using HttpClient raw = Raw(mac: " \n", upstream: Upstream);

        Assert.StartsWith($"{MacChangeLogs.MacHeading}\n\n{MacChangeLogs.MacMissing}\n\n", await MacChangeLogs.GetAsync(raw));
    }

    [Fact]
    public void Only_the_Mac_change_logs_relative_links_move_to_this_fork()
    {
        string links = "[a](./a.md) [b](/b.md) [c](c/d.md) [e](https://example.com/e) [f](#f) [g](mailto:g@example.com) [h](//example.com/h) ![i](./i.png)";

        Assert.Equal(
            $"[a]({MacChangeLogs.MacBlob}a.md) [b]({MacChangeLogs.MacBlob}b.md) [c]({MacChangeLogs.MacBlob}c/d.md) [e](https://example.com/e) [f](#f) " +
            $"[g](mailto:g@example.com) [h](//example.com/h) ![i]({MacChangeLogs.MacBlob}i.png)",
            MacChangeLogs.OnMacRepo(links));
    }

    [Fact]
    public void The_Mac_change_log_this_fork_serves_starts_under_the_Mac_heading_and_links_only_to_whole_URLs()
    {
        string mac = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "changelogs-mac.md"));

        string page = MacChangeLogs.Combine(mac, Upstream);

        Assert.StartsWith(MacChangeLogs.MacHeading + "\n", mac.ReplaceLineEndings("\n"));
        string macSection = page[..page.IndexOf(MacChangeLogs.UpstreamHeading, StringComparison.Ordinal)];
        string[] links = [.. Regex.Matches(macSection, @"\]\((?<url>[^)\s]+)").Select(m => m.Groups["url"].Value)];
        Assert.NotEmpty(links);
        Assert.All(links, l => Assert.StartsWith("https://", l));
        Assert.Contains(MacChangeLogs.MacBlob + "BUILD.md#install-on-macos", links);
    }

    /// <summary>
    /// GitHub's raw host, serving each change log that isn't null and answering 404 for one that is, as it does for a missing file. Any other
    /// path fails the test.
    /// </summary>
    private static HttpClient Raw(string? mac, string? upstream) =>
        new(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/" + MacChangeLogs.MacPath => Page(mac),
            "/" + MacChangeLogs.UpstreamPath => Page(upstream),
            string path => throw new InvalidOperationException($"Unexpected request for {path}"),
        }))
        {
            BaseAddress = new Uri("https://raw.githubusercontent.com/"),
            Timeout = TimeSpan.FromSeconds(30),
        };

    private static HttpResponseMessage Page(string? content) =>
        content is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
