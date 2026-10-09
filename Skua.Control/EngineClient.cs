using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using StreamJsonRpc;

namespace Skua.Control;

public sealed record EngineClientOptions
{
    /// <summary>Overrides the <c>skua-engine</c> executable that auto-start launches.</summary>
    public const string EngineExecutableVariable = "SKUA_ENGINE";

    public required EngineEndpoint Endpoint { get; init; }

    /// <summary>The <c>skua-engine</c> to launch when no Engine is running.</summary>
    public string EngineExecutable { get; init; } = DefaultEngineExecutable();

    /// <summary>How long to wait for a starting Engine to answer.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The build this client expects; an Engine from any other build is stale.</summary>
    public string Build { get; init; } = ControlProtocol.Build;

    /// <summary>
    /// Whether connecting replaces a stale Engine, one from another build or protocol version: it is stopped and a new one started when idle,
    /// and never while a Script runs. Only short-lived clients set it; a long-lived one, like an MCP server from an older install, would
    /// replace the newer Engine.
    /// </summary>
    public bool ReplaceStale { get; init; }

    /// <summary>
    /// Whether a stale Engine that speaks this protocol version is kept rather than replaced, for a command that only reads: it can answer, and
    /// replacing it would start an Engine at the login screen that nobody asked for. A command that drives the game replaces it.
    /// </summary>
    public bool KeepCompatibleStale { get; init; }

    /// <summary>
    /// Whether connecting starts the Engine when none runs: true by default. Without it, connecting fails with
    /// <see cref="ErrorCode.EngineUnavailable"/> instead, though it still waits for an Engine that is starting, and still starts the replacement
    /// of a stale Engine it stopped.
    /// </summary>
    public bool AutoStart { get; init; } = true;

    /// <summary>
    /// Receives a one-line notice when connecting replaces a stale Engine, or keeps one; a kept one is noticed once per Engine and build, since
    /// <c>status</c> and <c>engine list</c> keep showing it.
    /// </summary>
    public Action<string>? Notice { get; init; }

    /// <summary><c>skua-engine</c> next to this program, unless <c>SKUA_ENGINE</c> overrides it.</summary>
    public static string DefaultEngineExecutable() =>
        Environment.GetEnvironmentVariable(EngineExecutableVariable) is { Length: > 0 } path
            ? Path.GetFullPath(path)
            : Path.Combine(AppContext.BaseDirectory, "skua-engine");
}

/// <summary>
/// Connects to an Engine, starting one when none is running.
/// </summary>
/// <remarks>
/// When the socket doesn't answer and the lock is free, the socket is stale: the client starts an Engine, which removes it and serves.
/// When the lock is held, an Engine is starting: the client starts none, waits for it, and reports it as starting or hung if it never answers.
/// Starting an Engine never logs in.
/// </remarks>
public static class EngineClient
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Long enough for an Engine being replaced to close its Game Host.</summary>
    private static readonly TimeSpan ReplaceTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Connects to the Engine, auto-starting it if needed unless <see cref="EngineClientOptions.AutoStart"/> is off, and checks its protocol
    /// version; with <see cref="EngineClientOptions.ReplaceStale"/>,
    /// it first replaces an idle Engine from another build with one of the same Engine Name.
    /// </summary>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.EngineUnavailable"/> or <see cref="ErrorCode.ProtocolMismatch"/>; <see cref="ErrorCode.ScriptRunning"/> or
    /// <see cref="ErrorCode.Busy"/> for an Engine on another protocol version that is too busy to replace; <see cref="ErrorCode.Timeout"/>
    /// when the Engine being replaced doesn't stop in time.
    /// </exception>
    public static async Task<EngineConnection> ConnectAsync(EngineClientOptions options, CancellationToken cancellationToken = default)
    {
        EngineConnection connection = await ConnectOrStartAsync(options, cancellationToken);
        EngineEndpoint? replaced;
        try
        {
            replaced = options.ReplaceStale ? await ReplaceIfStaleAsync(options, connection, cancellationToken) : null;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        if (replaced is not null)
        {
            connection.Dispose();
            // A replacement isn't a new Engine, so it starts even with AutoStart off.
            connection = await ConnectOrStartAsync(options with { Endpoint = replaced, AutoStart = true }, cancellationToken);
        }

        try
        {
            connection.EnsureCompatible();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        return connection;
    }

    /// <summary>
    /// Asks the Engine of any protocol version to shut down, and waits until it has released its lock.
    /// Returns false when no Engine was running.
    /// </summary>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.EngineUnavailable"/> when the Engine is starting or hung; <see cref="ErrorCode.Timeout"/> when it doesn't stop in time;
    /// <see cref="ErrorCode.EngineOwnedByApp"/> when the Mac App hosts it, which keeps running.
    /// </exception>
    public static async Task<bool> StopAsync(EngineEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using (EngineConnection? connection = await TryConnectAsync(endpoint, cancellationToken))
        {
            if (connection is null)
            {
                if (EngineLock.IsHeld(endpoint.LockPath))
                    throw StartingOrHung(endpoint);
                return false;
            }

            endpoint = StartedAs(endpoint, connection.Hello);
            try
            {
                await connection.ShutdownAsync(cancellationToken);
            }
            catch (ControlException e) when (e.Code == ErrorCode.EngineUnavailable)
            {
                // The Engine may close the connection before its reply arrives.
            }
        }

        await WaitUntilStoppedAsync(endpoint, timeout, cancellationToken);
        return true;
    }

    /// <summary>
    /// Stops the Engine behind <paramref name="connection"/> when it is from another build and idle, and returns the endpoint to start its
    /// replacement at: the same socket, under the Engine Name it gave, or the endpoint's when it gave none valid. Returns null when it kept it.
    /// A busy one is kept, with a notice, when it speaks this protocol version, and fails the connect when it doesn't. The Mac App's is never
    /// stopped.
    /// </summary>
    private static async Task<EngineEndpoint?> ReplaceIfStaleAsync(
        EngineClientOptions options, EngineConnection connection, CancellationToken cancellationToken)
    {
        HelloResult engine = connection.Hello;
        if (engine.Protocol == ControlProtocol.Version && engine.Build == options.Build)
            return null;

        string staleEngine = $"Engine '{engine.EngineName}' from another build ({engine.Build}, protocol {engine.Protocol})";
        if (engine.Host == EngineHost.App)
        {
            // It would refuse anyway; EnsureCompatible refuses an incompatible one, saying to quit the app.
            if (connection.IsCompatible)
                NoticeKept(options, engine, $"{staleEngine} wasn't replaced: the Skua app hosts it; quit the app to replace it.");
            return null;
        }

        if (options.KeepCompatibleStale && connection.IsCompatible)
        {
            NoticeKept(options, engine, $"{staleEngine} wasn't replaced, since this command only reads; a skua command that drives the game replaces it once it's idle.");
            return null;
        }

        ScriptRunResultDto? lastRun = connection.IsCompatible ? await LastRunAsync(connection, cancellationToken) : null;
        try
        {
            if (!await connection.ShutdownIfIdleAsync(cancellationToken))
            {
                // EnsureCompatible refuses an incompatible one, with a stop hint.
                if (connection.IsCompatible)
                    NoticeKept(options, engine, $"{staleEngine} wasn't replaced: it is too old to replace safely; run 'skua engine stop' to replace it.");
                return null;
            }
        }
        catch (ControlException e) when (e.Code is ErrorCode.ScriptRunning or ErrorCode.Busy)
        {
            string kept = $"{staleEngine} wasn't replaced, because {(e.Code == ErrorCode.ScriptRunning ? "a Script is running in it" : "it is busy with another command")}";
            if (!connection.IsCompatible)
                throw new ControlException(e.Code,
                    $"{kept}, and this skua speaks protocol {ControlProtocol.Version}. Wait for it to finish, or run 'skua engine stop', which stops its Script too; then try again.",
                    e);
            NoticeKept(options, engine, $"{kept}; a later skua command replaces it once it's idle.");
            return null;
        }
        catch (ControlException e) when (e.Code == ErrorCode.EngineUnavailable)
        {
            // The Engine may close the connection before its reply arrives.
        }

        EngineEndpoint endpoint = StartedAs(options.Endpoint, engine);
        await WaitUntilStoppedAsync(endpoint, ReplaceTimeout, cancellationToken);
        if (lastRun is not null)
            HandOver(endpoint, lastRun);
        options.Notice?.Invoke($"Replaced {staleEngine} with build {options.Build}, protocol {ControlProtocol.Version}.");
        return endpoint;
    }

    /// <summary>The idle Engine's last run, which its replacement takes over; null when it has none or didn't say.</summary>
    private static async Task<ScriptRunResultDto?> LastRunAsync(EngineConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return (await connection.ScriptStatusAsync(cancellationToken)).LastRun;
        }
        // Losing the last run is no reason to keep the stale Engine.
        catch (Exception e) when (e is ControlException or RemoteInvocationException)
        {
            return null;
        }
    }

    /// <summary>Leaves the replaced Engine's last run for its replacement; without it, a caller polling for the run's outcome would lose it.</summary>
    private static void HandOver(EngineEndpoint endpoint, ScriptRunResultDto lastRun)
    {
        try
        {
            File.WriteAllText(endpoint.LastRunPath, JsonSerializer.Serialize(lastRun, ControlJson.Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Takes the last run a client left at <paramref name="endpoint"/> when it replaced the Engine from another build there, and removes it; null
    /// when there is none or it can't be read.
    /// </summary>
    public static ScriptRunResultDto? TakeOverLastRun(EngineEndpoint endpoint)
    {
        try
        {
            string json = File.ReadAllText(endpoint.LastRunPath);
            File.Delete(endpoint.LastRunPath);
            return JsonSerializer.Deserialize<ScriptRunResultDto>(json, ControlJson.Options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Says that a stale Engine was kept, unless a client of this build already said so about that Engine (by its pid): every command against
    /// an Engine whose Script runs for hours would repeat it. Remembered in the data folder, so it holds across commands.
    /// </summary>
    private static void NoticeKept(EngineClientOptions options, HelloResult engine, string notice)
    {
        if (options.Notice is null)
            return;
        string path = StartedAs(options.Endpoint, engine).KeptNoticePath;
        string kept = $"{engine.Pid} {engine.Build} {options.Build}";
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() == kept)
                return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        options.Notice(notice);
        try
        {
            File.WriteAllText(path, kept + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The endpoint of the Engine that answered at <paramref name="endpoint"/>'s socket, under the Engine Name it gave: it holds that name's
    /// lock, which may not be the one this client resolved.
    /// </summary>
    private static EngineEndpoint StartedAs(EngineEndpoint endpoint, HelloResult hello) =>
        EngineName.IsValid(hello.EngineName) ? endpoint.WithName(hello.EngineName) : endpoint;

    /// <summary>Waits until the Engine has released its lock, e.g. after a <c>shutdown</c> request.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.Timeout"/> when it doesn't stop in time.</exception>
    public static async Task WaitUntilStoppedAsync(EngineEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (EngineLock.IsHeld(endpoint.LockPath))
        {
            if (waited.Elapsed > timeout)
                throw new ControlException(ErrorCode.Timeout, $"Engine '{endpoint.Name}' didn't stop within {timeout.TotalSeconds:0} s.");
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Connects to the Engine, auto-starting it if needed unless <see cref="EngineClientOptions.AutoStart"/> is off, without checking its protocol
    /// version.
    /// </summary>
    public static async Task<EngineConnection> ConnectOrStartAsync(EngineClientOptions options, CancellationToken cancellationToken = default)
    {
        EngineEndpoint endpoint = options.Endpoint;
        if (await TryConnectAsync(endpoint, cancellationToken) is { } running)
            return running;

        // The new Engine removes the stale socket itself, once it holds the lock. If another client wins the race to start one, it exits at once.
        bool starting = EngineLock.IsHeld(endpoint.LockPath);
        if (!starting && !options.AutoStart)
            throw new ControlException(ErrorCode.EngineUnavailable, $"Engine '{endpoint.Name}' isn't running.");
        using Process? started = starting ? null : Start(options);
        Stopwatch waited = Stopwatch.StartNew();
        while (waited.Elapsed < options.StartTimeout)
        {
            await Task.Delay(PollInterval, cancellationToken);
            if (await TryConnectAsync(endpoint, cancellationToken) is { } connection)
                return connection;

            if (started is { HasExited: true } && started.ExitCode != EngineExitCodes.AlreadyRunning)
                throw new ControlException(ErrorCode.EngineUnavailable,
                    $"The Engine exited during start with code {started.ExitCode}; see {endpoint.LogPath}.");
        }

        throw StartingOrHung(endpoint);
    }

    private static ControlException StartingOrHung(EngineEndpoint endpoint) => new(ErrorCode.EngineUnavailable,
        $"Engine '{endpoint.Name}' is starting or hung: it holds {endpoint.LockPath} but {endpoint.SocketPath} doesn't answer. See {endpoint.LogPath}.");

    private static Process Start(EngineClientOptions options)
    {
        if (!File.Exists(options.EngineExecutable))
            throw new ControlException(ErrorCode.EngineUnavailable,
                $"No Engine is running and '{options.EngineExecutable}' doesn't exist; set {EngineClientOptions.EngineExecutableVariable} to the skua-engine executable.");

        // The Engine must never hold its starter's stdio: the .NET runtime keeps its own copies of fds 0-2 from the moment it starts,
        // so a `skua status | cat` or an MCP client would wait forever for EOF. The shell points them at /dev/null before the Engine starts.
        ProcessStartInfo startInfo = new("/bin/sh")
        {
            UseShellExecute = false,
            ArgumentList = { "-c", "exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1", options.EngineExecutable, "--name", options.Endpoint.Name, "--detach" },
        };
        foreach ((string key, string value) in options.Endpoint.EngineEnvironment())
            startInfo.Environment[key] = value;

        return Process.Start(startInfo)
            ?? throw new ControlException(ErrorCode.EngineUnavailable, $"Couldn't start '{options.EngineExecutable}'.");
    }

    /// <summary>Connects to a running Engine of any protocol version, or returns null when none answers. Never starts one.</summary>
    public static async Task<EngineConnection?> TryConnectAsync(EngineEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.SocketPath), cancellationToken);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }

        JsonRpc rpc = ControlJson.CreateRpc(socket);
        IEngineRpc proxy = rpc.Attach<IEngineRpc>();
        rpc.StartListening();
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HelloTimeout);
            HelloResult hello = await proxy.HelloAsync(ControlProtocol.Version, timeout.Token);
            return new EngineConnection(rpc, proxy, hello);
        }
        catch (Exception e) when (e is ConnectionLostException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            rpc.Dispose();
            return null;
        }
    }
}
