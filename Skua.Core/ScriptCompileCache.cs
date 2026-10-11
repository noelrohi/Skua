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
/// deletes entries, and only while no process compiles: a compile holds the lock file shared, the cleanup holds it exclusively.
/// </remarks>
public static class ScriptCompileCache
{
    private const string _lockFileName = ".lock";
    private const string _tempExtension = ".tmp";
    private static readonly TimeSpan _sharedWait = TimeSpan.FromSeconds(30);

    public static string Directory { get; } = Path.Combine(ClientFileSources.SkuaScriptsDIR, "Cached-Scripts");

    /// <summary>
    /// What every entry depends on besides its sources: the Skua assemblies Scripts compile against and the runtime, so a Skua update recompiles.
    /// </summary>
    public static byte[] BuildStamp { get; } = ComputeBuildStamp();

    /// <summary>
    /// Holds the cache for a compile, so the cleanup deletes no entry it reads or references until it is disposed. Waits while a cleanup runs.
    /// </summary>
    public static IDisposable Use()
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                // FileShare other than None takes a shared lock, which another process's exclusive one refuses until it closes.
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (waited.Elapsed > _sharedWait)
                {
                    // A cleanup is never this long; better to compile unguarded than to fail the Script.
                    Trace.WriteLine($"Compiling without the Script cache lock: {e.Message}");
                    return new NoLock();
                }
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>Runs <paramref name="clean"/> holding the cache exclusively, unless a process is compiling.</summary>
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
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = $"{path}.{Guid.NewGuid():N}{_tempExtension}";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Delete(temp);
            // Windows refuses to replace a file another process has open; that entry has the same key, so it is as good.
            if (!File.Exists(path))
                throw;
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

    private sealed class NoLock : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
