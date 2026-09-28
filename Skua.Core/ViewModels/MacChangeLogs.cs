using Skua.Core.Utils;
using System.Text.RegularExpressions;

namespace Skua.Core.ViewModels;

/// <summary>
/// The Change Logs page on macOS: the Mac App's own change log from this fork, then upstream Skua's, under a heading of its own. The Mac
/// change log is its own file, so syncing upstream's <c>changelogs.md</c> never conflicts with it.
/// </summary>
/// <remarks>
/// The Mac change log's relative links are made absolute on <c>noelrohi/Skua</c>, so a <c>./</c> link that reaches
/// <see cref="ChangeLogsViewModel"/> is still one of upstream's and opens on <c>auqw/Skua</c>, as on Windows.
/// </remarks>
public static partial class MacChangeLogs
{
    public const string MacPath = "noelrohi/Skua/refs/heads/master/changelogs-mac.md";
    public const string UpstreamPath = "auqw/Skua/refs/heads/master/changelogs.md";
    public const string MacBlob = "https://github.com/noelrohi/Skua/blob/master/";

    public const string NoContent = "### No content found. Please check your internet connection.";
    public const string MacHeading = "# Skua for Mac";
    public const string UpstreamHeading = "# Skua for Windows";
    public const string MacMissing = "### The Mac App's change log couldn't be loaded. Please check your internet connection.";
    public const string UpstreamMissing = "### Skua's change log couldn't be loaded. Please check your internet connection.";

    private const string UpstreamIntro = "Skua's own releases, from auqw/Skua, which the Mac App is built on.";

    /// <summary>Fetches both change logs at once from <paramref name="raw"/>, GitHub's raw file host, and puts them on one page.</summary>
    public static async Task<string> GetAsync(HttpClient raw)
    {
        Task<string?> mac = TryGetAsync(raw, MacPath);
        Task<string?> upstream = TryGetAsync(raw, UpstreamPath);
        return Combine(await mac.ConfigureAwait(false), await upstream.ConfigureAwait(false));
    }

    /// <summary>
    /// The Mac change log, a rule, then upstream's under <see cref="UpstreamHeading"/>. A change log that failed to load (null) leaves a
    /// note in its place; with neither, the page is <see cref="NoContent"/>, as on Windows.
    /// </summary>
    public static string Combine(string? mac, string? upstream)
    {
        if (mac is null && upstream is null)
            return NoContent;
        string macSection = mac is null ? $"{MacHeading}\n\n{MacMissing}" : OnMacRepo(mac.TrimEnd());
        string upstreamSection = $"{UpstreamHeading}\n\n{UpstreamIntro}\n\n{(upstream is null ? UpstreamMissing : upstream.TrimStart())}";
        return $"{macSection}\n\n---\n\n{upstreamSection}";
    }

    /// <summary>Makes each relative link in <paramref name="markdown"/> absolute on this fork, as GitHub resolves it from the repository's root.</summary>
    public static string OnMacRepo(string markdown) =>
        LinkTarget().Replace(markdown, m => IsRelative(m.Groups["url"].Value)
            ? m.Groups["open"].Value + MacBlob + FromRoot(m.Groups["url"].Value)
            : m.Value);

    /// <summary>Whether <paramref name="url"/> is a path in the repository: not a page anchor, and with no scheme or host of its own.</summary>
    private static bool IsRelative(string url) => !url.StartsWith('#') && !url.StartsWith("//", StringComparison.Ordinal) && !Scheme().IsMatch(url);

    private static string FromRoot(string path)
    {
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        return path.TrimStart('/');
    }

    private static async Task<string?> TryGetAsync(HttpClient raw, string path)
    {
        try
        {
            return await ValidatedHttpExtensions.GetStringAsync(raw, path).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?<open>\]\()(?<url>[^)\s]+)")]
    private static partial Regex LinkTarget();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex Scheme();
}
