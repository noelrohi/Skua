using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace Skua.Engine.Logging;

/// <summary>
/// Makes every string of an entry safe to store: it redacts each registered secret and the game's login token, then cuts the string
/// to its size cap on a character boundary, so a cut never leaves part of a secret behind.
/// </summary>
internal sealed partial class LogScrubber
{
    public const string Redacted = "[redacted]";

    /// <summary>The cap on any string field, in UTF-8 bytes.</summary>
    public const int MaxFieldBytes = 16 * 1024;

    /// <summary>The cap on a <c>stack</c> field in event data, in UTF-8 bytes.</summary>
    public const int MaxStackBytes = 4 * 1024;

    /// <summary>The cap on a Script Dialog's message, a <c>text</c> field in event data, in UTF-8 bytes.</summary>
    public const int MaxDialogTextBytes = 64 * 1024;

    /// <summary>The cap on a Script Report's data, a <c>data</c> field in event data, in UTF-8 bytes; as a Notice's text.</summary>
    public const int MaxReportDataBytes = MaxDialogTextBytes;

    /// <summary>Longest first, so a secret that contains another is redacted whole.</summary>
    private ImmutableArray<string> _secrets = [];

    /// <summary>Redacts every later occurrence of <paramref name="secret"/>. Entries already recorded stay as they are.</summary>
    public void AddSecret(string secret)
    {
        if (secret.Length == 0)
            return;
        ImmutableInterlocked.Update(ref _secrets, secrets =>
            secrets.Contains(secret) ? secrets : [.. secrets.Add(secret).OrderByDescending(s => s.Length)]);
    }

    public string Text(string text, ref bool truncated) => Scrub(text, MaxFieldBytes, ref truncated);

    /// <summary>
    /// Scrubs every string in <paramref name="data"/> in place; a string under a <c>stack</c> key gets the stack cap, one under a <c>text</c>
    /// key the Script Dialog cap, and one under a <c>data</c> key the Script Report cap.
    /// </summary>
    public void Data(JsonNode? data, ref bool truncated, string? key = null)
    {
        switch (data)
        {
            case JsonValue leaf when leaf.TryGetValue(out string? text):
                leaf.ReplaceWith(Scrub(text, key switch { "stack" => MaxStackBytes, "text" => MaxDialogTextBytes, "data" => MaxReportDataBytes, _ => MaxFieldBytes }, ref truncated));
                break;
            case JsonObject obj:
                foreach ((string name, JsonNode? value) in obj.ToList())
                    Data(value, ref truncated, name);
                break;
            case JsonArray array:
                foreach (JsonNode? item in array.ToList())
                    Data(item, ref truncated, key);
                break;
        }
    }

    /// <summary>Redacts each registered secret and the login token, without cutting.</summary>
    public string Redact(string text)
    {
        foreach (string secret in _secrets)
            text = text.Replace(secret, Redacted, StringComparison.Ordinal);
        if (text.Contains("<pword>", StringComparison.Ordinal))
            text = LoginToken().Replace(text, "${open}" + Redacted + "${close}");
        return text;
    }

    /// <summary>A Script Dialog's message as <c>dialogs</c> returns it: redacted, then cut to its cap.</summary>
    public string DialogText(string text)
    {
        bool truncated = false;
        return Scrub(text, MaxDialogTextBytes, ref truncated);
    }

    private string Scrub(string text, int maxBytes, ref bool truncated) => Cut(Redact(text), maxBytes, ref truncated);

    /// <summary>
    /// The login token in the game's own trace of its login: <c>&lt;pword&gt;&lt;![CDATA[…]]&gt;&lt;/pword&gt;</c>, or without CDATA,
    /// or to the end of the text when a cut or split line lost the closing tag.
    /// </summary>
    [GeneratedRegex(@"(?<open><pword>(?:<!\[CDATA\[)?).*?(?:(?<close>(?:\]\]>)?</pword>)|\z)", RegexOptions.Singleline)]
    private static partial Regex LoginToken();

    private static string Cut(string text, int maxBytes, ref bool truncated)
    {
        // Every char is at most 3 UTF-8 bytes (a surrogate pair is 4 for 2), so shorter strings always fit.
        if (text.Length * 3 <= maxBytes)
            return text;

        Span<byte> buffer = maxBytes <= MaxFieldBytes ? stackalloc byte[maxBytes] : new byte[maxBytes];
        // FromUtf16 never splits a surrogate pair: it stops before a character that doesn't fit whole.
        Utf8.FromUtf16(text, buffer, out int charsRead, out _, replaceInvalidSequences: true, isFinalBlock: true);
        if (charsRead == text.Length)
            return text;

        truncated = true;
        return text[..charsRead];
    }
}
