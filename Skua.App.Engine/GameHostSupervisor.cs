using Skua.Control;
using Skua.MacOS.GameHost;

namespace Skua.App.Engine;

/// <summary>
/// Runs the Game Host named by <c>SKUA_GAMEHOST</c>, if any, with the SWF named by <c>SKUA_SWF</c> (default: <c>skua.swf</c> next to the Engine).
/// </summary>
internal sealed class GameHostSupervisor : IDisposable
{
    public const string GameHostVariable = "SKUA_GAMEHOST";
    public const string SwfVariable = "SKUA_SWF";

    private readonly GameHostProcess? _process;

    private GameHostSupervisor(GameHostProcess? process)
    {
        _process = process;
    }

    /// <exception cref="FileNotFoundException">The configured Game Host doesn't exist.</exception>
    public static GameHostSupervisor FromEnvironment()
    {
        if (Environment.GetEnvironmentVariable(GameHostVariable) is not { Length: > 0 } executable)
            return new GameHostSupervisor(null);

        string swf = Environment.GetEnvironmentVariable(SwfVariable) is { Length: > 0 } path
            ? path
            : Path.Combine(AppContext.BaseDirectory, "skua.swf");

        GameHostProcess process = new(Path.GetFullPath(executable), [swf]);
        process.LogLine += line => EngineLog.Write($"[gamehost] {line}");
        process.FrameReceived += frame =>
        {
            if (frame.Type == 'L')
                EngineLog.Write($"[gamehost] {System.Text.Encoding.UTF8.GetString(frame.Payload)}");
        };
        process.Exited += code => EngineLog.Write($"Game Host exited with code {code}.");
        process.Start();
        EngineLog.Write($"Game Host started (pid {process.Pid}): {executable} {swf}");
        return new GameHostSupervisor(process);
    }

    public GameStatusDto Status()
    {
        bool up = _process?.IsRunning ?? false;
        return new GameStatusDto(up, up ? null : GameState.NotStarted, null);
    }

    public void Stop() => _process?.Dispose();

    public void Dispose() => Stop();
}
