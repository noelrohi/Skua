using System.Diagnostics;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>
/// A throwaway Skua data folder with its own Engine endpoint. Disposing it stops any Engine left running and deletes the folder.
/// </summary>
/// <remarks>The folder lives under /tmp, because $TMPDIR on macOS is too long for a socket path.</remarks>
public sealed class EngineSandbox : IAsyncDisposable
{
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    private readonly List<Process> _processes = [];

    public EngineSandbox()
    {
        SkuaDir = Directory.CreateDirectory(Path.Combine("/tmp", "skua-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        Endpoint = EngineEndpoint.Resolve(EngineName.Default, SkuaDir);
    }

    public string SkuaDir { get; }

    public EngineEndpoint Endpoint { get; }

    public static string BinDir => AppContext.BaseDirectory;

    public static string EngineExecutable => Path.Combine(BinDir, "skua-engine");

    public static string CliExecutable => Path.Combine(BinDir, "skua");

    public static string FakeGameHostExecutable => Path.Combine(BinDir, "fake-gamehost");

    public EngineClientOptions ClientOptions => new() { Endpoint = Endpoint, EngineExecutable = EngineExecutable };

    public Task<EngineConnection> ConnectAsync() => EngineClient.ConnectAsync(ClientOptions, TestContext.Current.CancellationToken);

    /// <summary>Starts skua-engine in the foreground with extra environment, as a developer would, and connects to it.</summary>
    public async Task<(Process Engine, EngineConnection Connection)> StartEngineAsync(IDictionary<string, string>? environment = null)
    {
        Process engine = StartEngineProcess(environment);
        Stopwatch waited = Stopwatch.StartNew();
        while (waited.Elapsed < StopTimeout)
        {
            if (await EngineClient.TryConnectAsync(Endpoint, TestContext.Current.CancellationToken) is { } connection)
                return (engine, connection);
            if (engine.HasExited)
                throw new InvalidOperationException($"skua-engine exited with {engine.ExitCode}: {await engine.StandardError.ReadToEndAsync()}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("skua-engine didn't answer.");
    }

    public Process StartEngineProcess(IDictionary<string, string>? environment = null)
    {
        ProcessStartInfo startInfo = new(EngineExecutable) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach ((string key, string value) in Endpoint.EngineEnvironment())
            startInfo.Environment[key] = value;
        foreach ((string key, string value) in environment ?? new Dictionary<string, string>())
            startInfo.Environment[key] = value;
        Process process = Process.Start(startInfo)!;
        _processes.Add(process);
        return process;
    }

    /// <summary>Runs <c>skua</c> to completion with this sandbox's data folder in its environment.</summary>
    public Task<ProcessResult> RunCliAsync(params string[] arguments) => RunCliAsync(new Dictionary<string, string>(), arguments);

    public Task<ProcessResult> RunCliAsync(IDictionary<string, string> environment, params string[] arguments) =>
        RunCliWithInputAsync(environment, null, arguments);

    /// <summary>Runs <c>skua</c> to completion with <paramref name="input"/> as its standard input, which then closes.</summary>
    public async Task<ProcessResult> RunCliWithInputAsync(IDictionary<string, string> environment, string? input, params string[] arguments)
    {
        using Process process = LaunchCli(environment, arguments, closeInput: input is null);
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(StopTimeout * 2);
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>Starts <c>skua</c> with this sandbox's data folder, for a command that keeps running; disposing the sandbox kills it.</summary>
    public Process StartCli(params string[] arguments) => StartCli(new Dictionary<string, string>(), arguments);

    public Process StartCli(IDictionary<string, string> environment, params string[] arguments)
    {
        Process process = LaunchCli(environment, arguments);
        _processes.Add(process);
        return process;
    }

    /// <summary>
    /// Starts <c>skua</c> on a pseudo-terminal, through macOS's <c>script</c>, as a developer runs it in a terminal: what is written to its
    /// standard input is typed, and its output has the terminal's CRLF line ends and echo. Disposing the sandbox kills it.
    /// </summary>
    public Process StartCliInTerminal(IDictionary<string, string> environment, params string[] arguments)
    {
        Process process = LaunchCli(environment, ["-q", "/dev/null", CliExecutable, .. arguments], terminal: true);
        _processes.Add(process);
        return process;
    }

    private Process LaunchCli(IDictionary<string, string> environment, string[] arguments, bool terminal = false, bool closeInput = true)
    {
        // In the data folder, so files the CLI writes by default stay in the sandbox.
        ProcessStartInfo startInfo = new(terminal ? "/usr/bin/script" : CliExecutable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            WorkingDirectory = SkuaDir,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment[EngineEndpoint.SkuaDirVariable] = SkuaDir;
        startInfo.Environment[EngineClientOptions.EngineExecutableVariable] = EngineExecutable;
        startInfo.Environment.Remove(EngineEndpoint.SocketVariable);
        foreach ((string key, string value) in environment)
            startInfo.Environment[key] = value;

        Process process = Process.Start(startInfo)!;
        if (!terminal && closeInput)
            process.StandardInput.Close();
        return process;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            try
            {
                await EngineClient.StopAsync(Endpoint, StopTimeout);
            }
            catch (ControlException)
            {
            }

            foreach (Process process in _processes)
            {
                // The whole tree, so a CLI under script(1) goes too; and waited for, so nothing writes to the folder once it is deleted.
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                process.WaitForExit(StopTimeout);
                process.Dispose();
            }
        }
        finally
        {
            DeleteFolder(SkuaDir);
        }
    }

    /// <summary>Deletes a sandbox's folder, retrying for a while when a process that is ending still writes to it.</summary>
    /// <exception cref="IOException">It still couldn't be deleted.</exception>
    public static void DeleteFolder(string dir)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(100);
            }
        }
    }
}

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
