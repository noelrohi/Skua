using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Skua.Control;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Models;
using Skua.Core.Scripts;
using Skua.Engine.Scripts;
using StreamJsonRpc;

namespace Skua.Engine;

/// <summary>What <c>skua scripts check</c> compiled.</summary>
/// <param name="Script">The Script's file.</param>
/// <param name="ScriptsFolder">The folder its includes were found in: the data folder's Scripts, or the Scripts checkout the Script is in.</param>
/// <param name="Includes">The files it includes, directly or through another include.</param>
/// <param name="Warnings">An include that doesn't exist, which Core skips as the Engine does, at its file and line.</param>
public sealed record ScriptCheckResult(string Script, string ScriptsFolder, IReadOnlyList<string> Includes, IReadOnlyList<string> Warnings);

/// <summary>
/// <c>skua scripts check</c>: compiles a Script and its includes as a start would, through Core's compiler, in the caller's process with no
/// Engine, and reports each error at its file and line.
/// </summary>
/// <remarks>
/// It compiles copies, in a folder it makes this process's data folder, so it writes nothing next to the Scripts it checks or into the data
/// folder's compile cache. Each copy carries <c>#line</c> directives, so an error points at the original file and line although Core drops the
/// <c>//cs_</c> lines before it compiles. It must run before anything in the process reads Core's data folder.
/// </remarks>
public static class ScriptCheck
{
    /// <param name="path">A Script's file, absolute or from the working directory, or else its path in the data folder's Scripts.</param>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.ScriptNotFound"/>; <see cref="ErrorCode.InvalidArgument"/> for a file in no Scripts folder;
    /// <see cref="ErrorCode.CompileFailed"/> with each error as a diagnostic.
    /// </exception>
    public static ScriptCheckResult Run(string path, string skuaDir)
    {
        string dataScripts = Path.Combine(skuaDir, "Scripts");
        string script = Find(path, dataScripts);
        string root = ScriptsFolder(script, dataScripts);
        Closure closure = Includes(script, root);

        string stage = Directory.CreateTempSubdirectory("skua-check-").FullName;
        try
        {
            // A file in the Scripts folder keeps its place in it; an include from outside it gets a folder of its own.
            Dictionary<string, string> staged = closure.Files.Prepend(script).Concat(closure.References).Select((file, i) => (file, staged:
                    InFolder(file, root) ? Path.Combine(stage, "Scripts", Path.GetRelativePath(root, file)) : Path.Combine(stage, "External", $"{i}", Path.GetFileName(file))))
                .ToDictionary(f => f.file, f => f.staged);
            foreach ((string file, string copy) in staged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                if (closure.Includes.TryGetValue(file, out Dictionary<int, string>? includes))
                    File.WriteAllText(copy, Staged(File.ReadAllLines(file), file, includes, staged));
                else
                    File.Copy(file, copy);
            }
            if (Directory.Exists(Path.Combine(skuaDir, "plugins")))
                Directory.CreateSymbolicLink(Path.Combine(stage, "plugins"), Path.Combine(skuaDir, "plugins"));

            Compile(staged[script], stage, script, closure.Warnings,
                message => staged.OrderByDescending(f => f.Value.Length).Aggregate(message, (text, f) => text.Replace(f.Value, f.Key, StringComparison.Ordinal)));
            return new ScriptCheckResult(script, root, closure.Files, closure.Warnings);
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    /// <summary>The file at <paramref name="path"/> from the working directory, or else in the data folder's Scripts.</summary>
    private static string Find(string path, string dataScripts)
    {
        string file = Path.GetFullPath(path);
        if (File.Exists(file))
            return file;
        string inDataFolder = Path.GetFullPath(Path.Combine(dataScripts, path));
        if (!Path.IsPathRooted(path) && File.Exists(inDataFolder))
            return inDataFolder;
        throw new ControlException(ErrorCode.ScriptNotFound, $"There is no Script at {path}: neither {file} nor {inDataFolder} exists.");
    }

    /// <summary>The data folder's Scripts when the Script is in them, or else the Scripts checkout it is in: the nearest folder with a <c>scripts.json</c>.</summary>
    private static string ScriptsFolder(string script, string dataScripts)
    {
        if (script.StartsWith(Path.GetFullPath(dataScripts) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return Path.GetFullPath(dataScripts);
        for (DirectoryInfo? folder = Directory.GetParent(script); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "scripts.json")))
                return folder.FullName;
        }
        throw new ControlException(ErrorCode.InvalidArgument,
            $"{script} is neither in {dataScripts} nor in a Scripts checkout (a folder with a scripts.json), so its includes can't be found.");
    }

    /// <param name="Files">The included files, in the order they were found.</param>
    /// <param name="References">The assemblies in the Scripts folder that a <c>//cs_ref</c> names; Core finds any other itself.</param>
    /// <param name="Includes">For the Script and each include, the file each of its <c>//cs_include</c> lines names, by line index.</param>
    /// <param name="Warnings">An include that doesn't exist, at its line.</param>
    private sealed record Closure(
        List<string> Files, List<string> References, Dictionary<string, Dictionary<int, string>> Includes, List<string> Warnings);

    /// <summary>
    /// Every file the Script includes, as Core finds them: from each <c>//cs_</c> line of the Script, and of an include up to its first
    /// <c>using</c> line, resolved as Core resolves them, in the Scripts folder or else as a path of their own.
    /// </summary>
    private static Closure Includes(string script, string root)
    {
        Closure closure = new([], [], [], []);
        Queue<string> pending = new([script]);
        while (pending.TryDequeue(out string? file))
        {
            Dictionary<int, string> includes = closure.Includes[file] = [];
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (file != script && line.StartsWith("using", StringComparison.Ordinal))
                    break;
                if (!line.StartsWith("//cs_", StringComparison.Ordinal) || line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries) is not [string directive, string target])
                    continue;
                string? resolved = ScriptDirectives.Resolve(root, target) is { } found ? Path.GetFullPath(found) : null;
                switch (directive[5..])
                {
                    case "include" when resolved is null:
                        closure.Warnings.Add($"{file}({i + 1},1): warning: the include {target} doesn't exist in {root}, so it is skipped");
                        break;
                    case "include":
                        includes[i] = resolved;
                        if (resolved != script && !closure.Includes.ContainsKey(resolved) && !pending.Contains(resolved))
                        {
                            closure.Files.Add(resolved);
                            pending.Enqueue(resolved);
                        }
                        break;
                    case "ref" when resolved is not null && InFolder(resolved, root) && !closure.References.Contains(resolved):
                        closure.References.Add(resolved);
                        break;
                }
            }
        }
        return closure;
    }

    private static bool InFolder(string file, string folder) => file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// The staged copy of <paramref name="file"/>: each <c>//cs_include</c> line names its include's staged copy, and a <c>#line</c> directive at
    /// the top and after each <c>//cs_</c> line, which Core removes, keeps every line's number in <paramref name="file"/>.
    /// </summary>
    private static string Staged(string[] lines, string file, Dictionary<int, string> includes, Dictionary<string, string> staged)
    {
        List<string> marked = [$"#line 1 \"{file}\""];
        for (int i = 0; i < lines.Length; i++)
        {
            marked.Add(includes.TryGetValue(i, out string? include) ? $"//cs_include {staged[include]}" : lines[i]);
            if (lines[i].Trim().StartsWith("//cs_", StringComparison.Ordinal))
                marked.Add($"#line {i + 2} \"{file}\"");
        }
        return string.Join('\n', marked);
    }

    /// <summary>Compiles the staged Script with Core's compiler, with <paramref name="stage"/> as Core's data folder.</summary>
    /// <param name="unstage">Turns the staged files' paths in Core's messages back into the originals.</param>
    /// <param name="warnings">Lead the errors when it doesn't compile, since a missing include is a likely cause.</param>
    private static void Compile(string staged, string stage, string script, IReadOnlyList<string> warnings, Func<string, string> unstage)
    {
        Environment.SetEnvironmentVariable(ClientFileSources.SkuaDirEnvironmentVariable, stage);
        if (ClientFileSources.SkuaDIR != stage)
            throw new InvalidOperationException("Core read its data folder before the check could set it.");

        ServiceCollection services = new();
        services.AddCompiler();
        Ioc.Default.ConfigureServices(services.BuildServiceProvider());
        ScriptManager manager = new(new NoLog(), Unavailable<IScriptInterface>(), Unavailable<IScriptHandlers>(), Unavailable<IScriptSkill>(),
            Unavailable<IScriptDrop>(), Unavailable<IScriptWait>(), Unavailable<IAuraMonitorService>());
        manager.SetLoadedScript(staged);
        LocalRpcException? failure = null;
        try
        {
            if (manager.Compile(File.ReadAllText(staged)) is null)
                failure = RpcErrors.Of(ErrorCode.CompileFailed, $"{script} compiled to nothing; it needs a public class.");
        }
        catch (Exception e) when (CompileErrors(e) is { } errors)
        {
            failure = CompileFailure.Of(script, string.Join('\n', warnings.Append(unstage(errors))));
        }
        if (failure is not null)
            throw RpcErrors.ToControlException(failure);
    }

    /// <summary>Core's compile errors: the Script's own, or its includes', which Core compiles in parallel and throws together.</summary>
    private static string? CompileErrors(Exception e) => e switch
    {
        ScriptCompileException compile => compile.Message,
        AggregateException all when all.Flatten().InnerExceptions.All(inner => inner is ScriptCompileException) =>
            string.Join('\n', all.Flatten().InnerExceptions.Select(inner => inner.Message)),
        _ => null,
    };

    private static Lazy<T> Unavailable<T>() => new(() => throw new InvalidOperationException($"A check only compiles; it has no {typeof(T).Name}."));

    /// <summary>Core's compile logs nothing; a check has no log to write to anyway.</summary>
    private sealed class NoLog : ILogService
    {
        public void DebugLog(string message) { }

        public void ScriptLog(string message) { }

        public void FlashLog(string message) { }

        public void ClearLog(LogType logType) { }

        public List<string> GetLogs(LogType logType) => [];
    }
}
