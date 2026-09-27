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

    public BridgeFlashUtil(IMessenger messenger, Lazy<IScriptManager> manager, GameHostLaunch launch)
    {
        _messenger = messenger;
        _lazyManager = manager;
        _launch = launch;
    }

    public event FlashCallHandler? FlashCall;

    /// <summary>Raised when a Game Host process has started, with its pid.</summary>
    public event Action<int>? GameHostStarted;

    /// <summary>Raised when the Game Host process ends, with its exit code.</summary>
    public event Action<int>? GameHostExited;

    /// <summary>Raised for each Ruffle/wgpu log line and stderr line of the Game Host.</summary>
    public event Action<string>? GameHostLog;

    /// <summary>Raised for each Flash log line: AS3 <c>trace()</c>, warnings and uncaught AS3 errors.</summary>
    public event Action<string>? FlashLog;

    public bool IsGameHostRunning => _gameHost?.IsRunning ?? false;

    /// <summary>The names the Game Client has registered for the Engine to call.</summary>
    public IReadOnlyList<string> Callbacks => _gameHost?.Callbacks ?? [];

    /// <summary>Starts the Game Host with the Game Client, closing any earlier one first.</summary>
    /// <exception cref="FileNotFoundException">The Game Host executable doesn't exist.</exception>
    public void InitializeFlash()
    {
        _gameHost?.Dispose();

        GameHostProcess gameHost = new(_launch.Executable, _launch.Arguments);
        gameHost.Invoked += OnInvoked;
        gameHost.FlashLog += line => FlashLog?.Invoke(line);
        gameHost.LogLine += line => GameHostLog?.Invoke(line);
        gameHost.Exited += code => GameHostExited?.Invoke(code);
        gameHost.Start();
        _gameHost = gameHost;
        GameHostStarted?.Invoke(gameHost.Pid);
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

    public object FromFlashXml(XElement el) => FlashXml.FromFlashXml(el);

    public IFlashObject<T> CreateFlashObject<T>(string path) => new FlashObject<T>(Call<int>("lnkCreate", path), this);

    public void Dispose() => _gameHost?.Dispose();

    private void OnInvoked(string request)
    {
        (string function, object[] args) = FlashXml.ReadInvoke(request);
        FlashCall?.Invoke(function, args);
    }
}
