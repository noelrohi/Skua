using Skua.Core.ViewModels;

namespace Skua.Avalonia;

/// <summary>Where the Script Repo's search looks: every field, or one of them.</summary>
public enum ScriptSearchScope
{
    All,
    Name,
    Tag,
    Desc,
    File,
}

/// <summary>
/// The Script Repo's search, ported from <c>Skua.WPF/Views/ScriptRepoView.xaml.cs</c>: it keeps the Scripts that match in the chosen
/// scope, best matches first, then by file name.
/// </summary>
/// <remarks>
/// As in <c>skua scripts search</c>, the query's words each have to match, and a Script's file is its path in the Script Source (such as
/// <c>Farm/Leveling.cs</c>). A Script ranks by its weakest word; for one word the ranking is the Windows app's.
/// </remarks>
public static class ScriptRepoSearch
{
    public static IReadOnlyList<ScriptInfoViewModel> Apply(IEnumerable<ScriptInfoViewModel> scripts, string query, ScriptSearchScope scope)
    {
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return scripts.ToList();

        return scripts
            .Where(script => words.All(word => Matches(script, word, scope)))
            .OrderBy(script => words.Max(word => Rank(script, word, scope)))
            .ThenBy(script => script.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool Matches(ScriptInfoViewModel script, string word, ScriptSearchScope scope) =>
        (scope is ScriptSearchScope.All or ScriptSearchScope.Name && Contains(script.Info.Name, word))
        || (scope is ScriptSearchScope.All or ScriptSearchScope.Desc && Contains(script.Info.Description, word))
        || (scope is ScriptSearchScope.All or ScriptSearchScope.Tag && script.InfoTags.Any(tag => Contains(tag, word)))
        || (scope is ScriptSearchScope.All or ScriptSearchScope.File && Contains(script.FilePath, word));

    /// <summary>Lower is better: an exact tag or file, then a name, a description, then the rest.</summary>
    private static int Rank(ScriptInfoViewModel script, string word, ScriptSearchScope scope)
    {
        switch (scope)
        {
            case ScriptSearchScope.Tag:
                foreach (string tag in script.InfoTags)
                {
                    if (string.Equals(tag, word, StringComparison.OrdinalIgnoreCase))
                        return -1;
                    if (tag.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                        return 0;
                    if (Contains(tag, word))
                        return 1;
                }
                return 2;
            case ScriptSearchScope.File:
                string file = script.FilePath;
                if (string.Equals(file, word, StringComparison.OrdinalIgnoreCase))
                    return -1;
                if (file.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                    return 0;
                return Contains(file, word) ? 1 : 2;
            default:
                if (Contains(script.Info.Name, word))
                    return -1;
                if (Contains(script.Info.Description, word))
                    return 0;
                if (scope is ScriptSearchScope.All && Contains(script.FilePath, word))
                    return 1;
                return 2;
        }
    }

    private static bool Contains(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;
}
