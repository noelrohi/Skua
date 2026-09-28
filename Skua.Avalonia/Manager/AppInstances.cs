using System.Diagnostics;
using System.Runtime.InteropServices;
using Skua.Control;

namespace Skua.Avalonia.Manager;

/// <summary>An Engine running under the data folder, as its <c>hello</c> and <c>status</c> describe it.</summary>
/// <param name="Status">Its status, or null when it didn't answer in time.</param>
public sealed record RunningEngine(EngineEndpoint Endpoint, HelloResult Hello, StatusDto? Status)
{
    public string Name => Endpoint.Name;

    public bool IsApp => Hello.Host == EngineHost.App;
}

/// <summary>
/// The Mac App instances the Skua Manager launches, one app process per Engine Name (ADR 0006), and every other Engine under the same data
/// folder: it lists them, brings an app to the front, and stops one.
/// </summary>
public sealed partial class AppInstances
{
    /// <summary>Runs another app executable than this process's own, as the tests do.</summary>
    public const string ExecutableVariable = "SKUA_APP_EXECUTABLE";

    /// <summary>The signal that makes the app show its main window, as clicking its Dock icon does.</summary>
    public const int ShowSignal = 30; // SIGUSR1 on macOS

    private const int Sigterm = 15;

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    public AppInstances(string skuaDir)
    {
        SkuaDir = skuaDir;
    }

    public string SkuaDir { get; }

    /// <summary>The app executable this Manager launches: <see cref="ExecutableVariable"/>, else its own.</summary>
    public static string Executable =>
        Environment.GetEnvironmentVariable(ExecutableVariable) is { Length: > 0 } path ? path : Environment.ProcessPath!;

    public EngineEndpoint Endpoint(string name) => EngineEndpoint.Resolve(name, SkuaDir);

    /// <summary>The Engines that answer on a socket in the data folder, by name.</summary>
    public async Task<IReadOnlyList<RunningEngine>> ListAsync(CancellationToken cancellationToken)
    {
        string engines = Path.Combine(SkuaDir, "engines");
        IEnumerable<string> names = Directory.Exists(engines)
            ? Directory.EnumerateFiles(engines, "*.sock").Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(EngineName.IsValid)
            : [];
        RunningEngine?[] found = await Task.WhenAll(names.Order(StringComparer.Ordinal).Select(name => DescribeAsync(Endpoint(name), cancellationToken)));
        return [.. found.OfType<RunningEngine>()];
    }

    /// <summary>The Engine serving <paramref name="endpoint"/>, or null when none answers.</summary>
    public static async Task<RunningEngine?> DescribeAsync(EngineEndpoint endpoint, CancellationToken cancellationToken)
    {
        using EngineConnection? connection = await EngineClient.TryConnectAsync(endpoint, cancellationToken);
        if (connection is null)
            return null;
        StatusDto? status = null;
        if (connection.IsCompatible)
        {
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(StatusTimeout);
                status = await connection.StatusAsync(timeout.Token);
            }
            catch (Exception e) when (e is ControlException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }
        return new RunningEngine(endpoint, connection.Hello, status);
    }

    /// <summary>
    /// Starts an app for <paramref name="arguments"/> in the background. It hosts its own Engine under this data folder, so its socket is its
    /// name's, and it never holds this process's stdio.
    /// </summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.EngineUnavailable"/> when the app couldn't be started.</exception>
    public Process Launch(AppArguments arguments)
    {
        string executable = Executable;
        // As for skua-engine: the app's runtime keeps its own copies of fds 0-2, so it gets /dev/null rather than this process's.
        ProcessStartInfo startInfo = new("/bin/sh") { UseShellExecute = false };
        foreach (string argument in (string[])["-c", "exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1", executable, .. arguments.ToArgs()])
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment[EngineEndpoint.SkuaDirVariable] = SkuaDir;
        // A socket override names one socket, which each launched app would try to bind.
        startInfo.Environment.Remove(EngineEndpoint.SocketVariable);
        try
        {
            return Process.Start(startInfo) ?? throw new ControlException(ErrorCode.EngineUnavailable, $"Couldn't start '{executable}'.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ControlException(ErrorCode.EngineUnavailable, $"Couldn't start '{executable}': {e.Message}");
        }
    }

    /// <summary>Shows the app's main window and brings the app to the front; false when it has gone.</summary>
    public static bool Show(RunningEngine app)
    {
        if (!app.IsApp || !Signal(app.Hello.Pid, ShowSignal))
            return false;
        // macOS lets the active app, this Manager, hand activation to another.
        MacApps.Activate(app.Hello.Pid);
        return true;
    }

    /// <summary>
    /// Stops the Engine and waits until it has let go of its name: an app quits as on SIGTERM, without asking; a headless Engine stops as with
    /// <c>skua engine stop</c>. Either way its game logs out and any Script stops.
    /// </summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.Timeout"/> when it doesn't stop in time.</exception>
    public async Task StopAsync(RunningEngine engine, CancellationToken cancellationToken)
    {
        if (!engine.IsApp)
        {
            await EngineClient.StopAsync(engine.Endpoint, StopTimeout, cancellationToken);
            return;
        }
        // The pid is checked afresh, so a pid reused since the listing never gets the signal.
        if (await DescribeAsync(engine.Endpoint, cancellationToken) is not { IsApp: true } current || current.Hello.Pid != engine.Hello.Pid)
            return;
        Signal(engine.Hello.Pid, Sigterm);
        await EngineClient.WaitUntilStoppedAsync(engine.Endpoint, StopTimeout, cancellationToken);
    }

    /// <summary>Sends <paramref name="signal"/> to <paramref name="pid"/>; false when there is no such process.</summary>
    public static bool Signal(int pid, int signal) => kill(pid, signal) == 0;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
