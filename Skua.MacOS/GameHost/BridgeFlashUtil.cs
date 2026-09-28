using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Utils;
using System.Xml.Linq;

namespace Skua.MacOS.GameHost;

/// <summary>
/// The macOS <see cref="IFlashUtil"/>: the Game Client runs in the Game Host child process, and calls cross the Bridge as the same
/// invoke XML the Windows ActiveX control carries.
/// </summary>
/// <remarks>The Game Host's events outlive a restart by <see cref="InitializeFlash"/>, so subscribe to them once.</remarks>
public sealed class BridgeFlashUtil : IFlashUtil
{
    private readonly IMessenger _messenger;
    private readonly Lazy<IScriptManager> _lazyManager;
    private readonly GameHostLaunch _launch;
    private GameHostProcess? _gameHost;
    private volatile FrameBuffer? _frameBuffer;
    private volatile bool _live;

    public BridgeFlashUtil(IMessenger messenger, Lazy<IScriptManager> manager, GameHostLaunch launch)
    {
        _messenger = messenger;
        _lazyManager = manager;
        _launch = launch;
    }

    public event FlashCallHandler? FlashCall;

    /// <summary>Raised when a Game Host process has started, with its pid, before any of its other events.</summary>
    public event Action<int>? GameHostStarted;

    /// <summary>Raised when the Game Host process ends, with its exit code.</summary>
    public event Action<int>? GameHostExited;

    /// <summary>Raised for each Ruffle/wgpu log line and stderr line of the Game Host.</summary>
    public event Action<string>? GameHostLog;

    /// <summary>Raised for each Flash log line: AS3 <c>trace()</c>, warnings and uncaught AS3 errors.</summary>
    public event Action<string>? FlashLog;

    /// <summary>Raised when the Game Host sends a corrupt frame, with the reason; the Bridge reads nothing after it.</summary>
    public event Action<string>? BridgeFailed;

    public bool IsGameHostRunning => _gameHost?.IsRunning ?? false;

    /// <summary>The names the Game Client has registered for the Engine to call.</summary>
    public IReadOnlyList<string> Callbacks => _gameHost?.Callbacks ?? [];

    /// <summary>
    /// The current Game Host's Frame Buffer, when <see cref="GameHostLaunch.WantsFrameBuffer"/> asks for one; a restart replaces it, so read it
    /// afresh for each frame.
    /// </summary>
    public FrameBuffer? FrameBuffer => _frameBuffer;

    /// <summary>Whether the Game View is live, as last set with <see cref="SetLive"/>.</summary>
    public bool IsLive => _live;

    /// <summary>
    /// Starts the Game Host with the Game Client, closing any earlier one first. With a Frame Buffer, it creates a new one, and unlinks its
    /// name once the Game Host has answered a ping, by when the Game Host has mapped it.
    /// </summary>
    /// <exception cref="FileNotFoundException">The Game Host executable doesn't exist.</exception>
    public void InitializeFlash()
    {
        _gameHost?.Dispose();
        _frameBuffer?.Dispose();
        _frameBuffer = null;

        FrameBuffer? frameBuffer = _launch.WantsFrameBuffer
            ? FrameBuffer.Create(FrameBuffer.NewName(), GameHostLaunch.StageWidth, GameHostLaunch.StageHeight)
            : null;
        GameHostProcess gameHost = new(_launch.Executable, _launch.Arguments(frameBuffer?.Name));
        gameHost.Started += pid => GameHostStarted?.Invoke(pid);
        gameHost.Invoked += OnInvoked;
        gameHost.FlashLog += line => FlashLog?.Invoke(line);
        gameHost.LogLine += line => GameHostLog?.Invoke(line);
        gameHost.BridgeFailed += error => BridgeFailed?.Invoke(error);
        gameHost.Exited += code => GameHostExited?.Invoke(code);
        // Set before Start: the Game Client's first calls (loaded, requestLoadGame) reach handlers that call back into it before Start returns.
        _gameHost = gameHost;
        _frameBuffer = frameBuffer;
        try
        {
            gameHost.Start();
        }
        catch
        {
            _gameHost = null;
            _frameBuffer = null;
            frameBuffer?.Dispose();
            throw;
        }
        if (frameBuffer is not null)
            new Thread(() => HandOver(gameHost, frameBuffer)) { IsBackground = true, Name = "Frame Buffer hand-over" }.Start();
    }

    /// <summary>
    /// Makes the Game View live (the Game Host renders every 33 ms and writes each frame to the Frame Buffer) or not (the headless render
    /// defaults). It carries over a Game Host restart.
    /// </summary>
    public void SetLive(bool live)
    {
        _live = live;
        TrySend('W', GameInput.EncodeView(live));
    }

    /// <summary>Sends an input event to the Game Client; dropped when no Game Host runs.</summary>
    public void SendInput(GameInput input) => TrySend('U', input.Encode());

    private void TrySend(char type, byte[] payload)
    {
        try
        {
            if (_gameHost is { IsRunning: true } gameHost)
                gameHost.Send(type, payload);
        }
        catch (IOException)
        {
            // The Game Host is gone; GameHostExited tells.
        }
    }

    /// <summary>Unlinks the Frame Buffer's name once the Game Host has mapped it, then makes a restarted Game Host live if the view is.</summary>
    private void HandOver(GameHostProcess gameHost, FrameBuffer frameBuffer)
    {
        try
        {
            gameHost.Request('P', [], GameHostProcess.RequestTimeout);
            if (_live && ReferenceEquals(_gameHost, gameHost))
                gameHost.Send('W', GameInput.EncodeView(true));
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
        {
            GameHostLog?.Invoke($"The Game Host didn't answer the Frame Buffer ping: {e.Message}");
        }
        finally
        {
            frameBuffer.Unlink();
        }
    }

    public string? Call(string function, params object[] args) => Call<string>(function, args);

    public T? Call<T>(string function, params object[] args)
    {
        try
        {
            object? o = Call(function, typeof(T), args);
            return o is not null ? (T)o : (T?)DefaultProvider.GetDefault<T>(typeof(T));
        }
        catch
        {
            return (T?)DefaultProvider.GetDefault<T>(typeof(T));
        }
    }

    public object? Call(string function, Type type, params object[] args)
    {
        if (_lazyManager.Value.ShouldExit && Thread.CurrentThread.Name == "Script Thread")
            _lazyManager.Value.ScriptCts?.Token.ThrowIfCancellationRequested();
        try
        {
            GameHostProcess gameHost = _gameHost ?? throw new IOException("The Game Host hasn't started.");
            return FlashXml.ReadReturn(gameHost.Call(FlashXml.Invoke(function, args)), type);
        }
        catch (Exception e)
        {
            _messenger.Send<FlashErrorMessage>(new(e, function, args));
            return default;
        }
    }

    /// <inheritdoc cref="GameHostProcess.Screenshot"/>
    public GameHostScreenshot? Screenshot(uint maxWidth, TimeSpan timeout) =>
        (_gameHost ?? throw new IOException("The Game Host hasn't started.")).Screenshot(maxWidth, timeout);

    /// <summary>
    /// The Game Host's loop and render counters as JSON (ticks, frame rate, the largest tick gap and more); the maxima reset when read.
    /// Returns null when no Game Host runs.
    /// </summary>
    /// <exception cref="IOException">The Game Host is gone.</exception>
    /// <exception cref="TimeoutException">No reply came within <paramref name="timeout"/>.</exception>
    public string? Stats(TimeSpan timeout) =>
        _gameHost is { IsRunning: true } gameHost ? gameHost.Stats(timeout) : null;

    public object FromFlashXml(XElement el) => FlashXml.FromFlashXml(el);

    public IFlashObject<T> CreateFlashObject<T>(string path) => new FlashObject<T>(Call<int>("lnkCreate", path), this);

    public void Dispose()
    {
        _gameHost?.Dispose();
        _frameBuffer?.Dispose();
    }

    private void OnInvoked(string request)
    {
        (string function, object[] args) = FlashXml.ReadInvoke(request);
        FlashCall?.Invoke(function, args);
    }
}
