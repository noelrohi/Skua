using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Utils;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Tests;

/// <summary>
/// The app menu's About, Change Logs and GitHub sign-in, against <see cref="FakeGitHubWeb"/> and the fake Keychain, and each window's Top Most.
/// </summary>
/// <remarks>In the Game View tests' collection, as they share the one Engine. Every window is closed, and Core's GitHub client put back, in a finally.</remarks>
[Collection(nameof(GameViewTests))]
public sealed class AppMenuTests(AppEngine app)
{
    private AvaloniaWindowService Windows => app.Get<AvaloniaWindowService>();

    [AvaloniaFact]
    public async Task About_and_Change_Logs_open_once_from_the_app_menu_and_show_their_pages()
    {
        NativeMenu menu = AppMenu.Create(app.Engine.Services, Windows);
        Assert.Equal([AppMenu.AboutHeader, AppMenu.ChangeLogsHeader, AppMenu.GitHubHeader], menu.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).Select(i => i.Header));
        using PanelTests.BindingErrors errors = new();
        try
        {
            Item(menu, AppMenu.AboutHeader).Command!.Execute(null);
            HostWindow about = await OpenedAsync(AppMenu.AboutKey);
            Assert.Equal("About", about.Title);
            MarkdownView page = Ui.Find<AboutView>(about)!.FindControl<MarkdownView>("Page")!;
            await Ui.PumpUntilAsync(() => page.Children.Count > 0 && ((AboutViewModel)about.DataContext!).MarkdownDoc == FakeGitHubWeb.Readme, "the readme");
            Assert.Contains(page.Children.OfType<TextBlock>(), t => t.Inlines!.Text == "About the fake Skua");
            Assert.Contains(page.Children.OfType<TextBlock>(), t => t.Inlines!.Text == "• Story scripts in the Story folder.");
            Assert.Equal(["./usage.md", "./BUILD.md", "https://discord.com/invite/fake"], page.Links.Select(l => l.CommandParameter));
            Assert.All(page.Links, l => Assert.Same(((AboutViewModel)about.DataContext!).NavigateCommand, l.Command));
            // A second click brings the same window to the front.
            Item(menu, AppMenu.AboutHeader).Command!.Execute(null);
            await Ui.PumpUntilAsync(() => true, "the second click");
            Assert.Same(about, Windows.OpenWindow(AppMenu.AboutKey));

            Item(menu, AppMenu.ChangeLogsHeader).Command!.Execute(null);
            HostWindow changeLogs = await OpenedAsync(AppMenu.ChangeLogsKey);
            Assert.Equal("Change Logs", changeLogs.Title);
            ChangeLogsView view = Ui.Find<ChangeLogsView>(changeLogs)!;
            MarkdownView changes = view.FindControl<MarkdownView>("Page")!;
            await Ui.PumpUntilAsync(() => changes.Children.OfType<TextBlock>().Any(t => t.Inlines!.Text == "1.2.3"), "the change logs");
            // The Mac App's own change log comes first, then upstream's under its own heading.
            string[] headings = [.. changes.Children.OfType<TextBlock>().Select(t => t.Inlines!.Text).Where(t => t is "Skua for Mac" or "9.8.7" or "Skua for Windows" or "1.2.3")!];
            Assert.Equal(["Skua for Mac", "9.8.7", "Skua for Windows", "1.2.3"], headings);
            // A link in the Mac section opens on this fork; one in upstream's opens on upstream, as on Windows.
            Assert.Equal([MacChangeLogs.MacBlob + "BUILD.md#install-on-macos", "./usage.md"], changes.Links.Select(l => l.CommandParameter));
            Assert.All(changes.Links, l => Assert.Same(((ChangeLogsViewModel)changeLogs.DataContext!).NavigateCommand, l.Command));
            int launches = Launches.Runs().Count;
            Ui.Click(changes.Links[0]);
            await Ui.PumpUntilAsync(() => Launches.Runs().Count > launches, "the Mac link to open");
            Assert.Equal(("/usr/bin/open", "https://github.com/noelrohi/Skua/blob/master/BUILD.md#install-on-macos"), (Launches.Runs()[launches].Program, Assert.Single(Launches.Runs()[launches].Arguments)));
            launches = Launches.Runs().Count;
            Ui.Click(changes.Links[1]);
            await Ui.PumpUntilAsync(() => Launches.Runs().Count > launches, "the upstream link to open");
            Assert.Equal(("/usr/bin/open", "https://github.com/auqw/Skua/blob/master/usage.md"), (Launches.Runs()[launches].Program, Assert.Single(Launches.Runs()[launches].Arguments)));
            launches = Launches.Runs().Count;
            Ui.Click(view.FindControl<Button>("Donate")!);
            await Ui.PumpUntilAsync(() => Launches.Runs().Count > launches, "the donation link to open");
            Assert.Equal(("/usr/bin/open", "https://ko-fi.com/sharpthenightmare"), (Launches.Runs()[launches].Program, Assert.Single(Launches.Runs()[launches].Arguments)));
            Assert.True(errors.Lines.Count == 0, string.Join("\n", errors.Lines));
        }
        finally
        {
            Windows.OpenWindow(AppMenu.AboutKey)?.Close();
            Windows.OpenWindow(AppMenu.ChangeLogsKey)?.Close();
        }
        Assert.Contains("GET raw/auqw/Skua/refs/heads/master/readme.md", FakeGitHubWeb.Requests);
        Assert.Contains("GET raw/auqw/Skua/refs/heads/master/changelogs.md", FakeGitHubWeb.Requests);
        Assert.Contains("GET raw/noelrohi/Skua/refs/heads/master/changelogs-mac.md", FakeGitHubWeb.Requests);
    }

    [AvaloniaFact]
    public async Task GitHub_login_completes_against_the_fake_device_flow_and_the_token_lands_in_Keychain_only()
    {
        string token = "gho_fake" + Guid.NewGuid().ToString("N");
        FakeGitHubWeb.DeviceFlow = ("device-" + Guid.NewGuid().ToString("N")[..8], "ABCD-1234", token);
        int requests = FakeGitHubWeb.Requests.Count;
        WebClient? signedIn = HttpClients.UserGitHubClient;
        // Without a user client the device flow goes through Core's GitHub client, which the fake answers.
        HttpClients.UserGitHubClient = null;
        using TraceLines traces = new();
        using PanelTests.BindingErrors errors = new();
        try
        {
            Item(AppMenu.Create(app.Engine.Services, Windows), AppMenu.GitHubHeader).Command!.Execute(null);
            HostWindow window = await OpenedAsync(AppMenu.GitHubKey);
            Assert.Equal("GitHub Authentication", window.Title);
            GitHubAuthView view = Ui.Find<GitHubAuthView>(window)!;
            TextBlock hint = view.FindControl<TextBlock>("Hint")!;

            Ui.Click(view.FindControl<Button>("GetUserCode")!);
            await Ui.PumpUntilAsync(() => view.FindControl<TextBox>("UserCode")!.Text == "ABCD-1234", "the user code");
            Assert.Equal("Copied. Click \"Open Browser\".", hint.Text);

            int launches = Launches.Runs().Count;
            Ui.Click(view.FindControl<Button>("OpenBrowser")!);
            await Ui.PumpUntilAsync(() => Launches.Runs().Count > launches, "GitHub's device page to open");
            Assert.Equal(("/usr/bin/open", "https://github.com/login/device"), (Launches.Runs()[launches].Program, Assert.Single(Launches.Runs()[launches].Arguments)));

            Ui.Click(view.FindControl<Button>("Authorize")!);
            await Ui.PumpUntilAsync(() => hint.Text == "All good to go!", "the sign-in to finish");

            Assert.Equal(["POST /login/device/code", "POST /login/oauth/access_token"], FakeGitHubWeb.Requests.Skip(requests).Where(r => !r.StartsWith("GET raw/", StringComparison.Ordinal)));
            Assert.Equal($"password: \"{token}\"", AppEngine.Keychain.Find(GitHubToken.Service)?.PasswordLine);
            Assert.Equal(token, app.Get<ISettingsService>().Get<string>(GitHubToken.SettingKey));
            Assert.Equal(token, HttpClients.UserGitHubClient?.DefaultRequestHeaders.Authorization?.Parameter);
            // In Keychain's item alone: not in the settings file, the logs, the fake security tool's log or anything else in the data folder.
            Assert.Equal([Path.Combine(AppEngine.Keychain.Items, GitHubToken.Service, "password")], FilesHolding(token));
            Assert.Equal("", app.Get<ISettingsService>().GetShared().UserGitHubToken);
            Assert.Equal(0, traces.Lines.Count(l => l.Contains(token, StringComparison.Ordinal)));
            Assert.Equal(0, app.Get<ILogService>().GetLogs(LogType.Debug).Count(l => l.Contains(token, StringComparison.Ordinal)));
            Assert.True(errors.Lines.Count == 0, string.Join("\n", errors.Lines));

            Ui.Click(view.FindControl<Button>("Close")!);
            await Ui.PumpUntilAsync(() => Windows.OpenWindow(AppMenu.GitHubKey) is null, "the window to close");
        }
        finally
        {
            Windows.OpenWindow(AppMenu.GitHubKey)?.Close();
            HttpClients.UserGitHubClient = signedIn;
            // Signs out again, which removes the item.
            app.Get<GitHubToken>().Save(null);
        }
        Assert.Null(AppEngine.Keychain.Find(GitHubToken.Service));
    }

    [AvaloniaFact]
    public async Task A_token_in_Keychain_is_read_at_start_for_Cores_GitHub_requests_and_none_is_asked_for_without_one()
    {
        WebClient? signedIn = HttpClients.UserGitHubClient;
        HttpClients.UserGitHubClient = null;
        string token = "gho_saved" + Guid.NewGuid().ToString("N");
        try
        {
            // Without an item it looks only, so macOS never asks for access.
            int reads = AppEngine.Keychain.Reads;
            GitHubToken none = new();
            Assert.False(await none.LoadAsync(TestContext.Current.CancellationToken));
            Assert.Equal((reads, null, null), (AppEngine.Keychain.Reads, none.Token, HttpClients.UserGitHubClient));

            AppEngine.Keychain.Add(GitHubToken.Service, "github", token);
            GitHubToken saved = new();
            Assert.True(await saved.LoadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(token, saved.Token);
            Assert.Equal(token, HttpClients.UserGitHubClient?.DefaultRequestHeaders.Authorization?.Parameter);
            Assert.Same(HttpClients.UserGitHubClient, HttpClients.GetGHClient());
        }
        finally
        {
            HttpClients.UserGitHubClient = signedIn;
            Directory.Delete(Path.Combine(AppEngine.Keychain.Items, GitHubToken.Service), recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Top_Most_toggles_from_each_windows_Window_menu_and_persists()
    {
        TopMost topMost = app.Get<TopMost>();
        string name = "TopMostTest" + Guid.NewGuid().ToString("N")[..8];
        List<Window> windows = [];
        try
        {
            HostWindow window = new(app.Get<ConsoleViewModel>());
            windows.Add(window);
            NativeMenuItem menu = topMost.Menu(window, name);
            NativeMenuItem item = Assert.Single(menu.Menu!.Items.OfType<NativeMenuItem>());
            window.Show();
            Assert.Equal(("Window", TopMost.Header, MenuItemToggleType.CheckBox), (menu.Header, item.Header, item.ToggleType));
            Assert.Equal((false, false), (item.IsChecked, window.Topmost));

            item.Command!.Execute(null);
            Assert.Equal((true, true), (item.IsChecked, window.Topmost));
            Assert.Contains(name, SavedTopMost());

            // The next window of that name opens on top; another name doesn't.
            HostWindow again = new(app.Get<ConsoleViewModel>());
            windows.Add(again);
            Assert.True(Assert.Single(topMost.Menu(again, name).Menu!.Items.OfType<NativeMenuItem>()).IsChecked);
            Assert.True(again.Topmost);
            HostWindow other = new(app.Get<ConsoleViewModel>());
            windows.Add(other);
            topMost.Menu(other, name + "-other");
            Assert.False(other.Topmost);

            item.Command!.Execute(null);
            Assert.Equal((false, false), (item.IsChecked, window.Topmost));
            Assert.DoesNotContain(name, SavedTopMost());

            // As read back from the file at the next start.
            ClientSettings client = app.Get<ISettingsService>().GetClient();
            object? before = client.ExtensionData![AppSettingsService.TopMostKey];
            try
            {
                client.ExtensionData[AppSettingsService.TopMostKey] = JsonDocument.Parse($"[\"{name}\"]").RootElement.Clone();
                Assert.True(topMost.IsOn(name));
                client.ExtensionData[AppSettingsService.TopMostKey] = JsonDocument.Parse("{\"not\":\"a list\"}").RootElement.Clone();
                Assert.False(topMost.IsOn(name));
            }
            finally
            {
                client.ExtensionData[AppSettingsService.TopMostKey] = before!;
            }
            Assert.Equal(nameof(ConsoleViewModel), TopMost.NameOf(app.Get<ConsoleViewModel>()));
            await Ui.PumpUntilAsync(() => true, "a layout pass");
        }
        finally
        {
            foreach (Window window in windows)
            {
                topMost.Set(window, name, false);
                window.Close();
            }
        }
    }

    private static NativeMenuItem Item(NativeMenu menu, string header) => menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == header);

    private async Task<HostWindow> OpenedAsync(string key)
    {
        await Ui.PumpUntilAsync(() => Windows.OpenWindow(key) is { IsVisible: true }, $"the {key} window");
        return Windows.OpenWindow(key)!;
    }

    /// <summary>The names in the settings file's Top Most list.</summary>
    private static List<string> SavedTopMost()
    {
        JsonNode? root = JsonNode.Parse(File.ReadAllText(ClientFileSources.SkuaSettingsDIR));
        return root?["client"]?[AppSettingsService.TopMostKey]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
    }

    /// <summary>Every file in the data folder holding <paramref name="text"/>.</summary>
    private static List<string> FilesHolding(string text) =>
        [.. Directory.EnumerateFiles(AppEngine.SkuaDir, "*", SearchOption.AllDirectories).Where(f => Holds(f, text))];

    private static bool Holds(string file, string text)
    {
        try
        {
            return File.ReadAllText(file).Contains(text, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The Engine's socket, for one.
            return false;
        }
    }

    /// <summary>Collects the process's trace lines while it lives.</summary>
    private sealed class TraceLines : TraceListener
    {
        public TraceLines() => Trace.Listeners.Add(this);

        public List<string> Lines { get; } = [];

        public override void Write(string? message) => WriteLine(message);

        public override void WriteLine(string? message)
        {
            lock (Lines)
                Lines.Add(message ?? "");
        }

        protected override void Dispose(bool disposing)
        {
            Trace.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
