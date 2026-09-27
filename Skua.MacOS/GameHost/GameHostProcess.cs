using System.Diagnostics;

namespace Skua.MacOS.GameHost;

/// <summary>
/// The Game Host child process and its Bridge pipes. The Game Host exits on its own when its stdin closes, so it never outlives the Engine.
/// </summary>
/// <remarks>Subscribe to the events before calling <see cref="Start"/>, so no frame or exit is missed.</remarks>
public sealed class GameHostProcess : IDisposable
{
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(1);

    private readonly Process _process;
    private Stream? _stdin;
    private bool _disposed;

    public GameHostProcess(string executable, IEnumerable<string> arguments)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += (_, _) => Exited?.Invoke(_process.ExitCode);
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                LogLine?.Invoke(e.Data);
        };
    }

    public int Pid => _process.Id;

    public bool IsRunning => _stdin is not null && !_process.HasExited;

    /// <summary>Raised on the reader thread for every frame the Game Host sends, in order.</summary>
    public event Action<BridgeFrame>? FrameReceived;

    /// <summary>Raised for every line the Game Host writes to stderr, and when the Bridge stops.</summary>
    public event Action<string>? LogLine;

    /// <summary>Raised once when the Game Host process ends, with its exit code.</summary>
    public event Action<int>? Exited;

    /// <exception cref="FileNotFoundException">The executable doesn't exist.</exception>
    public void Start()
    {
        string executable = _process.StartInfo.FileName;
        if (!File.Exists(executable))
            throw new FileNotFoundException($"The Game Host '{executable}' doesn't exist.", executable);

        _process.Start();
        _stdin = _process.StandardInput.BaseStream;
        _process.BeginErrorReadLine();
        new Thread(ReadLoop) { IsBackground = true, Name = "Game Host reader" }.Start();
    }

    /// <summary>Closes the Game Host's stdin so it exits, and kills it if it doesn't within a second.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_stdin is not null)
        {
            try
            {
                _stdin.Close();
            }
            catch (IOException)
            {
            }

            if (!_process.WaitForExit(ExitGrace))
                _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }
        _process.Dispose();
    }

    private void ReadLoop()
    {
        Stream stdout = _process.StandardOutput.BaseStream;
        try
        {
            while (BridgeFrames.ReadAsync(stdout).GetAwaiter().GetResult() is { } frame)
                FrameReceived?.Invoke(frame);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException)
        {
            LogLine?.Invoke($"Bridge read stopped: {e.Message}");
        }
    }
}
