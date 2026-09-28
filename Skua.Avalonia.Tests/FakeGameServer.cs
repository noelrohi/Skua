using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Skua.Avalonia.Tests;

/// <summary>
/// A game server on this Mac for the Packet Interceptor's tests: it answers the simulated game's version check and login as SmartFoxServer
/// does, records each null-terminated message it receives, and sends the game what a test asks. It listens on <c>::1</c>, because the
/// Interceptor's proxy takes the server's port on 127.0.0.1, as it takes a real server's.
/// </summary>
public sealed class FakeGameServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _received = [];
    private readonly Task _serving;
    private NetworkStream? _client;
    private int _closed;

    public FakeGameServer()
    {
        // A port free on 127.0.0.1 too, for the proxy.
        for (int attempt = 0; ; attempt++)
        {
            TcpListener listener = new(IPAddress.IPv6Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                TcpListener probe = new(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                _listener = listener;
                Port = port;
                break;
            }
            catch (SocketException) when (attempt < 10)
            {
                listener.Stop();
            }
        }
        _serving = ServeAsync(_stop.Token);
    }

    public int Port { get; }

    /// <summary>How many connections the proxy has closed.</summary>
    public int Closed => Volatile.Read(ref _closed);

    /// <summary>The messages received so far, in order.</summary>
    public string[] Received()
    {
        lock (_received)
            return [.. _received];
    }

    /// <summary>Sends the connected game a message, as the server's own packet.</summary>
    public void Send(string message)
    {
        NetworkStream client = _client ?? throw new InvalidOperationException("No game is connected.");
        client.Write([.. Encoding.UTF8.GetBytes(message), 0]);
    }

    private async Task ServeAsync(CancellationToken stop)
    {
        List<Task> clients = [];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(stop);
                clients.Add(ServeClientAsync(client, stop));
            }
        }
        catch (OperationCanceledException)
        {
        }
        await Task.WhenAll(clients);
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            NetworkStream stream = client.GetStream();
            _client = stream;
            List<byte> message = [];
            byte[] buffer = new byte[4096];
            try
            {
                for (int read; (read = await stream.ReadAsync(buffer, stop)) > 0;)
                {
                    foreach (byte b in buffer.AsSpan(0, read))
                    {
                        if (b != 0)
                        {
                            message.Add(b);
                            continue;
                        }
                        string text = Encoding.UTF8.GetString([.. message]);
                        message.Clear();
                        lock (_received)
                            _received.Add(text);
                        if (text.Contains("action='verChk'", StringComparison.Ordinal))
                            Send("<msg t='sys'><body action='apiOK' r='0'></body></msg>");
                        else if (text.Contains("action='login'", StringComparison.Ordinal))
                            Send("%xt%loginResponse%-1%true%1%player%Welcome%");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            if (!stop.IsCancellationRequested)
                Interlocked.Increment(ref _closed);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _serving;
        _stop.Dispose();
    }
}
