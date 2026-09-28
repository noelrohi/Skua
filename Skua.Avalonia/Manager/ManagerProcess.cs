using System.Diagnostics;
using Skua.Control;

namespace Skua.Avalonia.Manager;

/// <summary>
/// The one Skua Manager process per data folder (<c>Skua --manager</c>, ADR 0006). It holds <c>&lt;SkuaDIR&gt;/manager.lock</c> while it runs and
/// writes its pid next to it; opening the Manager again brings that process to the front instead of starting another.
/// </summary>
public sealed class ManagerProcess : IDisposable
{
    private readonly EngineLock _lock;
    private readonly string _pidPath;

    private ManagerProcess(EngineLock held, string pidPath)
    {
        _lock = held;
        _pidPath = pidPath;
    }

    /// <summary>Makes this process the data folder's Manager, or returns null when another one is.</summary>
    public static ManagerProcess? TryClaim(string skuaDir)
    {
        Directory.CreateDirectory(skuaDir);
        if (EngineLock.TryAcquire(LockPath(skuaDir)) is not { } held)
            return null;
        string pidPath = PidPath(skuaDir);
        File.WriteAllText(pidPath, Environment.ProcessId.ToString());
        return new ManagerProcess(held, pidPath);
    }

    /// <summary>Brings the running Manager to the front; false when none runs.</summary>
    public static bool ShowRunning(string skuaDir)
    {
        if (!EngineLock.IsHeld(LockPath(skuaDir)) || !int.TryParse(ReadPid(skuaDir), out int pid))
            return false;
        if (!AppInstances.Signal(pid, AppInstances.ShowSignal))
            return false;
        MacApps.Activate(pid);
        return true;
    }

    /// <summary>For the app's menu: brings the running Manager to the front, or starts one from this app's executable.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.EngineUnavailable"/> when it couldn't be started.</exception>
    public static void Open(string skuaDir)
    {
        if (!ShowRunning(skuaDir))
            new AppInstances(skuaDir).Launch(new AppArguments(Manager: true)).Dispose();
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_pidPath);
        }
        catch (IOException)
        {
        }
        _lock.Dispose();
    }

    private static string LockPath(string skuaDir) => Path.Combine(skuaDir, "manager.lock");

    private static string PidPath(string skuaDir) => Path.Combine(skuaDir, "manager.pid");

    private static string? ReadPid(string skuaDir)
    {
        try
        {
            return File.ReadAllText(PidPath(skuaDir)).Trim();
        }
        catch (IOException e)
        {
            Trace.WriteLine($"No Manager pid: {e.Message}");
            return null;
        }
    }
}
