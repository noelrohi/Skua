using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Skua.Avalonia.Manager;
using Skua.Avalonia.Views.Manager;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.ViewModels;
using Skua.Core.ViewModels.Manager;
using Skua.Engine.Tests;

namespace Skua.Avalonia.Tests;

/// <summary>
/// The Skua Manager on macOS, against the fake Keychain and fake game: accounts whose passwords live only in Keychain, the Windows import, and
/// launching a Mac App per account, played by <c>fake-app</c>, which hosts an Engine as the app does, without a window.
/// </summary>
[Collection(nameof(GameViewTests))]
public sealed class ManagerTests : IDisposable
{
    private readonly AppEngine _app;

    /// <summary>The accounts the fake game accepts, besides the Test Account; each password is unique, so a match count finds it anywhere.</summary>
    private static readonly (string Username, string Password)[] GameAccounts =
    [
        ("AliceTester", "alice-Sekrit-7f3a"),
        ("BobTester", "bob-Sekrit-91c2"),
        ("CarolTester", "carol-Sekrit-5d0e"),
    ];

    private readonly string _dir;
    private readonly ServiceProvider _services;
    private readonly ScriptedDialogs _dialogs = new();

    public ManagerTests(AppEngine app)
    {
        _app = app;
        // The launched apps share the sandbox's data folder, Keychain and fake Game Host with the in-process Engine, but play their own game.
        string scenario = Path.Combine(AppEngine.SkuaDir, "fake-app.scenario");
        File.WriteAllLines(scenario,
        [
            $"calllog {Path.Combine(AppEngine.SkuaDir, "fake-app-gamehost.calls")}",
            $"game {AppEngine.Keychain.Username} {AppEngine.Keychain.Password}",
            .. GameAccounts.Select(a => $"account {a.Username} {a.Password}"),
            $"servers {FakeServer.ListJson(AppEngine.Servers)}",
            """send E <invoke name="loaded" returntype="xml"><arguments></arguments></invoke>""",
        ]);
        Environment.SetEnvironmentVariable("SKUA_FAKE_APP_SCENARIO", scenario);
        Environment.SetEnvironmentVariable(AppInstances.ExecutableVariable, Path.Combine(AppContext.BaseDirectory, "fake-app"));
        ManagerAccountsViewModel.LaunchSpacing = TimeSpan.FromMilliseconds(100);

        // Each test keeps its Manager's list in a folder of its own; the apps it launches use the sandbox's.
        _dir = Path.Combine(AppEngine.SkuaDir, "manager-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _services = Build(_dir);
    }

    private ManagerAccountsViewModel Accounts => _services.GetRequiredService<ManagerAccountsViewModel>();

    private ServiceProvider Build(string dir)
    {
        ServiceCollection services = new();
        services.AddManagerServices(dir);
        // The launched apps live in the sandbox, next to the in-process Engine.
        services.AddSingleton(new AppInstances(AppEngine.SkuaDir));
        services.AddSingleton<IDialogService>(_dialogs);
        return services.BuildServiceProvider();
    }

    [AvaloniaFact]
    public async Task Adding_editing_and_removing_an_account_keeps_its_password_only_in_Keychain()
    {
        AccountManagerViewModel list = Accounts.Accounts;
        Add(list, "Alice Tester", "first-Sekrit-c41d", "Alice");

        string service = Skua.Control.Accounts.ServiceOf("alice-tester");
        Assert.Contains("first-Sekrit-c41d", AppEngine.Keychain.Find(service)!.Value.PasswordLine);
        Assert.Equal("Alice Tester", AppEngine.Keychain.Find(service)!.Value.Account);
        AccountItemViewModel alice = Assert.Single(list.Accounts);
        Assert.Equal("", alice.Password);
        Assert.Equal(0, MatchesOutsideKeychain("first-Sekrit-c41d"));

        // The same username again edits it: a new password and display name.
        Add(list, "Alice Tester", "second-Sekrit-e9b0", "Alice the Second");
        Assert.Contains("second-Sekrit-e9b0", AppEngine.Keychain.Find(service)!.Value.PasswordLine);
        Assert.Equal("Alice the Second", Assert.Single(list.Accounts).DisplayName);
        Assert.Equal(0, MatchesOutsideKeychain("first-Sekrit-c41d") + MatchesOutsideKeychain("second-Sekrit-e9b0"));

        // A Manager started afresh reads the list back, without a password.
        Assert.Equal(["Alice the Second"], Reopen(m => m.Accounts.Select(a => a.DisplayName).ToList()));

        alice.RemoveCommand.Execute(null);
        await Ui.PumpUntilAsync(() => AppEngine.Keychain.Find(service) is null, "the account to leave Keychain");
        // By username, so a failure says which accounts are left.
        Assert.Empty(list.Accounts.Select(a => $"{a.Username} ({a.DisplayName})"));
        Assert.Empty(new ManagerAccounts(_dir).Accounts);
        Assert.Equal(0, MatchesOutsideKeychain("second-Sekrit-e9b0"));
    }

    [AvaloniaFact]
    public async Task Importing_a_Windows_list_moves_its_passwords_to_Keychain_and_keeps_a_backup()
    {
        string file = Path.Combine(_dir, "windows", "Skua.settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $$"""
            {
              "formatVersion": 1,
              "shared": { "sBG": "Generic2.swf" },
              "manager": {
                "syncTheme": true,
                "ManagedAccounts": {
                  "AliceTester": { "DisplayName": "Alice", "Password": "win-alice-Sekrit-1", "Tags": ["farm", "daily"] },
                  "BobTester": { "DisplayName": "", "Password": "win-bob-Sekrit-2", "Tags": [] },
                  "GhostTester": { "DisplayName": "Ghost", "Password": "", "Tags": [] }
                },
                "AccountGroups": [ { "Name": "Farmers", "Accounts": ["AliceTester", "BobTester", "Nobody"] } ],
                "LastServer": "Twilly"
              }
            }
            """);

        ImportResult result = (await Accounts.ImportAsync(file))!;

        Assert.Equal(["AliceTester", "BobTester"], result.Imported);
        Assert.Equal(["GhostTester"], result.Skipped);
        Assert.Empty(result.Failures);
        Assert.Contains("win-alice-Sekrit-1", AppEngine.Keychain.Find(Skua.Control.Accounts.ServiceOf("alicetester"))!.Value.PasswordLine);
        Assert.Contains("win-bob-Sekrit-2", AppEngine.Keychain.Find(Skua.Control.Accounts.ServiceOf("bobtester"))!.Value.PasswordLine);

        // The file keeps everything but the passwords; the backup is the file as it was, owner-only.
        string scrubbed = File.ReadAllText(file);
        Assert.DoesNotContain("win-alice-Sekrit-1", scrubbed);
        Assert.DoesNotContain("win-bob-Sekrit-2", scrubbed);
        JsonNode manager = JsonNode.Parse(scrubbed)!["manager"]!;
        Assert.Equal(["farm", "daily"], manager["ManagedAccounts"]!["AliceTester"]!["Tags"]!.AsArray().Select(t => (string)t!));
        Assert.Equal("Twilly", (string)manager["LastServer"]!);
        Assert.NotNull(result.BackupPath);
        Assert.Contains("win-alice-Sekrit-1", File.ReadAllText(result.BackupPath!));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(result.BackupPath!));
        Assert.Equal(0, MatchesOutsideKeychain("win-alice-Sekrit-1", except: result.BackupPath));

        // The list shows them, with their tags and group, as the Windows Manager did.
        AccountManagerViewModel list = Accounts.Accounts;
        Assert.Equal(["Alice", "BobTester"], list.Accounts.Select(a => a.DisplayName));
        Assert.Equal(["daily", "farm"], list.AllTags.Select(t => t.Name));
        GroupItemViewModel farmers = Assert.Single(list.Groups);
        Assert.Equal(["AliceTester", "BobTester"], farmers.Accounts.Select(a => a.Username));

        // A second import finds no password to move and changes nothing in Keychain.
        ImportResult again = (await Accounts.ImportAsync(file))!;
        Assert.Empty(again.Imported);
        Assert.Equal(["AliceTester", "BobTester"], again.Updated);
        Assert.Null(again.BackupPath);

        await RemoveAllAsync();
    }

    [Fact]
    public async Task Importing_the_legacy_list_format_scrubs_it_too()
    {
        string file = Path.Combine(_dir, "ManagerSettings.json");
        File.WriteAllText(file, """{ "ManagedAccounts": ["Carol{=}CarolTester{=}legacy-Sekrit-3"], "syncTheme": false }""");
        ManagerAccounts store = new(_dir);

        ImportResult result = await WindowsAccountImport.ImportAsync(store, file, CancellationToken.None);

        Assert.Equal(["CarolTester"], result.Imported);
        Assert.Equal("""["Carol{=}CarolTester{=}"]""", JsonNode.Parse(File.ReadAllText(file))!["ManagedAccounts"]!.ToJsonString());
        Assert.Contains("legacy-Sekrit-3", AppEngine.Keychain.Find(Skua.Control.Accounts.ServiceOf("caroltester"))!.Value.PasswordLine);
        await store.SetAccountsAsync([], CancellationToken.None);
    }

    [Fact]
    public async Task Two_stores_on_one_folder_can_save_at_once()
    {
        ManagerAccounts first = new(_dir), second = new(_dir);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => (i % 2 == 0 ? first : second).LastServer = $"Server{i}")));

        Assert.StartsWith("Server", new ManagerAccounts(_dir).LastServer);
        Assert.Equal([ManagerAccounts.FileName], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [AvaloniaFact]
    public async Task A_store_that_saves_after_another_keeps_the_others_change()
    {
        ManagerAccounts first = new(_dir);
        await first.SetAccountsAsync([new AccountEntry("Alice Tester", "Alice", [])], CancellationToken.None);
        // As a second Manager on the folder does: it read the list, and saves the last server once its server list arrives.
        ManagerAccounts second = new(_dir);
        await first.SetAccountsAsync([], CancellationToken.None);

        second.LastServer = "Twilly";

        ManagerAccounts saved = new(_dir);
        Assert.Empty(saved.Accounts);
        Assert.Equal("Twilly", saved.LastServer);
    }

    [AvaloniaFact]
    public async Task An_Engine_save_after_an_import_of_its_own_settings_file_leaves_the_passwords_out()
    {
        // An Engine that has read the Windows list with its passwords, as one started on that file would have.
        string settings = Path.Combine(AppEngine.SkuaDir, "Skua.settings.json");
        ISettingsService engine = _app.Get<ISettingsService>();
        string sBG = engine.Get("sBG", "Generic2.swf");
        engine.Set("sBG", sBG);
        EditSettings(root => ((JsonObject)root["manager"]!)["ManagedAccounts"] = new JsonObject
        {
            ["CarolTester"] = new JsonObject { ["DisplayName"] = "Carol", ["Password"] = "live-carol-Sekrit-4", ["Tags"] = new JsonArray() },
        });
        engine.Set("sBG", sBG);
        Assert.Contains("live-carol-Sekrit-4", File.ReadAllText(settings));

        await Accounts.ImportAsync(settings);
        engine.Set("sBG", sBG);

        Assert.DoesNotContain("live-carol-Sekrit-4", File.ReadAllText(settings));
        EditSettings(root => ((JsonObject)root["manager"]!)["ManagedAccounts"] = new JsonObject());
        engine.Set("sBG", sBG);
        foreach (string backup in Directory.GetFiles(AppEngine.SkuaDir, "Skua.settings.json.*.bak"))
            File.Delete(backup);
        await RemoveAllAsync();
    }

    [AvaloniaFact]
    public async Task Launching_two_accounts_starts_an_app_each_that_plays_it_and_stopping_one_leaves_the_other_playing()
    {
        AccountManagerViewModel list = Accounts.Accounts;
        foreach ((string username, string password) in GameAccounts[..2])
            Add(list, username, password, "");
        foreach (AccountItemViewModel account in list.Accounts)
            account.UseCheck = true;

        await Accounts.LaunchSelectedCommand.ExecuteAsync(null);
        Assert.Equal("Launched alicetester, bobtester.", Accounts.Status);
        await WaitForLoginAsync("alicetester", "AliceTester");
        await WaitForLoginAsync("bobtester", "BobTester");

        RunningViewModel running = _services.GetRequiredService<RunningViewModel>();
        await running.RefreshAsync();
        RunningItemViewModel alice = running.Engines.Single(e => e.Name == "alicetester");
        RunningItemViewModel bob = running.Engines.Single(e => e.Name == "bobtester");
        Assert.All([alice, bob], e => Assert.Equal("App", e.Host));
        Assert.NotEqual(alice.Engine.Hello.Pid, bob.Engine.Hello.Pid);
        Assert.StartsWith("Playing on ", alice.Game);
        Assert.Equal("AliceTester", alice.Account);

        // No password reaches a command line, a log or a settings file.
        foreach (string name in new[] { "alicetester", "bobtester" })
            Assert.Equal(["--name", name, "--account", name], File.ReadAllLines(Path.Combine(AppEngine.SkuaDir, "engines", name + ".argv")));
        Assert.Equal(0, GameAccounts[..2].Sum(a => MatchesOutsideKeychain(a.Password)));

        // Launching a running account brings its app to the front instead.
        await Accounts.LaunchAsync([list.Accounts[1]], withScript: false);
        Assert.Equal("already running: bobtester.", Accounts.Status);
        await Ui.PumpUntilAsync(() => File.Exists(Path.Combine(AppEngine.SkuaDir, "engines", "bobtester.shown")), "bobtester to be shown");

        // Manager-launched apps are human logins; an agent's login there keeps ADR 0005's rule, with the app's account in the Active Account's place.
        using (EngineConnection bobEngine = (await EngineClient.TryConnectAsync(EngineEndpoint.Resolve("bobtester", AppEngine.SkuaDir), TestContext.Current.CancellationToken))!)
        {
            LoginResult agent = await bobEngine.AgentLoginAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(agent.IsTestAccount);
            Assert.Equal(AppEngine.Keychain.Username, agent.Username);
            LoginResult human = await bobEngine.LoginAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("BobTester", human.Username);
            // A login naming another of the Manager's accounts uses it there, and one naming none goes back to the app's own.
            LoginResult named = await bobEngine.LoginAsync(null, null, "alicetester", TestContext.Current.CancellationToken);
            Assert.Equal(("AliceTester", false), (named.Username, named.AlreadyLoggedIn));
            LoginResult own = await bobEngine.LoginAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(("BobTester", false), (own.Username, own.AlreadyLoggedIn));
        }

        await running.StopCommand.ExecuteAsync(alice);
        Assert.Equal("Stopped alicetester.", running.Status);
        Assert.DoesNotContain(running.Engines, e => e.Name == "alicetester");
        RunningEngine? still = await AppInstances.DescribeAsync(EngineEndpoint.Resolve("bobtester", AppEngine.SkuaDir), CancellationToken.None);
        Assert.Equal(GameState.Playing, still?.Status?.Game.State);

        await running.StopCommand.ExecuteAsync(running.Engines.Single(e => e.Name == "bobtester"));
        await RemoveAllAsync();
    }

    [AvaloniaFact]
    public async Task Tags_filter_the_list_and_a_group_launches_its_accounts_as_on_Windows()
    {
        AccountManagerViewModel list = Accounts.Accounts;
        foreach ((string username, string password) in GameAccounts)
            Add(list, username, password, "");
        list.Accounts[0].Tags.Add("farm");
        list.Accounts[2].Tags.Add("farm");
        list.Accounts[1].Tags.Add("daily");
        list.SaveAccounts();
        list.RefreshTagFilters();

        list.AllTags.Single(t => t.Name == "farm").IsSelected = true;
        Assert.Equal(["AliceTester", "CarolTester"], list.FilteredAccounts.Select(a => a.Username));
        list.AllTags.Single(t => t.Name == "daily").IsSelected = true;
        Assert.Equal(3, list.FilteredAccounts.Count);
        list.ClearTagFiltersCommand.Execute(null);
        list.AllTags.Single(t => t.Name == "daily").IsSelected = true;
        Assert.Equal(["BobTester"], list.FilteredAccounts.Select(a => a.Username));

        list.AddGroup("Solo");
        GroupItemViewModel solo = list.Groups.Single();
        list.AddAccountToGroup(list.Accounts[2], solo);
        // A Manager started afresh has the tags and the group.
        Assert.Equal(["CarolTester"], Reopen(m => m.Groups.Single().Accounts.Select(a => a.Username).ToList()));
        Assert.Equal(["daily", "farm"], Reopen(m => m.AllTags.Select(t => t.Name).ToList()));

        // The group's Launch, as its button sends it.
        solo.StartCommand.Execute(null);
        await WaitForLoginAsync("caroltester", "CarolTester");
        RunningViewModel running = _services.GetRequiredService<RunningViewModel>();
        await running.RefreshAsync();
        Assert.DoesNotContain(running.Engines, e => e.Name is "alicetester" or "bobtester");

        await running.StopCommand.ExecuteAsync(running.Engines.Single(e => e.Name == "caroltester"));
        await RemoveAllAsync();
    }

    [Fact]
    public void The_updates_view_compares_the_installed_build_with_the_checkout_and_downloads_nothing()
    {
        string repo = Path.Combine(_dir, "checkout");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Directory.Build.props"), "<Project><PropertyGroup><Version>9.8.7</Version></PropertyGroup></Project>");
        Git(repo, "init", "-q");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "one");
        Git(repo, "add", "-A");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "two");
        string commit = Git(repo, "rev-parse", "HEAD");
        string bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(bin);
        string build = $"9.8.7+{commit}";
        Directory.CreateDirectory(Path.Combine(_dir, "versions", build));
        File.CreateSymbolicLink(Path.Combine(bin, "skua"), Path.Combine(_dir, "versions", build, "skua"));

        try
        {
            Environment.SetEnvironmentVariable(UpdatesViewModel.CheckoutVariable, repo);
            Environment.SetEnvironmentVariable("SKUA_BIN_DIR", bin);
            UpdatesViewModel updates = new(new NoClipboard());
            Assert.True(updates.UpToDate);
            Assert.Equal(build, updates.Installed);
            Assert.Equal($"cd {repo} && ./install-macos.sh", updates.UpdateCommand);

            File.Delete(Path.Combine(bin, "skua"));
            File.CreateSymbolicLink(Path.Combine(bin, "skua"), Path.Combine(_dir, "versions", "9.8.6+0000000", "skua"));
            updates.Refresh();
            Assert.False(updates.UpToDate);
            Assert.Equal("9.8.6+0000000", updates.Installed);
            Assert.Contains("doesn't match", updates.Verdict);

            // With the app installed, skua is the CLI inside the build's Skua.app.
            File.Delete(Path.Combine(bin, "skua"));
            File.CreateSymbolicLink(Path.Combine(bin, "skua"), Path.Combine(_dir, "versions", build, "Skua.app", "Contents", "Helpers", "skua"));
            updates.Refresh();
            Assert.True(updates.UpToDate);
            Assert.Equal(build, updates.Installed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdatesViewModel.CheckoutVariable, null);
            Environment.SetEnvironmentVariable("SKUA_BIN_DIR", null);
        }
    }

    [AvaloniaFact]
    public async Task A_releases_updates_view_says_it_updates_itself_and_shows_no_command()
    {
        AppUpdates.Release release = new("/Applications/Skua.app", "1.2.3", "42", "https://example.invalid/appcast.xml", "key");
        UpdatesViewModel updates = new(new NoClipboard(), release);
        Assert.True(updates.IsRelease);
        Assert.Equal(("", true), (updates.UpdateCommand, updates.UpToDate));
        Assert.StartsWith("Skua 1.2.3 is a release: it checks for updates by itself", updates.Verdict);
        Assert.Contains(AppUpdates.CheckHeader, updates.Verdict);
        using PanelTests.BindingErrors errors = new();
        HostWindow window = new(updates);
        try
        {
            window.Show();
            await Ui.PumpUntilAsync(() => Ui.Find<UpdatesView>(window) is not null, "the Updates view");
            UpdatesView view = Ui.Find<UpdatesView>(window)!;
            Assert.False(view.FindControl<TextBox>("UpdateCommand")!.IsEffectivelyVisible);
            Assert.Equal(updates.Verdict, view.FindControl<TextBlock>("Verdict")!.Text);
            Assert.True(errors.Lines.Count == 0, string.Join("\n", errors.Lines));
        }
        finally
        {
            window.Close();
        }
        Assert.False(new UpdatesViewModel(new NoClipboard(), null).IsRelease);
    }

    [AvaloniaFact]
    public async Task Each_Manager_view_binds_to_its_view_model_with_no_binding_errors()
    {
        Add(Accounts.Accounts, "AliceTester", GameAccounts[0].Password, "");
        Accounts.Accounts.Accounts[0].Tags.Add("farm");
        Accounts.Accounts.RefreshTagFilters();
        Accounts.Accounts.AddGroup("Solo");
        Accounts.Accounts.AddAccountToGroup(Accounts.Accounts.Accounts[0], Accounts.Accounts.Groups[0]);
        ManagerMainViewModel main = _services.GetRequiredService<ManagerMainViewModel>();
        using PanelTests.BindingErrors errors = new();

        HostWindow window = new(main);
        window.Show();
        foreach (TabItemViewModel tab in main.Tabs)
        {
            main.SelectedTab = tab;
            await Ui.PumpUntilAsync(() => Ui.Find<UserControl>(window, c => c.DataContext == tab.Content) is not null, $"the {tab.Header} tab");
        }
        window.Close();

        Assert.Empty(errors.Lines);
        await RemoveAllAsync();
    }

    [AvaloniaFact]
    public void The_app_menu_bar_and_window_menu_open_the_Manager()
    {
        MainMenuViewModel viewModel = _app.Get<MainMenuViewModel>();
        Services.AvaloniaWindowService windows = _app.Get<Services.AvaloniaWindowService>();
        int opened = 0;

        MenuItem inWindow = MainMenus.InWindow(viewModel, windows, () => opened++).Items.OfType<MenuItem>().Single(i => (string)i.Header! == "Manager");
        NativeMenuItem native = MainMenus.Native(viewModel, windows, () => opened++).Items.OfType<NativeMenuItem>().Single(i => i.Header == "Manager");

        Assert.Equal(MainMenus.ManagerHeader, (string)((MenuItem)inWindow.Items.Single()!).Header!);
        Assert.Equal(MainMenus.ManagerHeader, native.Menu!.Items.OfType<NativeMenuItem>().Single().Header);
        ((MenuItem)inWindow.Items.Single()!).RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, opened);
    }

    [Fact]
    public void The_app_command_line_round_trips_and_never_takes_a_password()
    {
        AppArguments launch = new("alicetester", "alicetester", "Sir Ver", "/tmp/Farm.cs");
        Assert.Equal(launch, AppArguments.Parse(launch.ToArgs()));
        Assert.Equal(new AppArguments(Manager: true), AppArguments.Parse(["--manager"]));
        Assert.Equal(new AppArguments(), AppArguments.Parse(["-psn_0_1234"]));
        Assert.Throws<ControlException>(() => AppArguments.Parse(["--password", "x"]));
        Assert.Throws<ControlException>(() => AppArguments.Parse(["--server", "Twig"]));
        Assert.Throws<ControlException>(() => AppArguments.Parse(["--manager", "--name", "x"]));
    }

    [Fact]
    public void Started_without_a_name_the_app_serves_the_Engine_Name_it_last_served_but_not_a_Manager_launch()
    {
        string skuaDir = Path.Combine(_dir, "last-name");
        Assert.Equal(EngineName.Default, AppArguments.Parse([], LastEngineName.Read(skuaDir)).Name);

        LastEngineName.Remember(skuaDir, AppArguments.Parse(["--name", "alt"], LastEngineName.Read(skuaDir)));
        Assert.Equal("alt", AppArguments.Parse([], LastEngineName.Read(skuaDir)).Name);
        Assert.Equal(EngineName.Default, AppArguments.Parse(["--name", "default"], LastEngineName.Read(skuaDir)).Name);
        Assert.Equal(new AppArguments(Manager: true), AppArguments.Parse(["--manager"], LastEngineName.Read(skuaDir)));

        // The Skua Manager's launches name an account's own app.
        LastEngineName.Remember(skuaDir, AppArguments.Parse(["--name", "alicetester", "--account", "alicetester"], LastEngineName.Read(skuaDir)));
        Assert.Equal("alt", LastEngineName.Read(skuaDir));

        File.WriteAllText(Path.Combine(skuaDir, LastEngineName.FileName), "Not A Name\n");
        Assert.Equal(EngineName.Default, LastEngineName.Read(skuaDir));
    }

    [Fact]
    public async Task A_new_account_gets_a_name_of_its_own()
    {
        Assert.Equal("alice-tester", await ManagerAccounts.ChooseNameAsync("Alice Tester!", [], CancellationToken.None));
        Assert.Equal("alice-tester-2", await ManagerAccounts.ChooseNameAsync("alice_tester", ["alice-tester"], CancellationToken.None));
        Assert.Equal("test-2", await ManagerAccounts.ChooseNameAsync("Test", [], CancellationToken.None));
        Assert.Equal("default-2", await ManagerAccounts.ChooseNameAsync("Default", [], CancellationToken.None));
        Assert.Equal("a-very-long-na-2", await ManagerAccounts.ChooseNameAsync("A very long name indeed", ["a-very-long-name"], CancellationToken.None));
        // An item skua account add made for another username keeps its name; one for the same username is the same account.
        AppEngine.Keychain.Add(Skua.Control.Accounts.ServiceOf("dora"), "SomeoneElse", "x");
        try
        {
            Assert.Equal("dora-2", await ManagerAccounts.ChooseNameAsync("Dora", [], CancellationToken.None));
            AppEngine.Keychain.Add(Skua.Control.Accounts.ServiceOf("dora"), "dora", "x");
            Assert.Equal("dora", await ManagerAccounts.ChooseNameAsync("Dora", [], CancellationToken.None));
        }
        finally
        {
            await Keychain.DeleteAsync(Skua.Control.Accounts.ServiceOf("dora"), CancellationToken.None);
        }
    }

    /// <summary>Stops any app a failed test left running, then closes the Manager.</summary>
    public void Dispose()
    {
        AppInstances apps = new(AppEngine.SkuaDir);
        foreach ((string username, _) in GameAccounts)
        {
            EngineEndpoint endpoint = apps.Endpoint(username.ToLowerInvariant());
            if (AppInstances.DescribeAsync(endpoint, CancellationToken.None).GetAwaiter().GetResult() is { } left)
                apps.StopAsync(left, CancellationToken.None).GetAwaiter().GetResult();
        }
        Close(_services);
    }

    /// <summary>
    /// What a Manager started afresh on this test's list shows. It is closed at once: Core's view models listen on a messenger the whole process
    /// shares, where a second list would answer the first one's buttons.
    /// </summary>
    private T Reopen<T>(Func<AccountManagerViewModel, T> read)
    {
        ServiceProvider services = Build(_dir);
        try
        {
            return read(services.GetRequiredService<ManagerAccountsViewModel>().Accounts);
        }
        finally
        {
            Close(services);
        }
    }

    private static void Close(ServiceProvider services)
    {
        if (services.GetService<ManagerAccountsViewModel>() is { } accounts)
        {
            WeakReferenceMessenger.Default.UnregisterAll(accounts);
            WeakReferenceMessenger.Default.UnregisterAll(accounts.Accounts);
            StrongReferenceMessenger.Default.UnregisterAll(accounts.Accounts);
        }
        services.Dispose();
    }

    private static void Add(AccountManagerViewModel list, string username, string password, string displayName)
    {
        list.UsernameInput = username;
        list.DisplayNameInput = displayName;
        list.PasswordInput = password;
        list.AddAccountCommand.Execute(null);
    }

    private async Task RemoveAllAsync() => await new ManagerAccounts(_dir).SetAccountsAsync([], CancellationToken.None);

    private static async Task WaitForLoginAsync(string name, string username)
    {
        string file = Path.Combine(AppEngine.SkuaDir, "engines", name + ".login");
        await Ui.PumpUntilAsync(() => File.Exists(file) && File.ReadAllText(file).Length > 0, $"{name}'s login", TimeSpan.FromSeconds(60));
        Assert.StartsWith($"ok {username} ", File.ReadAllText(file));
    }

    /// <summary>
    /// How many times <paramref name="secret"/> appears in the sandbox's files outside the fake Keychain's items: settings, the Manager's list,
    /// the Engines' logs and the apps' command lines.
    /// </summary>
    private static int MatchesOutsideKeychain(string secret, string? except = null) =>
        Directory.EnumerateFiles(AppEngine.SkuaDir, "*", SearchOption.AllDirectories)
            // The fake Keychain's items, and the fake game's own accounts, which stand in for AQW's servers.
            .Where(f => !f.Contains("fake-security-items", StringComparison.Ordinal) && !f.EndsWith(".scenario", StringComparison.Ordinal) && f != except)
            .Sum(f =>
            {
                try
                {
                    string text = File.ReadAllText(f);
                    int count = 0;
                    for (int i = text.IndexOf(secret, StringComparison.Ordinal); i >= 0; i = text.IndexOf(secret, i + 1, StringComparison.Ordinal))
                        count++;
                    return count;
                }
                catch (IOException)
                {
                    return 0;
                }
            });

    private static void EditSettings(Action<JsonNode> change)
    {
        string path = Path.Combine(AppEngine.SkuaDir, "Skua.settings.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        root["manager"] ??= new JsonObject();
        change(root);
        File.WriteAllText(path, root.ToJsonString());
    }

    private static string Git(string repo, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git") { RedirectStandardOutput = true, UseShellExecute = false };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repo);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using Process git = Process.Start(startInfo)!;
        string output = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
        return output;
    }

    /// <summary>Answers every question yes, and keeps what it was shown.</summary>
    private sealed class ScriptedDialogs : IDialogService
    {
        public List<string> Shown { get; } = [];

        public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class => true;

        public bool? ShowDialog<TViewModel>(TViewModel viewModel, string title) where TViewModel : class => true;

        public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class => true;

        public void ShowMessageBox(string message, string caption) => Shown.Add(message);

        public bool? ShowMessageBox(string message, string caption, bool yesAndNo)
        {
            Shown.Add(message);
            return true;
        }

        public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
        {
            Shown.Add(message);
            return new DialogResult(buttons.LastOrDefault() ?? "OK", Math.Max(0, buttons.Length - 1));
        }
    }

    private sealed class NoClipboard : IClipboardService
    {
        public void SetText(string text)
        {
        }

        public void SetData(string format, object data)
        {
        }

        public object GetData(string format) => "";

        public string GetText() => "";
    }
}
