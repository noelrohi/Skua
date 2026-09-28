using System.Diagnostics;
using System.Net.Sockets;
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

    /// <summary>Receives a one-line notice when connecting replaces a stale Engine, or keeps one.</summary>
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
    /// Connects to the Engine, auto-starting it if needed, and checks its protocol version; with <see cref="EngineClientOptions.ReplaceStale"/>,
    /// it first replaces an idle Engine from another build.
    /// </summary>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.EngineUnavailable"/> or <see cref="ErrorCode.ProtocolMismatch"/>; <see cref="ErrorCode.ScriptRunning"/> or
    /// <see cref="ErrorCode.Busy"/> for an Engine on another protocol version that is too busy to replace; <see cref="ErrorCode.Timeout"/>
    /// when the Engine being replaced doesn't stop in time.
    /// </exception>
    public static async Task<EngineConnection> ConnectAsync(EngineClientOptions options, CancellationToken cancellationToken = default)
    {
        EngineConnection connection = await ConnectOrStartAsync(options, cancellationToken);
        bool replaced;
        try
        {
            replaced = options.ReplaceStale && await ReplaceIfStaleAsync(options, connection, cancellationToken);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        if (replaced)
        {
            connection.Dispose();
            connection = await ConnectOrStartAsync(options, cancellationToken);
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
    /// Stops the Engine behind <paramref name="connection"/> when it is from another build and idle, and returns whether it did. A busy one
    /// is kept, with a notice, when it speaks this protocol version, and fails the connect when it doesn't. The Mac App's is never stopped.
    /// </summary>
    private static async Task<bool> ReplaceIfStaleAsync(EngineClientOptions options, EngineConnection connection, CancellationToken cancellationToken)
    {
        HelloResult engine = connection.Hello;
        if (engine.Protocol == ControlProtocol.Version && engine.Build == options.Build)
            return false;

        string staleEngine = $"Engine '{engine.EngineName}' from another build ({engine.Build}, protocol {engine.Protocol})";
        if (engine.Host == EngineHost.App)
        {
            // It would refuse anyway; EnsureCompatible refuses an incompatible one, saying to quit the app.
            if (connection.IsCompatible)
                options.Notice?.Invoke($"{staleEngine} wasn't replaced: the Skua app hosts it; quit the app to replace it.");
            return false;
        }

        try
        {
            if (!await connection.ShutdownIfIdleAsync(cancellationToken))
            {
                // EnsureCompatible refuses an incompatible one, with a stop hint.
                if (connection.IsCompatible)
                    options.Notice?.Invoke($"{staleEngine} wasn't replaced: it is too old to replace safely; run 'skua engine stop' to replace it.");
                return false;
            }
        }
        catch (ControlException e) when (e.Code is ErrorCode.ScriptRunning or ErrorCode.Busy)
        {
            string kept = $"{staleEngine} wasn't replaced, because {(e.Code == ErrorCode.ScriptRunning ? "a Script is running in it" : "it is busy with another command")}";
            if (!connection.IsCompatible)
                throw new ControlException(e.Code,
                    $"{kept}, and this skua speaks protocol {ControlProtocol.Version}. Wait for it to finish, or run 'skua engine stop', which stops its Script too; then try again.",
                    e);
            options.Notice?.Invoke($"{kept}; a later skua command replaces it once it's idle.");
            return false;
        }
        catch (ControlException e) when (e.Code == ErrorCode.EngineUnavailable)
        {
            // The Engine may close the connection before its reply arrives.
        }

        await WaitUntilStoppedAsync(options.Endpoint, ReplaceTimeout, cancellationToken);
        options.Notice?.Invoke($"Replaced {staleEngine} with build {options.Build}, protocol {ControlProtocol.Version}.");
        return true;
    }

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

    /// <summary>Connects to the Engine, auto-starting it if needed, without checking its protocol version.</summary>
    public static async Task<EngineConnection> ConnectOrStartAsync(EngineClientOptions options, CancellationToken cancellationToken = default)
    {
        EngineEndpoint endpoint = options.Endpoint;
        if (await TryConnectAsync(endpoint, cancellationToken) is { } running)
            return running;

        // The new Engine removes the stale socket itself, once it holds the lock. If another client wins the race to start one, it exits at once.
        using Process? started = EngineLock.IsHeld(endpoint.LockPath) ? null : Start(options);
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
