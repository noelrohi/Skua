using System.Text.Json;
using System.Text.Json.Nodes;

namespace Skua.Control;

/// <summary>
/// <c>&lt;SkuaDIR&gt;/Skua.settings.json</c>, which Core's settings also hold: the Control Surface reads single settings from it, and changes
/// one at a time under Core's lock, keeping the rest of the file.
/// </summary>
internal static class SettingsFile
{
    /// <summary>Core's lock on the settings file, taken across processes.</summary>
    private const string FileMutex = @"Global\Skua.Settings.IO";

    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(10);

    // Core reads the file ignoring the case of its keys.
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    public static string Path(string skuaDir) => System.IO.Path.Combine(skuaDir, "Skua.settings.json");

    /// <summary>The file's JSON object, or null when it is missing, unreadable or not a JSON object.</summary>
    public static JsonObject? Read(string skuaDir)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path(skuaDir)), NodeOptions) as JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Changes the file's JSON object (an empty one when there is no file) and saves it, under Core's lock.</summary>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.InvalidArgument"/> when the settings file isn't a JSON object, and <see cref="ErrorCode.Busy"/> when another process
    /// holds the file's lock for 10 s.
    /// </exception>
    public static void Update(string skuaDir, Action<JsonObject> change) => UpdateFile(Path(skuaDir), change);

    /// <summary>
    /// Changes the JSON object in the settings file at <paramref name="path"/> (an empty one when there is no file) and saves it, under Core's
    /// lock, so a file an app is using and a copied one are changed alike.
    /// </summary>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.InvalidArgument"/> when the file isn't a JSON object, and <see cref="ErrorCode.Busy"/> when another process holds
    /// the lock for 10 s.
    /// </exception>
    public static void UpdateFile(string path, Action<JsonObject> change)
    {
        using Mutex mutex = new(false, FileMutex);
        bool held;
        try
        {
            held = mutex.WaitOne(MutexTimeout);
        }
        catch (AbandonedMutexException)
        {
            held = true;
        }
        if (!held)
            throw new ControlException(ErrorCode.Busy, $"Another Skua process holds {path} locked; try again.");
        try
        {
            JsonObject root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path), NodeOptions) as JsonObject
                    ?? throw new ControlException(ErrorCode.InvalidArgument, $"{path} isn't a JSON object; fix or delete it.");
            }
            catch (FileNotFoundException)
            {
                root = new JsonObject(NodeOptions);
            }
            catch (JsonException e)
            {
                throw new ControlException(ErrorCode.InvalidArgument, $"{path} isn't valid JSON ({e.Message}); fix or delete it.");
            }

            change(root);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            string temporary = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>The object under <paramref name="key"/>, added when it is missing or not an object.</summary>
    public static JsonObject Section(JsonObject root, string key)
    {
        if (root[key] is not JsonObject section)
            root[key] = section = new JsonObject(NodeOptions);
        return section;
    }
}
