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
/// When the socket doesn't answer, the client starts an Engine. If the lock was free, the socket was stale: the new Engine removes it and serves.
/// If the lock was held, an Engine is starting: the new one exits at once, and the client waits for the other and reports it as starting or hung if it never answers.
/// Starting an Engine never logs in.
/// </remarks>
public static class EngineClient
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Connects to the Engine, auto-starting it if needed, and checks its protocol version.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.EngineUnavailable"/> or <see cref="ErrorCode.ProtocolMismatch"/>.</exception>
    public static async Task<EngineConnection> ConnectAsync(EngineClientOptions options, CancellationToken cancellationToken = default)
    {
        EngineConnection connection = await ConnectOrStartAsync(options, cancellationToken);
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

    /// <summary>Connects to a running Engine of any protocol version, or returns null when none answers. Never starts one.</summary>
    public static Task<EngineConnection?> TryConnectAsync(EngineEndpoint endpoint, CancellationToken cancellationToken = default) =>
        TryOpenAsync(endpoint, cancellationToken);

    /// <summary>
    /// Asks the Engine of any protocol version to shut down, and waits until it has released its lock.
    /// Returns false when no Engine was running.
    /// </summary>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.EngineUnavailable"/> when the Engine is starting or hung; <see cref="ErrorCode.Timeout"/> when it doesn't stop in time.
    /// </exception>
    public static async Task<bool> StopAsync(EngineEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using (EngineConnection? connection = await TryOpenAsync(endpoint, cancellationToken))
        {
            if (connection is null)
            {
                if (EngineLock.IsHeld(endpoint.LockPath))
                    throw new ControlException(ErrorCode.EngineUnavailable,
                        $"Engine '{endpoint.Name}' is starting or hung: it holds {endpoint.LockPath} but {endpoint.SocketPath} doesn't answer. See {endpoint.LogPath}.");
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

        Stopwatch waited = Stopwatch.StartNew();
        while (EngineLock.IsHeld(endpoint.LockPath))
        {
            if (waited.Elapsed > timeout)
                throw new ControlException(ErrorCode.Timeout, $"Engine '{endpoint.Name}' didn't stop within {timeout.TotalSeconds:0} s.");
            await Task.Delay(PollInterval, cancellationToken);
        }
        return true;
    }

    /// <summary>Connects to the Engine, auto-starting it if needed, without checking its protocol version.</summary>
    public static async Task<EngineConnection> ConnectOrStartAsync(EngineClientOptions options, CancellationToken cancellationToken = default)
    {
        EngineEndpoint endpoint = options.Endpoint;
        if (await TryOpenAsync(endpoint, cancellationToken) is { } running)
            return running;

        // The Engine itself takes the lock and removes a stale socket; it exits at once when another Engine holds the lock.
        using Process started = Start(options);
        Stopwatch waited = Stopwatch.StartNew();
        while (waited.Elapsed < options.StartTimeout)
        {
            await Task.Delay(PollInterval, cancellationToken);
            if (await TryOpenAsync(endpoint, cancellationToken) is { } connection)
                return connection;

            if (started.HasExited && started.ExitCode != EngineExitCodes.AlreadyRunning)
                throw new ControlException(ErrorCode.EngineUnavailable,
                    $"The Engine exited during start with code {started.ExitCode}; see {endpoint.LogPath}.");
        }

        throw new ControlException(ErrorCode.EngineUnavailable,
            $"Engine '{endpoint.Name}' is starting or hung: it holds {endpoint.LockPath} but {endpoint.SocketPath} doesn't answer. See {endpoint.LogPath}.");
    }

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

    private static async Task<EngineConnection?> TryOpenAsync(EngineEndpoint endpoint, CancellationToken cancellationToken)
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
