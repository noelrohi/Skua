using System.Net;
using System.Net.Sockets;

namespace Skua.Engine.Tests;

/// <summary>An <see cref="HttpListener"/> on a free loopback port, for the fake web services.</summary>
/// <remarks>
/// HttpListener can't listen on port 0, so a free port is found first and bound after; another test, or the other test project, can
/// take it in between, and then another port is tried.
/// </remarks>
internal static class LoopbackHttp
{
    private const int Attempts = 20;

    /// <summary>Starts a listener; returns it with its base URL, e.g. <c>http://127.0.0.1:52100/</c>.</summary>
    public static (HttpListener Listener, string BaseUrl) Start()
    {
        for (int attempt = 1; ; attempt++)
        {
            string baseUrl = $"http://127.0.0.1:{FreePort()}/";
            HttpListener listener = new();
            listener.Prefixes.Add(baseUrl);
            try
            {
                listener.Start();
                return (listener, baseUrl);
            }
            catch (HttpListenerException) when (attempt < Attempts)
            {
                listener.Close();
            }
        }
    }

    private static int FreePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
