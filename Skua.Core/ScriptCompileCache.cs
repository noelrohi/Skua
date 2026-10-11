using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Utils;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Skua.Core;

/// <summary>
/// The compiled Scripts and includes in <c>Scripts/Cached-Scripts</c>, which every Engine of a data folder shares, each its own process.
/// </summary>
/// <remarks>
/// An entry's name holds a key over everything it was compiled from, so a changed source, include or Skua build makes a new entry rather
/// than invalidating one. Entries are written whole, through a temp file and a rename, so a reader never sees half of one. Only the cleanup
/// deletes entries, and only while no process compiles: a compile holds the lock file shared, the cleanup holds it exclusively (ADR 0009).
/// </remarks>
public static class ScriptCompileCache
{
    private const string _lockFileName = ".lock";
    private const string _tempExtension = ".tmp";
    private static readonly TimeSpan _sharedWait = TimeSpan.FromMinutes(2);

    public static string Directory { get; } = Path.Combine(ClientFileSources.SkuaScriptsDIR, "Cached-Scripts");

    /// <summary>
    /// What every entry depends on besides its sources: the Skua assemblies Scripts compile against and the runtime, so a Skua update recompiles.
    /// </summary>
    public static byte[] BuildStamp { get; } = ComputeBuildStamp();

    /// <summary>
    /// Takes the cache's lock file shared for a compile, so the cleanup, which needs it exclusively, deletes no entry the compile reads or
    /// references until this is disposed. Any number of compiles, in any process, hold it at once; it waits while a cleanup runs.
    /// </summary>
    /// <exception cref="IOException">The lock stayed taken exclusively for two minutes, far longer than a cleanup takes.</exception>
    public static IDisposable Use()
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                // On Unix, .NET takes FileShare.None as an exclusive flock and any other FileShare as a shared one.
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (waited.Elapsed > _sharedWait)
                    throw new IOException($"The Script cache {Directory} stayed locked for {_sharedWait.TotalMinutes:0} minutes, so the Script wasn't compiled: {e.Message}", e);
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>Runs <paramref name="clean"/> holding the cache's lock file exclusively, unless a process is compiling.</summary>
    /// <returns>Whether it ran.</returns>
    public static bool TryClean(Action clean)
    {
        FileStream exclusive;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            exclusive = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        using (exclusive)
        {
            // A temp file no write still holds, since none runs: one a crashed process left behind.
            foreach (string temp in System.IO.Directory.EnumerateFiles(Directory, "*" + _tempExtension))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                }
            }
            clean();
        }
        return true;
    }

    /// <summary>Writes an entry whole: a reader sees the old file or the new one, never part of it.</summary>
    public static void Write(string path, byte[] bytes)
    {
        try
        {
            WriteWhole(path, bytes);
        }
        // Windows refuses to replace a file another process has open; that entry has the same key, so it is as good.
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && File.Exists(path))
        {
        }
    }

    /// <summary>
    /// Writes <paramref name="path"/> through a temp file next to it and a rename, so another process reading it, as an Engine compiling a
    /// Script while another updates the Scripts, sees the old file or the new one, never part of it.
    /// </summary>
    public static void WriteWhole(string path, byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = $"{path}.{Guid.NewGuid():N}{_tempExtension}";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Whether <paramref name="path"/> is a whole assembly.</summary>
    public static bool IsValidAssembly(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The key of an entry compiled from <paramref name="parts"/>, e.g. a source's hash and those of its includes, and the build.</summary>
    public static int Key(IEnumerable<byte[]> parts)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BuildStamp);
        foreach (byte[] part in parts)
            hash.AppendData(part);
        return BitConverter.ToInt32(hash.GetHashAndReset(), 0);
    }

    private static string LockPath => Path.Combine(Directory, _lockFileName);

    private static byte[] ComputeBuildStamp()
    {
        StringBuilder stamp = new(Environment.Version.ToString());
        foreach (Assembly assembly in new[] { typeof(ScriptCompileCache).Assembly, typeof(IScriptInterface).Assembly, typeof(ClientFileSources).Assembly, typeof(RangedObservableCollection<>).Assembly })
            stamp.Append('|').Append(assembly.ManifestModule.ModuleVersionId);
        return SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString()));
    }
}
