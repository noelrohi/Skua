using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Skua.Control;

/// <summary>
/// The Script Source: <c>ScriptSource</c> under <c>shared</c> in <c>&lt;SkuaDIR&gt;/Skua.settings.json</c>, which Core's settings also hold.
/// Unset, the macOS Engine uses <see cref="Default"/>. <c>skua scripts source</c> changes it while an Engine runs, so the Engine reads it afresh
/// every time it reaches the Script Source.
/// </summary>
public static partial class ScriptSourceSetting
{
    public const string Key = "ScriptSource";

    /// <summary>
    /// The macOS default: the fork of <c>auqw/Scripts@Skua</c> with the <c>skua-macos</c> patches. Upstream's <c>CoreBots.cs</c> uses Windows Forms,
    /// so every Script that includes it fails to compile on macOS. The Windows app keeps Core's default, <c>auqw/Scripts@Skua</c>.
    /// </summary>
    public static ScriptSourceDto Default { get; } = new("noelrohi", "Scripts", "Skua");

    /// <summary>The Script Source the setting names, or null when the file or the setting is missing or unreadable; a blank part is the default's.</summary>
    public static ScriptSourceDto? Read(string skuaDir)
    {
        try
        {
            if (SettingsFile.Read(skuaDir)?["shared"]?[Key] is not JsonObject source)
                return null;
            return new ScriptSourceDto(Part(source, "Owner", Default.Owner), Part(source, "Repo", Default.Repo), Part(source, "Branch", Default.Branch));
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        static string Part(JsonObject source, string key, string fallback) =>
            source[key]?.GetValue<string>() is { } part && !string.IsNullOrWhiteSpace(part) ? part : fallback;
    }

    /// <summary>Sets the Script Source in the settings file, or removes it for the default, keeping the rest of the file, under Core's lock.</summary>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.InvalidArgument"/> when the settings file isn't a JSON object, and <see cref="ErrorCode.Busy"/> when another process
    /// holds the file's lock for 10 s.
    /// </exception>
    public static void Write(string skuaDir, ScriptSourceDto? source) => SettingsFile.Update(skuaDir, root =>
    {
        JsonObject shared = SettingsFile.Section(root, "shared");
        if (source is null)
            shared.Remove(Key);
        else
            shared[Key] = new JsonObject { ["Owner"] = source.Owner, ["Repo"] = source.Repo, ["Branch"] = source.Branch };
    });

    /// <summary>Reads <c>owner/repo@branch</c>, as GitHub allows each part.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> when <paramref name="text"/> isn't one.</exception>
    public static ScriptSourceDto Parse(string text)
    {
        Match match = SourcePattern().Match(text.Trim());
        if (!match.Success)
            throw new ControlException(ErrorCode.InvalidArgument, $"'{text}' isn't a Script Source; give it as owner/repo@branch, e.g. {Format(Default)}.");
        return new ScriptSourceDto(match.Groups["owner"].Value, match.Groups["repo"].Value, match.Groups["branch"].Value);
    }

    public static string Format(ScriptSourceDto source) => $"{source.Owner}/{source.Repo}@{source.Branch}";

    [GeneratedRegex(@"^(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]{1,100})@(?<branch>[^\s~^:?*\[\\]+)$")]
    private static partial Regex SourcePattern();
}
