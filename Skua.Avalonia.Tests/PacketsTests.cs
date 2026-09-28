using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Servers;
using Skua.Core.ViewModels;
using Skua.Engine.Tests;

namespace Skua.Avalonia.Tests;

/// <summary>
/// The Packets menu's Spammer, Logger and Interceptor, over the app's Engine with the simulated game: each opens from the main menu as a
/// user opens it. The Interceptor's game server is a <see cref="FakeGameServer"/> on this Mac; no test reaches a real one.
/// </summary>
[Collection(nameof(GameViewTests))]
public sealed class PacketsTests(AppEngine app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task The_Spammer_sends_a_packet_to_the_server_or_the_game_and_spams_its_list_until_stopped()
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        string emote = $"%xt%zm%emotea%1%spam{id}%";
        string chat = $"%xt%zm%message%1%spam{id}%zone%";
        PacketSpammerViewModel model = app.Get<PacketSpammerViewModel>();
        model.Packets.Clear();
        model.SendToClient = false;
        using EngineConnection connection = await ConnectAsync();
        await connection.LoginAsync("Galanoth", cancellationToken: Ct);
        try
        {
            (Window window, PacketSpammerView spammer) = await OpenAsync<PacketSpammerView>("Spammer");
            try
            {
                TextBox packet = spammer.FindControl<TextBox>("PacketText")!;
                Button send = spammer.FindControl<Button>("Send")!;

                packet.Text = emote;
                Ui.Click(send);
                await WaitForCallAsync($"send {emote}");

                // Send to Client hands it to the game, as if the server had sent it.
                spammer.FindControl<CheckBox>("SendToClient")!.IsChecked = true;
                Ui.Click(send);
                await WaitForCallAsync($"clientPacket str {emote}");
                spammer.FindControl<CheckBox>("SendToClient")!.IsChecked = false;

                // Start sends the list's packets in turn, over and over, and nothing else can change meanwhile.
                Ui.Click(spammer.FindControl<Button>("Add")!);
                packet.Text = chat;
                Ui.Click(spammer.FindControl<Button>("Add")!);
                Assert.Equal([emote, chat], spammer.FindControl<ListBox>("Packets")!.Items.OfType<string>());
                spammer.FindControl<TextBox>("Delay")!.Text = "50";
                Button start = spammer.FindControl<Button>("Start")!;
                Assert.Equal("Start", Ui.Text(start));
                Ui.Click(start);
                await Ui.PumpUntilAsync(() => Sends(emote) >= 3 && Sends(chat) >= 3, "the list's packets to be sent over and over");
                Assert.Equal("Stop", Ui.Text(start));
                Assert.False(send.IsEffectivelyEnabled);
                Assert.False(spammer.FindControl<Button>("Add")!.IsEffectivelyEnabled);

                Ui.Click(start);
                await Ui.PumpUntilAsync(() => !model.SpammerCommand.IsRunning && Ui.Text(start) == "Start", "the spammer to stop");
                int sent = Sends(emote) + Sends(chat);
                await Task.Delay(200, Ct);
                Assert.Equal(sent, Sends(emote) + Sends(chat));
                Assert.True(send.IsEffectivelyEnabled);
            }
            finally
            {
                if (model.SpammerCommand.IsRunning)
                    model.SpammerCommand.Cancel();
                window.Close();
            }
        }
        finally
        {
            model.Packets.Clear();
            model.SendToClient = false;
            model.PacketText = string.Empty;
            await connection.LogoutAsync(Ct);
        }
    }

    [AvaloniaFact]
    public async Task The_Logger_lists_the_games_packets_as_they_come_searched_and_without_the_unchecked_filters()
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        string move = $"%xt%zm%moveToCell%1%r{id}%Spawn%";
        string chat = $"%xt%zm%message%1%hello {id}%zone%";
        PacketLoggerViewModel model = app.Get<PacketLoggerViewModel>();
        model.PacketFilters.ForEach(f => f.IsChecked = true);
        model.IsReceivingPackets = false;
        model.PacketLogs.Clear();
        (Window window, PacketLoggerView logger) = await OpenAsync<PacketLoggerView>("Logger");
        try
        {
            logger.FindControl<ToggleButton>("Enabled")!.IsChecked = true;
            Assert.True(model.IsReceivingPackets);

            // The game's packet calls arrive on the Bridge's thread.
            await AppEngine.DoAsync($"packet {move}");
            await AppEngine.DoAsync($"packet {chat}");
            await Ui.PumpUntilAsync(() => logger.Shown.SequenceEqual([move, chat]), "the game's packets");

            TextBox search = logger.FindControl<TextBox>("SearchBox")!;
            search.Text = $"HELLO {id}";
            await Ui.PumpUntilAsync(() => logger.Shown.SequenceEqual([chat]), "the search to keep the chat packet");
            search.Text = "";
            await Ui.PumpUntilAsync(() => logger.Shown.Count == 2, "every packet again");

            // An unchecked filter's packets aren't logged from then on.
            ItemsControl filters = logger.FindControl<ItemsControl>("Filters")!;
            Filter(filters, "Chat").IsChecked = false;
            Assert.False(model.PacketFilters.Single(f => f.Content == "Chat").IsChecked);
            string laterChat = $"%xt%zm%message%1%later {id}%zone%";
            string laterMove = $"%xt%zm%moveToCell%1%later{id}%Spawn%";
            await AppEngine.DoAsync($"packet {laterChat}");
            await AppEngine.DoAsync($"packet {laterMove}");
            await Ui.PumpUntilAsync(() => logger.Shown.Contains(laterMove), "the later move");
            Assert.DoesNotContain(laterChat, model.PacketLogs);

            // The filters are searched by name.
            logger.FindControl<TextBox>("FilterSearchBox")!.Text = "CHA";
            await Ui.PumpUntilAsync(() => FilterNames(filters).SequenceEqual(["Chat"]), "the filter search");

            Ui.Click(logger.FindControl<Button>("Clear")!);
            await Ui.PumpUntilAsync(() => logger.Shown.Count == 0, "the cleared list");
        }
        finally
        {
            model.IsReceivingPackets = false;
            model.PacketFilters.ForEach(f => f.IsChecked = true);
            model.PacketLogs.Clear();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_Interceptor_relays_the_game_through_its_proxy_and_lists_each_packet_by_direction()
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        await using FakeGameServer server = new();
        PacketInterceptorViewModel model = app.Get<PacketInterceptorViewModel>();
        ICaptureProxy proxy = app.Get<ICaptureProxy>();
        IScriptServers servers = app.Get<IScriptServers>();
        Server local = new() { Name = $"Interceptor {id}", IP = "::1", Port = server.Port, Online = true };
        Blocker blocker = new($"block{id}");
        model.PacketFilters.ForEach(f => f.IsChecked = true);
        model.IsLogging = true;
        model.Packets.Clear();
        using EngineConnection connection = await ConnectAsync();
        await connection.LoginAsync("Galanoth", cancellationToken: Ct);
        servers.CachedServers.Add(local);
        proxy.Interceptors.Add(blocker);
        try
        {
            (Window window, PacketInterceptorView view) = await OpenAsync<PacketInterceptorView>("Interceptor");
            try
            {
                view.FindControl<ComboBox>("Servers")!.SelectedItem = local;
                Assert.Same(local, model.SelectedServer);
                Button connect = view.FindControl<Button>("Connect")!;
                Assert.Equal("Connect", Ui.Text(connect));

                // Connect logs out and reconnects: so the game first settles from its login, with no map load or Script stop still going.
                IScriptPlayer player = app.Get<IScriptPlayer>();
                IScriptMap map = app.Get<IScriptMap>();
                IScriptManager scripts = app.Get<IScriptManager>();
                await Ui.PumpUntilAsync(() => player.Playing && player.Loaded && map.Loaded && !scripts.ShouldExit, "the game to settle");

                // The game reconnects to the proxy on 127.0.0.1, which relays it to the server; it logs in there and enters the world.
                server.ReleaseProxyPort();
                Ui.Click(connect);
                await Ui.PumpUntilAsync(() => server.Received().Any(m => m.Contains("action='login'", StringComparison.Ordinal)), "the game's login through the proxy");
                await Ui.PumpUntilAsync(() => connect.IsEffectivelyEnabled && Ui.Text(connect) == "Disconnect", "Connect to finish");
                Assert.True(proxy.Running);
                // Connect waits for the world only a few seconds, and ends with the game still entering it on a slow Mac.
                await Ui.PumpUntilAsync(() => player.Playing, "the game to enter the world through the proxy");
                Assert.Contains($"connectTo 127.0.0.1 {server.Port}", GameCalls());
                await Ui.PumpUntilAsync(
                    () => Has(view, "action='verChk'", true) && Has(view, "action='apiOK'", false) && Has(view, "%xt%loginResponse%", false),
                    "the handshake, listed by direction");

                // A packet the game sends goes through to the server.
                string move = $"%xt%zm%moveToCell%1%r{id}%Spawn%";
                await SendAsync(move);
                await Ui.PumpUntilAsync(() => server.Received().Contains(move), "the server to receive the packet");
                await Ui.PumpUntilAsync(() => Has(view, move, true), "the outbound packet");

                // One an interceptor blocks never reaches the server, and is listed as blocked.
                string blocked = $"%xt%zm%message%1%block{id}%zone%";
                string after = $"%xt%zm%message%1%after{id}%zone%";
                await SendAsync(blocked);
                await SendAsync(after);
                await Ui.PumpUntilAsync(() => server.Received().Contains(after), "the packet after the blocked one");
                Assert.DoesNotContain(blocked, server.Received());
                await Ui.PumpUntilAsync(() => Has(view, blocked, null), "the blocked packet");

                // The server's own packets come in.
                string inbound = $"%xt%uotls%-1%artixfan%afk:{id}%";
                string moved = """{"t":"xt","b":{"r":-1,"o":{"cmd":"moveToCell","strFrame":"r""" + id + "\"}}}";
                server.Send(inbound);
                server.Send(moved);
                await Ui.PumpUntilAsync(() => Has(view, inbound, false) && Has(view, moved, false), "the inbound packets");
                ListBox packets = view.FindControl<ListBox>("Packets")!;
                Assert.Equal(PacketInterceptorView.BlockedBrush, await RowBackgroundAsync(packets, blocked));
                Assert.Equal(PacketInterceptorView.InboundBrush, await RowBackgroundAsync(packets, inbound));

                // As the WPF view's code filters it: an unchecked filter hides its packets (the server's, which Core's filters read as JSON), and
                // the search keeps the matching ones.
                ItemsControl filters = view.FindControl<ItemsControl>("Filters")!;
                Filter(filters, "Jump").IsChecked = false;
                await Ui.PumpUntilAsync(() => !Has(view, moved, false) && Has(view, inbound, false), "the Jump filter to hide the server's move");
                Filter(filters, "Jump").IsChecked = true;
                await Ui.PumpUntilAsync(() => Has(view, moved, false), "the move to show again");
                TextBox search = view.FindControl<TextBox>("SearchBox")!;
                search.Text = $"AFTER{id}";
                await Ui.PumpUntilAsync(() => view.Shown.Count == 1 && Has(view, after, true), "the search to keep one packet");
                search.Text = "";
                view.FindControl<TextBox>("FilterSearchBox")!.Text = "jum";
                await Ui.PumpUntilAsync(() => FilterNames(filters).SequenceEqual(["Jump"]), "the filter search");

                // Disconnect stops the proxy, which closes both connections and lets go of its port.
                Ui.Click(connect);
                await Ui.PumpUntilAsync(() => connect.IsEffectivelyEnabled && Ui.Text(connect) == "Connect", "Disconnect to finish");
                Assert.False(proxy.Running);
                await Ui.PumpUntilAsync(() => server.Closed >= 1, "the proxy to close the server's connection");
                await Ui.PumpUntilAsync(() => !Accepts(server.Port), "the proxy to stop listening");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            if (proxy.Running)
                await Task.Run(() => model.ConnectInterceptorCommand.Execute(null), Ct);
            proxy.Interceptors.Remove(blocker);
            servers.CachedServers.Remove(local);
            model.SelectedServer = null;
            model.PacketFilters.ForEach(f => f.IsChecked = true);
            model.Packets.Clear();
            await connection.LogoutAsync(Ct);
        }
    }

    /// <summary>Sends a packet as a Script does, off the UI thread, where the Interceptor lists what its proxy relays.</summary>
    private Task SendAsync(string packet) => Task.Run(() => app.Get<IScriptSend>().Packet(packet), Ct);

    /// <summary>Whether the Interceptor's list shows <paramref name="packet"/> going that way (null: blocked).</summary>
    private static bool Has(PacketInterceptorView view, string packet, bool? outbound) =>
        view.Shown.OfType<InterceptedPacketViewModel>().Any(p => p.Packet.Contains(packet, StringComparison.Ordinal) && p.Outbound == outbound);

    private static async Task<object?> RowBackgroundAsync(ListBox packets, string packet)
    {
        object item = packets.Items.OfType<InterceptedPacketViewModel>().Single(p => p.Packet == packet);
        packets.ScrollIntoView(item);
        await Ui.PumpUntilAsync(() => packets.ContainerFromItem(item) is not null, "the packet's row");
        return ((ListBoxItem)packets.ContainerFromItem(item)!).Background;
    }

    private static CheckBox Filter(ItemsControl filters, string name) =>
        filters.GetLogicalDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == name);

    private static List<string> FilterNames(ItemsControl filters) =>
        filters.Items.OfType<PacketLogFilterViewModel>().Select(f => f.Content).ToList();

    private static bool Accepts(int port)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>How many times the game has sent <paramref name="packet"/> to the server.</summary>
    private static int Sends(string packet) => GameCalls().Count(c => c == $"send {packet}");

    private static string[] GameCalls() => File.Exists(AppEngine.CallLog) ? File.ReadAllLines(AppEngine.CallLog) : [];

    private static Task WaitForCallAsync(string call) =>
        Ui.PumpUntilAsync(() => GameCalls().Contains(call), $"the game's '{call}'");

    /// <summary>Opens a panel from the main menu's Packets group, as a click does, and returns its window and view.</summary>
    private async Task<(Window, T)> OpenAsync<T>(string item) where T : global::Avalonia.Visual
    {
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        Menu menu = MainMenus.InWindow(app.Get<MainMenuViewModel>(), windows);
        MenuItem packets = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header! == "Packets");
        MenuItem leaf = packets.Items.OfType<MenuItem>().Single(i => (string)i.Header! == item);
        Assert.True(leaf.IsEnabled, $"Packets → {item} is disabled");
        leaf.Command!.Execute(null);
        await Ui.PumpUntilAsync(() => windows.OpenWindow(item) is not null, $"the {item} window");
        Window window = windows.OpenWindow(item)!;
        try
        {
            await Ui.PumpUntilAsync(() => Ui.Find<T>(window) is not null, $"a {typeof(T).Name}");
        }
        catch
        {
            window.Close();
            throw;
        }
        return (window, Ui.Find<T>(window)!);
    }

    private static async Task<EngineConnection> ConnectAsync() =>
        await EngineClient.TryConnectAsync(EngineEndpoint.FromEnvironment(), Ct) ?? throw new InvalidOperationException("The app's Engine didn't answer.");

    /// <summary>Blocks the packets that contain its marker, as a plugin's interceptor may.</summary>
    private sealed class Blocker(string marker) : IInterceptor
    {
        public int Priority => 0;

        public void Intercept(MessageInfo message, bool outbound)
        {
            if (message.Content.Contains(marker, StringComparison.Ordinal))
                message.Send = false;
        }
    }
}
