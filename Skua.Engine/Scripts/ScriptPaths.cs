using Skua.Control;
using Skua.Core.Models;

namespace Skua.Engine.Scripts;

/// <summary>How the Control Surface names a Script: by its path in the Script Source, or by an absolute path for one written elsewhere.</summary>
internal static class ScriptPaths
{
    /// <summary>Returns the Script's name as reported back, and its file, which must exist.</summary>
    /// <exception cref="StreamJsonRpc.LocalRpcException">
    /// <see cref="ErrorCode.InvalidArgument"/> for an empty name, <see cref="ErrorCode.ScriptNotFound"/> for a missing file.
    /// </exception>
    public static (string Name, string File) Resolve(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            throw RpcErrors.Of(ErrorCode.InvalidArgument, "Name a Script: a path in the Script Source such as Farm/Leveling.cs, or an absolute path.");

        if (Path.IsPathRooted(script))
        {
            string file = Path.GetFullPath(script);
            if (!File.Exists(file))
                throw RpcErrors.Of(ErrorCode.ScriptNotFound, $"There is no Script at {file}.");
            return (Name(file), file);
        }

        string name = script.Replace('\\', '/').TrimStart('/');
        string inSource = Path.GetFullPath(Path.Combine(ClientFileSources.SkuaScriptsDIR, name));
        if (!File.Exists(inSource))
            throw RpcErrors.Of(ErrorCode.ScriptNotFound,
                $"{name} isn't in the Scripts folder; run 'skua scripts update' to download it, or find its path with 'skua scripts search'.");
        return (name, inSource);
    }

    /// <summary>A file's path in the Scripts folder when it is under it, else the absolute path.</summary>
    public static string Name(string file)
    {
        if (file.Length == 0)
            return file;
        string relative = Path.GetRelativePath(ClientFileSources.SkuaScriptsDIR, Path.GetFullPath(file));
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Path.GetFullPath(file) : relative.Replace('\\', '/');
    }
}
