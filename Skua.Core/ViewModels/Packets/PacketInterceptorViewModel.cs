using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Servers;
using Skua.Core.Utils;
using System.Diagnostics;
using System.Net;

namespace Skua.Core.ViewModels;

public partial class PacketInterceptorViewModel : BotControlViewModelBase
{

    public PacketInterceptorViewModel(IEnumerable<PacketLogFilterViewModel> filters, ICaptureProxy gameProxy, IScriptServers server, IScriptPlayer player, IFlashUtil flash)
        : base("Packet Interceptor")
    {
        _gameProxy = gameProxy;
        _server = server;
        _player = player;
        _flash = flash;
        _packetFilters = filters.ToList();
        ClearPacketsCommand = new RelayCommand(Packets.Clear);
        SynchronizationContext? context = SynchronizationContext.Current;
        void addFunc(InterceptedPacketViewModel st) => context?.Send(obj => Packets.Add((InterceptedPacketViewModel)obj!), st);
        _logger = new InterceptorLogger(addFunc);
        IsLogging = true;
    }

    protected override void OnActivated()
    {
        Messenger.Register<PacketInterceptorViewModel, PropertyChangedMessage<bool>>(this, RunningChanged);
        OnPropertyChanged(nameof(Running));
    }

    private readonly ICaptureProxy _gameProxy;
    private readonly IScriptServers _server;
    private readonly IScriptPlayer _player;
    private readonly IFlashUtil _flash;
    private readonly InterceptorLogger _logger;

    [ObservableProperty]
    private Server? _selectedServer;

    /// <summary>Why the last Connect didn't get the game into the world through the proxy; null if it did, or while one is going.</summary>
    [ObservableProperty]
    private string? _connectFailure;

    [ObservableProperty]
    private RangedObservableCollection<InterceptedPacketViewModel> _packets = new();

    [ObservableProperty]
    private List<PacketLogFilterViewModel> _packetFilters;

    private bool _isLogging;

    public bool IsLogging
    {
        get => _isLogging;
        set
        {
            if (SetProperty(ref _isLogging, value))
            {
                if (value)
                    _gameProxy.Interceptors.Add(_logger);
                else
                    _gameProxy.Interceptors.Remove(_logger);
            }
        }
    }

    ~PacketInterceptorViewModel()
    {
        _logger?.Dispose();
    }

    public bool Running => _gameProxy.Running;
    public List<Server> ServerList => _server.CachedServers;
    public IRelayCommand ClearPacketsCommand { get; }

    [RelayCommand]
    private void ClearFilters()
    {
        _packetFilters.ForEach(f => f.IsChecked = false);
    }

    [RelayCommand]
    private async Task ConnectInterceptor()
    {
        ConnectFailure = null;
        if (_gameProxy.Running)
        {
            _gameProxy.Stop();
            OnPropertyChanged(nameof(Running));
            return;
        }

        if (SelectedServer is null)
            return;

        IScriptOption options = Ioc.Default.GetRequiredService<IScriptOption>();
        bool relogin = options.AutoRelogin;
        options.AutoRelogin = false;
        try
        {
            IPAddress ip = IPAddress.TryParse(SelectedServer.IP, out IPAddress? addr) ? addr : Dns.GetHostEntry(SelectedServer.IP).AddressList[0];
            int port = SelectedServer.Port != 0 ? SelectedServer.Port : 5588;
            _gameProxy.Destination = new IPEndPoint(ip, port);
            _gameProxy.Start();
            _server.Logout();
            _server.Login();
            _server.ConnectIP("127.0.0.1", port);
            OnPropertyChanged(nameof(Running));

            // ConnectIP waits for the world only a few seconds, and not at all while any Script is stopping, so Connect waits for it here, up
            // to the Login Timeout. It awaits between checks, so a UI thread that runs this keeps listing the packets the proxy relays.
            TimeSpan timeout = TimeSpan.FromMilliseconds(Math.Max(options.LoginTimeout, 1000));
            if (!await WaitForWorldAsync(timeout))
            {
                _gameProxy.Stop();
                OnPropertyChanged(nameof(Running));
                ConnectFailure = $"The game didn't enter the world through the interceptor within {timeout.TotalSeconds:0} s, so the interceptor stopped.";
            }
        }
        finally
        {
            options.AutoRelogin = relogin;
        }
    }

    private async Task<bool> WaitForWorldAsync(TimeSpan timeout)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!(_player.Playing && _flash.IsWorldLoaded))
        {
            if (waited.Elapsed >= timeout)
                return false;
            await Task.Delay(250);
        }
        return true;
    }

    private void RunningChanged(PacketInterceptorViewModel recipient, PropertyChangedMessage<bool> message)
    {
        if (message.PropertyName == nameof(ICaptureProxy.Running))
            recipient.OnPropertyChanged(nameof(recipient.Running));
    }
}

public class InterceptorLogger : IInterceptor, IDisposable
{
    public const bool LogToFile = false;

    private readonly Action<InterceptedPacketViewModel> _addFunc;
    private readonly StreamWriter _logWriter;
    private readonly object _logLock = new();

    public int Priority => int.MaxValue;

    public InterceptorLogger(Action<InterceptedPacketViewModel> addFunc)
    {
        _addFunc = addFunc;
        if (LogToFile)
        {
            string logPath = Path.Combine(ClientFileSources.SkuaDIR, "InterceptedLogs.txt");
            _logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        }
    }

    public void Intercept(MessageInfo message, bool outbound)
    {
        _addFunc(new(message.Content, message.Send ? outbound : null));

        if (LogToFile)
        {
            lock (_logLock)
            {
                string direction = message.Send ? (outbound ? "OUT" : "IN") : "UNKNOWN";
                _logWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{direction}] {message.Content}");
            }
        }
    }

    public void Dispose()
    {
        if (_logWriter != null)
        {
            _logWriter?.Dispose();
        }
    }
}
