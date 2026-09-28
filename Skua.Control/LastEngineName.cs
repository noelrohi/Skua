namespace Skua.Control;

/// <summary>
/// The Engine Name the Mac App last served, which it serves again when started without <c>--name</c>, as from the Dock or Finder. Only launches
/// without <c>--account</c> count: the Skua Manager's launches name an account's own app, which the Manager starts again itself.
/// </summary>
public static class LastEngineName
{
    public const string FileName = "last-engine-name";

    /// <summary>The name last remembered in <paramref name="skuaDir"/>, or <see cref="EngineName.Default"/> when there is none or it is unreadable.</summary>
    public static string Read(string skuaDir)
    {
        try
        {
            string name = File.ReadAllText(Path.Combine(skuaDir, FileName)).Trim();
            return EngineName.IsValid(name) ? name : EngineName.Default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return EngineName.Default;
        }
    }

    /// <summary>Remembers the name the app serves for <paramref name="arguments"/>, unless the Skua Manager launched it; a failure goes unnoticed.</summary>
    public static void Remember(string skuaDir, AppArguments arguments)
    {
        if (arguments.Manager || arguments.Account is not null)
            return;
        try
        {
            Directory.CreateDirectory(skuaDir);
            File.WriteAllText(Path.Combine(skuaDir, FileName), arguments.Name + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
