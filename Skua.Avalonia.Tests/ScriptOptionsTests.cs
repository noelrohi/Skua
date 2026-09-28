using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.ViewModels;
using Skua.Engine;
using Skua.Engine.Tests;

namespace Skua.Avalonia.Tests;

/// <summary>
/// A Script's options edited in the app and read by the Engine's <c>script_options</c> and the next run, both ways, and CoreBots' options,
/// over the app's Engine with the fake Game Host.
/// </summary>
/// <remarks>
/// The editor is the app's own dialog, opened as Core opens it, from a thread that waits for it to close. The test of an agent storing an
/// option while the editor is open stands in for the dialog with <see cref="EditingDialogs"/>, to store it at a known moment.
/// </remarks>
[Collection(nameof(GameViewTests))]
public sealed class ScriptOptionsTests(AppEngine app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_app_and_its_Engine_use_the_Avalonia_options_container()
    {
        Assert.IsType<AvaloniaScriptOptionContainer>(app.Get<IScriptOptionContainer>());
    }

    [AvaloniaFact]
    public async Task A_Script_with_options_opens_the_editor_and_what_is_saved_there_is_what_skua_script_options_shows_and_the_next_run_uses()
    {
        // Core opens the options window at every start of a Script with options; this one waits for the editor's value.
        (string path, string storage) = WriteFarm(dontPreconfigure: false, before: """
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (bot.Config.Get<int>("count") != 9 && clock.ElapsedMilliseconds < 30000 && !bot.ShouldExit)
                Thread.Sleep(50);
            """);
        using EditorWindows editors = new(app);
        using EngineConnection connection = await ConnectAsync();

        ScriptStartResult first = await connection.ScriptStartAsync(path, cancellationToken: Ct);
        DialogWindow editor = await editors.NextAsync();
        TextBox count = (TextBox)await RowAsync(editor, "count");
        Assert.Equal("5", count.Text);
        count.Text = "9";
        ((ComboBox)await RowAsync(editor, "mode")).SelectedItem = "Slow";
        editor.Close();

        // The running Script sees them as the editor closes, as on Windows.
        Assert.Equal("count=9 mode=Slow flag=False once=1 name=nobody", await LoggedAsync(connection, first.Run));
        await connection.ScriptWaitAsync(60, Ct);
        (int exit, string output) = await ScriptsPanelTests.CliAsync(["script", "options", path, "--json"]);
        Assert.True(exit == 0, output);
        using (JsonDocument options = JsonDocument.Parse(output))
        {
            Assert.Equal(storage, options.RootElement.GetProperty("storage").GetString());
            Assert.Equal(["9", "Slow", "False", "1", "nobody"], options.RootElement.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString()));
        }

        ScriptStartResult second = await connection.ScriptStartAsync(path, cancellationToken: Ct);
        Assert.Equal("count=9 mode=Slow flag=False once=1 name=nobody", await LoggedAsync(connection, second.Run));
        (await editors.NextAsync()).Close();
        await connection.ScriptWaitAsync(60, Ct);
    }

    [AvaloniaFact]
    public async Task Options_set_with_the_CLI_show_in_the_editor_the_next_time_it_opens()
    {
        (string path, _) = WriteFarm();
        (int exit, string output) = await ScriptsPanelTests.CliAsync(["script", "start", path, "--no-update", "--option", "count=3", "--option", "Extra:name=Twilly", "--json"]);
        Assert.True(exit == 0, output);
        await WaitAsync();
        using EditorWindows editors = new(app);

        // As the Scripts panel's Edit Script Options loads and opens them, from a thread that may wait on the editor.
        IScriptManager manager = app.Get<EngineScripts>().ScriptManager;
        manager.LoadScriptConfig(manager.Compile(File.ReadAllText(Path.Combine(ClientFileSources.SkuaScriptsDIR, path))));
        IScriptOptionContainer config = Assert.IsType<AvaloniaScriptOptionContainer>(manager.Config);
        Task configured = Task.Run(config.Configure, Ct);
        DialogWindow editor = await editors.NextAsync();
        Ui.Find<Expander>(editor, e => e.Header as string == "Extra")!.IsExpanded = true;

        Assert.Equal("3", ((TextBox)await RowAsync(editor, "count")).Text);
        Assert.Equal("Twilly", ((TextBox)await RowAsync(editor, "name")).Text);
        editor.Close();
        await configured.WaitAsync(Ui.Timeout, Ct);
    }

    [AvaloniaFact]
    public async Task Closing_the_editor_saves_only_what_changed_so_a_value_an_agent_stored_while_it_was_open_survives()
    {
        (string path, _) = WriteFarm();
        using EngineConnection connection = await ConnectAsync();
        EditingDialogs dialogs = new(editor =>
        {
            // An agent starts a run with an option of its own while the editor is open.
            connection.ScriptStartAsync(path, new Dictionary<string, string> { ["flag"] = "true" }, cancellationToken: Ct).GetAwaiter().GetResult();
            connection.ScriptWaitAsync(60, Ct).GetAwaiter().GetResult();
            Edit(editor, "count", "12");
            Edit(editor, "once", "4");
        });

        await Task.Run(() => EditorFor(path, dialogs).Configure(), Ct);

        ScriptOptionsResult options = await connection.ScriptOptionsAsync(path, Ct);
        // A transient option isn't stored: the next run starts with its default.
        Assert.Equal(["12", "Fast Farm", "True", "1", "nobody"], options.Options.Select(o => o.Value));
    }

    [AvaloniaFact]
    public async Task CoreBots_options_load_from_the_players_file_and_edits_persist_there()
    {
        using EngineConnection connection = await ConnectAsync();
        await connection.LoginAsync("Galanoth", cancellationToken: Ct);
        try
        {
            string username = app.Get<IScriptPlayer>().Username;
            Assert.False(string.IsNullOrEmpty(username));
            // Where CoreBots reads them: CoreBots.cs's CBO_Path.
            string file = Path.Combine(ClientFileSources.SkuaOptionsDIR, $"CBO_Storage({username}).txt");
            File.WriteAllLines(file, ["HuntDelayNr: 250", "doRepBoost: True"]);
            // The fake Script Source serves an empty skill set file, which the Loadout tab's class modes can't read; a real one is JSON.
            string skills = Path.Combine(ClientFileSources.SkuaDIR, "UserAdvancedSkills.json");
            if (new FileInfo(skills) is { Exists: false } or { Length: 0 })
                File.WriteAllText(skills, "{}");
            MainMenuViewModel menu = app.Get<MainMenuViewModel>();
            AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
            MenuItem options = MainMenus.InWindow(menu, windows).Items.OfType<MenuItem>().Single(i => (string)i.Header! == "Options");
            MenuItem coreBots = options.Items.OfType<MenuItem>().Single(i => (string)i.Header! == "CoreBots");
            Assert.True(coreBots.IsEnabled, "Options → CoreBots is disabled");

            coreBots.Command!.Execute(null);
            (Window window, CoreBotsView view) = await ShownAsync<CoreBotsView>("CoreBots");
            CoreBotsViewModel model = (CoreBotsViewModel)view.DataContext!;
            await Ui.PumpUntilAsync(() => view.FindControl<TextBlock>("CurrentPlayer")!.Text == username, "the player's name");
            model.SelectedTab = model.CoreBotsTabs.Single(t => t.Header == "Options");
            CBOptionsView general = await FoundAsync<CBOptionsView>(window);
            await Ui.PumpUntilAsync(() => OptionText(general, "Hunt Delay") is { Text: "250" }, "Hunt Delay loaded from the file");
            OptionText(general, "Action Delay")!.Text = "900";
            Ui.Find<CheckBox>(general, c => c.Content as string == "Anti Lag")!.IsChecked = false;
            model.SelectedTab = model.CoreBotsTabs.Single(t => t.Header == "Other");
            CBOOtherOptionsView other = await FoundAsync<CBOOtherOptionsView>(window);
            await Ui.PumpUntilAsync(() => Ui.Find<Expander>(other, e => e.Header as string == "Boosters") is not null, "the Boosters category");
            Expander boosters = Ui.Find<Expander>(other, e => e.Header as string == "Boosters")!;
            boosters.IsExpanded = true;
            await Ui.PumpUntilAsync(() => BoosterCheck(boosters, "Use Reputation Boosts when farming REP") is not null, "the Boosters options");
            Assert.True(BoosterCheck(boosters, "Use Reputation Boosts when farming REP")!.IsChecked, "doRepBoost wasn't loaded from the file");
            BoosterCheck(boosters, "Use Gold Boosts when farming gold")!.IsChecked = true;

            Ui.Click(view.FindControl<Button>("Save")!);

            string[] saved = File.ReadAllLines(file);
            Assert.Superset(new HashSet<string> { "ActionDelayNr: 900", "HuntDelayNr: 250", "AntiLag: False", "doGoldBoost: True", "doRepBoost: True" }, saved.ToHashSet());

            // Closing the window saves too, as on Windows.
            BoosterCheck(boosters, "Use Experience Boosts when farming EXP")!.IsChecked = true;
            window.Close();
            await Ui.PumpUntilAsync(() => windows.OpenWindow("CoreBots") is null, "the closed window");
            Assert.Contains("doExpBoost: True", File.ReadAllLines(file));
        }
        finally
        {
            await connection.LogoutAsync(Ct);
        }
    }

    /// <summary>A Script with options in the main list, a transient one and a group, which logs the values it runs with; its storage is its own.</summary>
    /// <param name="dontPreconfigure">Whether Core skips opening the options window at the Script's start.</param>
    /// <param name="before">What the Script does before it logs its values.</param>
    private static (string Path, string Storage) WriteFarm(bool dontPreconfigure = true, string before = "")
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        string storage = $"AppFarm{id}";
        string path = $"Tests/OptionsFarm{id}.cs";
        AppEngine.WriteScript(path, $$"""
            using System.Collections.Generic;
            using System.Threading;
            using Skua.Core.Interfaces;
            using Skua.Core.Options;

            public class TestScript
            {
                public enum Mode { Fast_Farm, Slow }

                public string OptionsStorage = "{{storage}}";

                public bool DontPreconfigure = {{(dontPreconfigure ? "true" : "false")}};

                public List<IOption> Options = new()
                {
                    new Option<int>("count", "Count", "How many to farm.", 5),
                    new Option<Mode>("mode", "Mode", "", Mode.Fast_Farm),
                    new Option<bool>("flag", "Flag", "A switch.", false),
                    new Option<int>("once", "Once", "Resets every start.", 1, transient: true),
                };

                public string[] MultiOptions = { "Extra" };

                public List<IOption> Extra = new()
                {
                    new Option<string>("name", "Name", "Who.", "nobody"),
                };

                public void ScriptMain(IScriptInterface bot)
                {
                    {{before}}
                    bot.Log($"count={bot.Config.Get<int>("count")} mode={bot.Config.Get<Mode>("mode")} flag={bot.Config.Get<bool>("flag")} once={bot.Config.Get<int>("once")} name={bot.Config.Get<string>("Extra", "name")}");
                }
            }
            """);
        return (path, storage);
    }

    /// <summary>
    /// The container the app makes for the Script, as the Scripts panel's Edit Script Options loads it, with the stand-in dialogs: the app's
    /// Engine compiles it and loads its options, and the editor gets the same options and storage.
    /// </summary>
    /// <returns>The editor as Core sees it, so <c>Configure</c> is the call Core makes.</returns>
    private IScriptOptionContainer EditorFor(string path, IDialogService dialogs)
    {
        IScriptManager manager = app.Get<EngineScripts>().ScriptManager;
        manager.LoadScriptConfig(manager.Compile(File.ReadAllText(Path.Combine(ClientFileSources.SkuaScriptsDIR, path))));
        AvaloniaScriptOptionContainer loaded = Assert.IsType<AvaloniaScriptOptionContainer>(manager.Config);
        AvaloniaScriptOptionContainer editor = new(dialogs) { Storage = loaded.Storage };
        editor.Options.AddRange(loaded.Options);
        foreach ((string group, List<IOption> options) in loaded.MultipleOptions)
            editor.MultipleOptions.Add(group, options);
        editor.SetDefaults();
        editor.Load();
        return editor;
    }

    /// <summary>Sets an option in the editor as its row would: text as the text typed, an enum by its name, a bool as the check box's.</summary>
    private static void Edit(OptionContainerViewModel editor, string name, object value)
    {
        OptionContainerItemViewModel item = editor.Options.Single(o => o.Option.Name == name);
        if (item.Type.IsEnum)
            item.SelectedValue = (string)value;
        else
            item.Value = value;
    }

    /// <summary>The values the run logged.</summary>
    private static async Task<string> LoggedAsync(EngineConnection connection, int run) =>
        (await connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Run == run && e.Text!.StartsWith("count=", StringComparison.Ordinal)))[0].Text!;

    /// <summary>The editor's control for an option, once its row shows.</summary>
    private static async Task<global::Avalonia.Controls.Control> RowAsync(DialogWindow editor, string name)
    {
        await Ui.PumpUntilAsync(() => Row(editor, name) is not null, $"the editor's '{name}' row");
        return Row(editor, name)!;
    }

    private static global::Avalonia.Controls.Control? Row(DialogWindow editor, string name) =>
        Ui.Find<Grid>(editor, g => g.Tag is OptionContainerItemViewModel item && item.Option.Name == name)?.Children[1];

    private static async Task WaitAsync()
    {
        using EngineConnection connection = await ConnectAsync();
        await connection.ScriptWaitAsync(60, Ct);
    }

    private static TextBox? OptionText(CBOptionsView view, string name) =>
        Ui.Find<DockPanel>(view, d => d.Children.OfType<TextBlock>().Any(t => t.Text == name))?.Children.OfType<TextBox>().Single();

    private static CheckBox? BoosterCheck(Expander boosters, string name) =>
        boosters.GetLogicalDescendants().OfType<Grid>()
            .FirstOrDefault(g => g.Children.OfType<TextBlock>().Any(t => t.Text == name))?.Children.OfType<CheckBox>().Single();

    private async Task<(Window, T)> ShownAsync<T>(string key) where T : global::Avalonia.Visual
    {
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        await Ui.PumpUntilAsync(() => windows.OpenWindow(key) is not null, $"the {key} window");
        Window window = windows.OpenWindow(key)!;
        return (window, await FoundAsync<T>(window));
    }

    private static async Task<T> FoundAsync<T>(Window window) where T : global::Avalonia.Visual
    {
        await Ui.PumpUntilAsync(() => Ui.Find<T>(window) is not null, $"a {typeof(T).Name}");
        return Ui.Find<T>(window)!;
    }

    private static async Task<EngineConnection> ConnectAsync() =>
        await EngineClient.TryConnectAsync(EngineEndpoint.FromEnvironment(), Ct) ?? throw new InvalidOperationException("The app's Engine didn't answer.");

    /// <summary>The options editors the app's dialog service opens while it lives, in turn.</summary>
    private sealed class EditorWindows : IDisposable
    {
        private readonly AvaloniaDialogService _dialogs;
        private readonly Action<Window>? _previous;
        private readonly List<DialogWindow> _opened = [];
        private int _next;

        public EditorWindows(AppEngine app)
        {
            _dialogs = app.Get<AvaloniaDialogService>();
            _previous = _dialogs.WindowCreated;
            _dialogs.WindowCreated = window =>
            {
                _previous?.Invoke(window);
                if (window is DialogWindow dialog)
                    _opened.Add(dialog);
            };
        }

        /// <summary>The next editor to open, once it shows.</summary>
        public async Task<DialogWindow> NextAsync()
        {
            await Ui.PumpUntilAsync(() => _opened.Count > _next && _opened[_next] is { IsVisible: true, DataContext: OptionContainerViewModel }, "the options editor");
            return _opened[_next++];
        }

        public void Dispose() => _dialogs.WindowCreated = _previous;
    }

    /// <summary>Stands in for the app's dialogs around the options editor: records the values it shows, makes the edits, then closes it.</summary>
    private sealed class EditingDialogs(Action<OptionContainerViewModel> edit) : IDialogService
    {
        public Dictionary<string, string> Shown { get; } = [];

        public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class
        {
            OptionContainerViewModel editor = Assert.IsType<OptionContainerViewModel>(viewModel);
            foreach (OptionContainerItemViewModel item in editor.Options)
                Shown[item.Option.Name] = item.Type.IsEnum ? item.SelectedValue! : item.Value.ToString()!;
            edit(editor);
            callback(viewModel);
            return true;
        }

        public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class => throw new NotSupportedException();

        public bool? ShowDialog<TViewModel>(TViewModel viewModel, string title) where TViewModel : class => throw new NotSupportedException();

        public void ShowMessageBox(string message, string caption) => throw new NotSupportedException();

        public bool? ShowMessageBox(string message, string caption, bool yesAndNo) => throw new NotSupportedException();

        public DialogResult ShowMessageBox(string message, string caption, params string[] buttons) => throw new NotSupportedException();
    }
}
