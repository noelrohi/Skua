using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Skua.Core.Models;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Westwind.Scripting;

namespace Skua.Core;

/// <summary>
/// "Slightly" modified compiler based on Westwind.Scripting (https://github.com/RickStrahl/Westwind.Scripting)
/// </summary>
public class Compiler : CSharpScriptExecution
{
    private const int _maxCachedAssemblies = 1024;
    private static readonly string _cacheDirectory = ScriptCompileCache.Directory;
    private static readonly TimeSpan _cacheExpiration = TimeSpan.FromDays(7);
    private static readonly TimeSpan _cleanupThrottle = TimeSpan.FromMinutes(5);
    private static DateTime _lastCleanupTime = DateTime.MinValue;
    private static readonly object _cleanupLock = new();
    private static readonly CSharpCompilationOptions _compilationOptions = new(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        concurrentBuild: false,
        deterministic: true,
        reportSuppressedDiagnostics: false);
    private static readonly CSharpParseOptions _parseOptions = OperatingSystem.IsMacOS()
        ? CSharpParseOptions.Default.WithPreprocessorSymbols("MACOS")
        : CSharpParseOptions.Default;
    private readonly object _namespaceCacheLock = new();
    private string? _cachedNamespacePrefix = null;
    private int _lastNamespaceHash = 0;

    private static readonly ConcurrentDictionary<int, string> _compiledRegistry = new();
    private static readonly ConcurrentDictionary<string, Assembly> _loadedAssemblies = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsCompiled(int hash) => _compiledRegistry.ContainsKey(hash) || TryLoadFromDiskCache(hash, null) != null;

    public static bool IsLoaded(string assemblyPath) => _loadedAssemblies.ContainsKey(assemblyPath);

    public static void RegisterCompiled(int hash, string cachePath)
    {
        _compiledRegistry[hash] = cachePath;
    }

    public static void RegisterLoaded(string assemblyPath, Assembly assembly)
    {
        _loadedAssemblies[assemblyPath] = assembly;
    }

    public static void ClearSessionRegistries()
    {
        _loadedAssemblies.Clear();
    }

    /// <summary>
    /// This method compiles a class and hands back a
    /// dynamic reference to that class that you can
    /// call members on.
    ///
    /// Must have include parameterless ctor()
    /// </summary>
    /// <param name="code">Fully self-contained C# class</param>
    /// <param name="cacheHash">Optional hash for disk cache lookup</param>
    /// <param name="loadContext">Optional AssemblyLoadContext for loading the assembly</param>
    /// <param name="scriptName">Optional script name for cache file naming</param>
    /// <returns>Instance of that class or null</returns>
    [RequiresUnreferencedCode("This method may require code that cannot be statically analyzed for trimming. Use with caution.")]
    public new dynamic? CompileClass(string code, int? cacheHash = null, ScriptLoadContext? loadContext = null, string? scriptName = null)
    {
        Type? type = CompileClassToType(code, cacheHash, loadContext, scriptName);
        if (type == null)
            return null;

        GeneratedClassName = type.Name;
        GeneratedNamespace = type.Namespace;

        return CreateInstance();
    }

    /// <summary>
    /// This method compiles a class and hands back a
    /// dynamic reference to that class that you can
    /// call members on.
    /// </summary>
    /// <param name="code">Fully self-contained C# class</param>
    /// <param name="cacheHash">Optional hash for disk cache lookup</param>
    /// <param name="loadContext">Optional AssemblyLoadContext for loading the assembly</param>
    /// <param name="scriptName">Optional script name for cache file naming</param>
    /// <returns>Instance of that class or null</returns>
    [RequiresUnreferencedCode("This method may require code that cannot be statically analyzed for trimming. Use with caution.")]
    public new Type? CompileClassToType(string code, int? cacheHash = null, ScriptLoadContext? loadContext = null, string? scriptName = null)
    {
        int hash = cacheHash ?? code.GetHashCode();

        if (loadContext != null || !CachedAssemblies.ContainsKey(hash))
        {
            string? cachedAssemblyPath = TryLoadFromDiskCache(hash, scriptName);

            if (cachedAssemblyPath != null)
            {
                try
                {
                    if (loadContext != null)
                    {
                        using FileStream stream = File.OpenRead(cachedAssemblyPath);
                        Assembly = loadContext.LoadFromStream(stream);
                    }
                    else
                    {
                        Assembly = Assembly.LoadFrom(cachedAssemblyPath);
                    }
                    RegisterCompiled(hash, cachedAssemblyPath);
                    RegisterLoaded(cachedAssemblyPath, Assembly);
                }
                catch
                {
                    // Compiled again below, which replaces it.
                    cachedAssemblyPath = null;
                }
            }

            if (cachedAssemblyPath == null)
            {
                code = PrependNamespaces(code);
                GeneratedClassCode = code;
                string diskCachePath = GetDiskCachePath(hash, scriptName);

                if (!CompileOrWaitForAssembly(code, diskCachePath, loadContext))
                    return null;

                RegisterCompiled(hash, diskCachePath);
                RegisterLoaded(diskCachePath, Assembly);
            }

            if (loadContext == null)
            {
                lock (CachedAssemblies)
                {
                    if (CachedAssemblies.Count >= _maxCachedAssemblies)
                    {
                        int oldestKey = CachedAssemblies.Keys.Min();
                        CachedAssemblies.TryRemove(oldestKey, out _);
                    }

                    CachedAssemblies[hash] = Assembly;
                }
            }
        }
        else
        {
            Assembly = CachedAssemblies[hash];
        }

        Type? firstType = Assembly.ExportedTypes.FirstOrDefault();
        if (firstType == null)
            throw new InvalidOperationException($"Assembly '{Assembly.FullName}' contains no exported types.");
        return firstType;
    }

    /// <summary>
    /// <para>
    /// Compiles a class and creates an assembly from the compiled class.</para>
    /// <para>
    /// Assembly is stored on the `.Assembly` property. Use `noLoad()`
    /// to bypass loading of the assembly
    /// </para>
    /// <para>Must include parameterless ctor()</para>
    /// </summary>
    /// <param name="source">Source code</param>
    /// <param name="noLoad">if set doesn't load the assembly (useful only when OutputAssembly is set)</param>
    /// <returns></returns>
    [RequiresUnreferencedCode("This method may require code that cannot be statically analyzed for trimming. Use with caution.")]
    public new bool CompileAssembly(string source, bool noLoad = false)
    {
        ClearErrors();
        string sourceWithNamespaces = PrependNamespaces(source);

        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceWithNamespaces.Trim(), _parseOptions);

        CSharpCompilation compilation = CSharpCompilation.Create(GeneratedClassName + ".cs")
            .WithOptions(_compilationOptions)
            .WithReferences(References)
            .AddSyntaxTrees(tree);

        if (SaveGeneratedCode)
            GeneratedClassCode = tree.ToString();

        bool isFileAssembly = false;
        Stream? codeStream = null;
        if (string.IsNullOrEmpty(OutputAssembly))
        {
            codeStream = new MemoryStream();
        }
        else
        {
            codeStream = new FileStream(OutputAssembly, FileMode.Create, FileAccess.Write);
            isFileAssembly = true;
        }

        using (codeStream)
        {
            EmitResult? compilationResult = null;
            if (CompileWithDebug)
            {
                const DebugInformationFormat debugOptions = DebugInformationFormat.Embedded;
                compilationResult = compilation.Emit(codeStream, options: new EmitOptions(debugInformationFormat: debugOptions));
            }
            else
            {
                compilationResult = compilation.Emit(codeStream);
            }

            if (!compilationResult.Success)
            {
                StringBuilder sb = new();
                foreach (Diagnostic diag in
                    compilationResult.Diagnostics
                        .Where(diagnostic => diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    sb.AppendLine(diag.ToString());
                }

                ErrorType = ExecutionErrorTypes.Compilation;
                ErrorMessage = sb.ToString();
                SetErrors(new ApplicationException(ErrorMessage));
                return false;
            }

            if (!noLoad)
            {
                Assembly = !isFileAssembly ? Assembly.Load(((MemoryStream)codeStream).ToArray()) : Assembly.LoadFrom(OutputAssembly);
            }
        }

        return true;
    }

    private void ClearErrors()
    {
        LastException = null;
        Error = false;
        ErrorMessage = null;
        ErrorType = ExecutionErrorTypes.None;
    }

    private void SetErrors(Exception ex)
    {
        Error = true;
        LastException = ex.GetBaseException();
        ErrorMessage = LastException.Message;

        if (ThrowExceptions)
            throw LastException;
    }

    private string PrependNamespaces(string source)
    {
        if (Namespaces == null || Namespaces.Count == 0)
            return source;

        int currentHash = Namespaces.Count > 0 ? string.Join(";", Namespaces).GetHashCode() : 0;

        lock (_namespaceCacheLock)
        {
            if (_cachedNamespacePrefix == null || _lastNamespaceHash != currentHash)
            {
                StringBuilder sb = new(Namespaces.Count * 40);
                foreach (string ns in Namespaces)
                {
                    sb.Append("using ");
                    sb.Append(ns);
                    sb.AppendLine(";");
                }
                sb.AppendLine();
                _cachedNamespacePrefix = sb.ToString();
                _lastNamespaceHash = currentHash;
            }

            return _cachedNamespacePrefix + source;
        }
    }

    /// <summary>
    /// Clears the cached assemblies to free memory
    /// </summary>
    public static void ClearAssemblyCache()
    {
        CachedAssemblies?.Clear();
    }

    private static string? TryLoadFromDiskCache(int hash, string? scriptName = null)
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
                return null;

            string fileName = string.IsNullOrEmpty(scriptName) ? $"{hash}.dll" : $"{hash}-{scriptName}.dll";
            string cachedPath = Path.Combine(_cacheDirectory, fileName);

            // One that doesn't read is a miss; compiling it replaces it, while another process may still be loading the old one.
            return ScriptCompileCache.IsValidAssembly(cachedPath) ? cachedPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetDiskCachePath(int hash, string? scriptName = null)
    {
        string fileName = string.IsNullOrEmpty(scriptName) ? $"{hash}.dll" : $"{hash}-{scriptName}.dll";
        return Path.Combine(_cacheDirectory, fileName);
    }

    private bool CompileOrWaitForAssembly(string source, string outputPath, ScriptLoadContext? loadContext)
    {
        // Across processes too: an Engine compiling the same entry waits for the other's and loads it.
        string mutexName = $"Global\\Skua_Compile_{Path.GetFileName(outputPath).Replace(".dll", "").Replace("-", "_")}";
        using Mutex compilationMutex = new(false, mutexName);

        try
        {
            if (!compilationMutex.WaitOne(TimeSpan.FromMinutes(2)))
            {
                ErrorType = ExecutionErrorTypes.Compilation;
                ErrorMessage = "Timeout waiting for script compilation to complete.";
                SetErrors(new ApplicationException(ErrorMessage));
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // A process died holding it; this one owns it now.
        }

        try
        {
            if (ScriptCompileCache.IsValidAssembly(outputPath))
            {
                try
                {
                    LoadCompiled(outputPath, null, loadContext);
                    return true;
                }
                catch
                {
                    // Compiled again below, which replaces it.
                }
            }

            if (!CompileAssemblyToBytes(source, outputPath, out byte[] bytes))
                return false;

            ScriptCompileCache.Write(outputPath, bytes);
            LoadCompiled(outputPath, bytes, loadContext);
            return true;
        }
        finally
        {
            compilationMutex.ReleaseMutex();
        }
    }

    /// <param name="bytes">The assembly just compiled, which loads without reading the file back; null to read it.</param>
    private void LoadCompiled(string path, byte[]? bytes, ScriptLoadContext? loadContext)
    {
        if (loadContext == null)
        {
            Assembly = Assembly.LoadFrom(path);
            return;
        }

        using Stream stream = bytes != null ? new MemoryStream(bytes) : File.OpenRead(path);
        Assembly = loadContext.LoadFromStream(stream);
    }

    /// <param name="outputPath">The cache entry, which names the assembly.</param>
    private bool CompileAssemblyToBytes(string source, string outputPath, out byte[] bytes)
    {
        ClearErrors();
        bytes = [];

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source.Trim(), _parseOptions);

        string fileName = Path.GetFileNameWithoutExtension(outputPath);
        int lastDash = fileName.LastIndexOf('-');
        string assemblyName = lastDash > 0 ? fileName[(lastDash + 1)..] : fileName;

        CSharpCompilation compilation = CSharpCompilation.Create(assemblyName)
            .WithOptions(_compilationOptions)
            .WithReferences(References)
            .AddSyntaxTrees(tree);

        if (SaveGeneratedCode)
            GeneratedClassCode = tree.ToString();

        using MemoryStream codeStream = new();
        EmitResult? compilationResult = null;
        if (CompileWithDebug)
        {
            const DebugInformationFormat debugOptions = DebugInformationFormat.Embedded;
            compilationResult = compilation.Emit(codeStream, options: new EmitOptions(debugInformationFormat: debugOptions));
        }
        else
        {
            compilationResult = compilation.Emit(codeStream);
        }

        if (!compilationResult.Success)
        {
            StringBuilder sb = new();
            foreach (Diagnostic diag in
                compilationResult.Diagnostics
                    .Where(diagnostic => diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error))
            {
                sb.AppendLine(diag.ToString());
            }

            ErrorType = ExecutionErrorTypes.Compilation;
            ErrorMessage = sb.ToString();
            SetErrors(new ApplicationException(ErrorMessage));
            return false;
        }

        bytes = codeStream.ToArray();
        return true;
    }

    /// <summary>
    /// Deletes expired, superseded and excess entries, at most every few minutes, and only while no process compiles; a compile calls it once
    /// it no longer uses the cache.
    /// </summary>
    public static void CleanCache()
    {
        lock (_cleanupLock)
        {
            if ((DateTime.Now - _lastCleanupTime) < _cleanupThrottle)
                return;
            _lastCleanupTime = DateTime.Now;
        }

        Task.Run(() =>
        {
            if (!ScriptCompileCache.TryClean(CleanupOldCachedAssemblies))
            {
                // Another process compiles; the next compile tries again.
                lock (_cleanupLock)
                    _lastCleanupTime = DateTime.MinValue;
            }
        });
    }

    private static void CleanupOldCachedAssemblies()
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
                return;

            string[] filePaths = Directory.GetFiles(_cacheDirectory, "*.dll");
            FileInfo[] files = new FileInfo[filePaths.Length];

            for (int i = 0; i < filePaths.Length; i++)
            {
                files[i] = new FileInfo(filePaths[i]);
            }

            DateTime now = DateTime.Now;
            HashSet<FileInfo> filesToDelete = [];

            foreach (FileInfo file in files)
            {
                string scriptName = GetScriptNameFromCacheFile(file.Name);
                if (scriptName.Contains("Core", StringComparison.OrdinalIgnoreCase))
                    continue;

                TimeSpan age = now - file.LastWriteTimeUtc;
                if (age > _cacheExpiration)
                {
                    filesToDelete.Add(file);
                }
            }

            Dictionary<string, List<FileInfo>> scriptGroups = new();
            foreach (FileInfo file in files)
            {
                if (filesToDelete.Contains(file))
                    continue;

                string scriptName = GetScriptNameFromCacheFile(file.Name);
                if (!scriptGroups.ContainsKey(scriptName))
                {
                    scriptGroups[scriptName] = [];
                }
                scriptGroups[scriptName].Add(file);
            }

            foreach (KeyValuePair<string, List<FileInfo>> kvp in scriptGroups)
            {
                if (kvp.Value.Count > 1)
                {
                    FileInfo newest = kvp.Value.MaxBy(f => f.LastWriteTimeUtc)!;
                    foreach (FileInfo file in kvp.Value)
                    {
                        if (file != newest)
                            filesToDelete.Add(file);
                    }
                }
            }

            if (files.Length - filesToDelete.Count >= _maxCachedAssemblies)
            {
                List<FileInfo> remaining = [.. files.Except(filesToDelete)
                    .Where(f => !GetScriptNameFromCacheFile(f.Name).Contains("Core", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.LastWriteTimeUtc)];
                int toRemove = remaining.Count - _maxCachedAssemblies + 1;

                for (int i = 0; i < toRemove && i < remaining.Count; i++)
                {
                    filesToDelete.Add(remaining[i]);
                }
            }

            foreach (FileInfo file in filesToDelete)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    /* ignored */
                }
            }
        }
        catch
        {
        }
    }

    private static string GetScriptNameFromCacheFile(string fileName)
    {
        ReadOnlySpan<char> nameWithoutExt = Path.GetFileNameWithoutExtension(fileName.AsSpan());
        int lastDashIndex = nameWithoutExt.LastIndexOf('-');

        return lastDashIndex <= 0 ? string.Empty : new string(nameWithoutExt[(lastDashIndex + 1)..]);
    }
}
