namespace Skua.Core.Scripts;

/// <summary>How a Script's <c>//cs_include</c> and <c>//cs_ref</c> lines name files, for Core's compiles and <c>skua scripts check</c> alike.</summary>
public static class ScriptDirectives
{
    /// <summary>
    /// The file a directive names: <paramref name="target"/> in <paramref name="scriptsFolder"/>, with any <c>Scripts/</c> in it dropped, or
    /// else <paramref name="target"/> as a path of its own, absolute or from the working directory; null when neither exists.
    /// </summary>
    public static string? Resolve(string scriptsFolder, string target)
    {
        string local = Path.Combine(scriptsFolder, target.Replace("Scripts/", ""));
        if (File.Exists(local))
            return local;
        return File.Exists(target) ? target : null;
    }
}
