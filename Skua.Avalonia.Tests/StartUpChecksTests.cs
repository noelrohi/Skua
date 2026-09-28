using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Skua.Avalonia.Services;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Skills;
using Skua.Engine;
using Skua.Engine.Tests;
using Skua.MacOS.Services;

namespace Skua.Avalonia.Tests;

/// <summary>
/// The Mac App's start-up checks against the fake Script Source: the Scripts, the AdvanceSkill sets, the junk items and the quest data, each
/// as its Application Options say, with their Questions on the sheet and their Notices; and Change Logs on the first start.
/// </summary>
/// <remarks>
/// In the Game View tests' collection, as they share the one Engine. Each test sets the settings it relies on and puts them, and the files it
/// changes, back in a finally, where it also closes its windows.
/// </remarks>
[Collection(nameof(GameViewTests))]
public sealed class StartUpChecksTests(AppEngine app)
{
    private const string Owner = "noelrohi";
    private const string Repo = "Scripts";
    private const string Branch = "Skua";

    private static readonly string[] CheckSettings =
    [
        "CheckBotScriptsUpdates", "AutoUpdateBotScripts", "CheckAdvanceSkillSetsUpdates", "AutoUpdateAdvanceSkillSetsUpdates",
        "CheckJunkItemsUpdates", "AutoUpdateJunkItems", "ChangeLogActivated",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private StartUpChecks Checks => app.Get<StartUpChecks>();

    private ScriptDialogsViewModel Dialogs => app.Get<ScriptDialogsViewModel>();

    [AvaloniaFact]
    public async Task An_outdated_Script_asks_first_and_Yes_updates_through_scripts_update_while_No_leaves_it()
    {
        string path = $"Tests/StartUp{Guid.NewGuid():N}.cs";
        using Settings settings = new(app, ("CheckBotScriptsUpdates", true), ("AutoUpdateBotScripts", false));
        (Window window, QuestionSheet sheet) = ShowWindow();
        try
        {
            await SyncedWithAsync(new FakeScript(path, "// v1", "Start-up v1"));
            AppEngine.GitHub.Commit(Owner, Repo, Branch, new FakeScript(path, "// v2, a little longer", "Start-up v2"));
            int requests = AppEngine.GitHub.Requests.Count;

            Task no = Task.Run(Checks.CheckScriptsAsync, Ct);
            await AnswerAsync(sheet, "Script Update", "Would you like to update your scripts?", "No");
            await Ui.PumpUntilAsync(() => no.IsCompleted, "the check to finish after No");
            await no;
            Assert.Equal("// v1", ReadScript(path));
            Assert.Empty(ScriptDownloadsSince(requests));

            using DialogLog dialogs = new(app);
            Task yes = Task.Run(Checks.CheckScriptsAsync, Ct);
            await AnswerAsync(sheet, "Script Update", "Would you like to update your scripts?", "Yes");
            await Ui.PumpUntilAsync(() => yes.IsCompleted, "the check to finish after Yes");
            await yes;
            Assert.Equal("// v2, a little longer", ReadScript(path));
            Assert.Equal([path], ScriptDownloadsSince(requests));
            Notice notice = Assert.Single(dialogs.Notices);
            Assert.Equal("Script Update", notice.Caption);
            Assert.Equal("Downloaded 1 script(s).\nYou can disable auto script updates in Options > Application.", notice.Text);
            // The app's own Notice, from no Script.
            Assert.Null(notice.Script);

            // scripts_update recorded it in the Script history, as any update does.
            using EngineConnection connection = await ConnectAsync();
            ScriptsNewResult news = await connection.ScriptsNewAsync(cancellationToken: Ct);
            Assert.Contains(news.Scripts, s => s.Path == path && s.Commit == AppEngine.GitHub.Head(Owner, Repo, Branch));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task With_auto_update_on_the_Scripts_update_without_a_Question_and_with_the_check_off_nothing_is_requested()
    {
        string path = $"Tests/StartUp{Guid.NewGuid():N}.cs";
        using Settings settings = new(app, ("CheckBotScriptsUpdates", true), ("AutoUpdateBotScripts", true));
        await SyncedWithAsync(new FakeScript(path, "// v1"));
        AppEngine.GitHub.Commit(Owner, Repo, Branch, new FakeScript(path, "// v2, auto"));
        using (DialogLog dialogs = new(app))
        {
            await Task.Run(Checks.CheckScriptsAsync, Ct);
            Assert.Equal(0, dialogs.Questions);
            Notice notice = Assert.Single(dialogs.Notices);
            Assert.Equal("Script Update", notice.Caption);
            Assert.StartsWith("Downloaded 1 script(s).", notice.Text);
        }

        Assert.Equal("// v2, auto", ReadScript(path));

        AppEngine.GitHub.Commit(Owner, Repo, Branch, new FakeScript(path, "// v3, unchecked"));
        app.Get<ISettingsService>().Set("CheckBotScriptsUpdates", false);
        int requests = AppEngine.GitHub.Requests.Count;
        await Task.Run(Checks.CheckScriptsAsync, Ct);
        Assert.Empty(AppEngine.GitHub.Requests.Skip(requests));
        Assert.Equal("// v2, auto", ReadScript(path));
    }

    [AvaloniaFact]
    public async Task While_a_Script_runs_the_Scripts_check_is_skipped_with_a_debug_line()
    {
        string path = $"Tests/StartUp{Guid.NewGuid():N}.cs";
        using Settings settings = new(app, ("CheckBotScriptsUpdates", true), ("AutoUpdateBotScripts", true));
        await SyncedWithAsync(new FakeScript(path, "// v1"));
        AppEngine.GitHub.Commit(Owner, Repo, Branch, new FakeScript(path, "// v2, while running"));
        AppEngine.WriteScript("Tests/StartUpRunning.cs", """
            using System.Threading;
            using Skua.Core.Interfaces;

            public class TestScript
            {
                public void ScriptMain(IScriptInterface bot)
                {
                    while (!bot.ShouldExit)
                        Thread.Sleep(50);
                }
            }
            """);
        using EngineConnection connection = await ConnectAsync();
        await connection.ScriptStartAsync("Tests/StartUpRunning.cs", cancellationToken: Ct);
        try
        {
            await Ui.PumpUntilAsync(() => app.Get<EngineScripts>().ScriptManager.ScriptRunning, "the Script to run");
            int requests = AppEngine.GitHub.Requests.Count;

            await Task.Run(Checks.CheckScriptsAsync, Ct);

            Assert.Empty(AppEngine.GitHub.Requests.Skip(requests));
            Assert.Equal("// v1", ReadScript(path));
            Assert.Contains(app.Get<ILogService>().GetLogs(LogType.Debug), l => l.Contains("skipped the Scripts check, since a Script is running", StringComparison.Ordinal));
        }
        finally
        {
            await connection.ScriptStopAsync(Ct);
        }
    }

    [AvaloniaFact]
    public async Task The_skill_sets_follow_the_same_rules_and_are_synced_after_an_update()
    {
        IAdvancedSkillContainer skills = app.Get<IAdvancedSkillContainer>();
        string userFile = Path.Combine(ClientFileSources.SkuaDIR, "UserAdvancedSkills.json");
        using SkillLoads loads = new();
        // A save another test made reloads the sets on another thread; the files are kept once it is done.
        await loads.SettledAsync();
        using Settings settings = new(app, ("CheckAdvanceSkillSetsUpdates", true), ("AutoUpdateAdvanceSkillSetsUpdates", false));
        KeptFiles kept = new(ClientFileSources.SkuaAdvancedSkillsFile, userFile);
        try
        {
            await CheckFileAsync(
                "Skills/AdvancedSkills.json", ClientFileSources.SkuaAdvancedSkillsFile, "CheckAdvanceSkillSetsUpdates", "AutoUpdateAdvanceSkillSetsUpdates",
                Checks.CheckSkillSetsAsync,
                "AdvanceSkill Sets Update", "Would you like to update your AdvanceSkill Sets?", "AdvanceSkill Sets has been updated.",
                "AdvanceSkill Sets updates",
                marker => $$"""{"{{marker}}": {"Base": {"skillUseMode": "UseIfAvailable", "skillTimeout": 100, "skills": []} } }""",
                // The sync runs on another thread and has finished once Core says the sets changed.
                loads.Finished);
        }
        finally
        {
            // No sync may still be reloading the sets when they are put back, in the file and in Core's list, as the next test finds them.
            await loads.SettledAsync();
            kept.Restore();
            skills.LoadSkills();
        }
    }

    [AvaloniaFact]
    public async Task The_junk_items_follow_the_same_rules_and_the_junk_list_is_reloaded_after_an_update()
    {
        IJunkService junk = app.Get<IJunkService>();
        using Settings settings = new(app, ("CheckJunkItemsUpdates", true), ("AutoUpdateJunkItems", false));
        KeptFiles kept = new(ClientFileSources.SkuaJunkItemsFile);
        try
        {
            await CheckFileAsync(
                "JunkItems.json", ClientFileSources.SkuaJunkItemsFile, "CheckJunkItemsUpdates", "AutoUpdateJunkItems", Checks.CheckJunkItemsAsync,
                "Junk Items Update", "Would you like to update your Junk Items list?", "Junk Items list has been updated.", "Junk Items updates",
                marker => $$"""[{"ID":{{Math.Abs(marker.GetHashCode()) % 100000 + 1}},"Name":"{{marker}}","Category":"Item","Meta":""}]""",
                marker => junk.JunkItems.Any(j => j.Name == marker));
        }
        finally
        {
            kept.Restore();
            junk.Load();
        }
    }

    [AvaloniaFact]
    public async Task The_quest_data_is_refreshed_at_every_start_and_the_checks_that_are_off_request_nothing()
    {
        string questData = $$"""[{"ID":1,"Name":"StartUp {{Guid.NewGuid():N}}"}]""";
        using Settings settings = new(app, ("CheckBotScriptsUpdates", false), ("CheckAdvanceSkillSetsUpdates", false), ("CheckJunkItemsUpdates", false),
            ("ChangeLogActivated", true));
        KeptFiles kept = new(ClientFileSources.SkuaQuestsFile);
        AppEngine.GitHub.Put(Owner, Repo, Branch, "QuestData.json", questData);
        try
        {
            int requests = AppEngine.GitHub.Requests.Count;

            await Checks.RunAsync();

            Assert.Equal(questData, File.ReadAllText(ClientFileSources.SkuaQuestsFile));
            Assert.Equal([$"/raw/{Owner}/{Repo}/refs/heads/{Branch}/QuestData.json"], AppEngine.GitHub.Requests.Skip(requests));
            Assert.Null(app.Get<AvaloniaWindowService>().OpenWindow(AppMenu.ChangeLogsKey));
        }
        finally
        {
            AppEngine.GitHub.Put(Owner, Repo, Branch, "QuestData.json", null);
            kept.Restore();
        }
    }

    [AvaloniaFact]
    public async Task Change_Logs_opens_on_the_first_start_and_not_on_later_ones()
    {
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        using Settings settings = new(app, ("ChangeLogActivated", false));
        try
        {
            windows.OpenWindow(AppMenu.ChangeLogsKey)?.Close();
            await Ui.PumpUntilAsync(() => windows.OpenWindow(AppMenu.ChangeLogsKey) is null, "any earlier Change Logs to close");

            Checks.ShowChangeLogsOnFirstStart();
            await Ui.PumpUntilAsync(() => windows.OpenWindow(AppMenu.ChangeLogsKey) is not null, "Change Logs");
            Assert.Equal("Change Logs", windows.OpenWindow(AppMenu.ChangeLogsKey)!.Title);
            // Saved where the next start reads it: the Windows Manager's setting.
            using (JsonDocument file = JsonDocument.Parse(File.ReadAllText(ClientFileSources.SkuaSettingsDIR)))
                Assert.True(file.RootElement.GetProperty("manager").GetProperty("ChangeLogActivated").GetBoolean());
            windows.OpenWindow(AppMenu.ChangeLogsKey)!.Close();
            await Ui.PumpUntilAsync(() => windows.OpenWindow(AppMenu.ChangeLogsKey) is null, "Change Logs to close");

            Checks.ShowChangeLogsOnFirstStart();
            await Ui.PumpUntilAsync(() => true, "a later start");
            Assert.Null(windows.OpenWindow(AppMenu.ChangeLogsKey));
        }
        finally
        {
            windows.OpenWindow(AppMenu.ChangeLogsKey)?.Close();
        }
    }

    /// <summary>
    /// A file's check: off, it requests nothing; with auto update off, No leaves the file and Yes downloads it, makes the app use it and
    /// says so; with auto update on, it downloads without a Question.
    /// </summary>
    /// <param name="content">The Script Source's file, marked so the test can tell it apart.</param>
    /// <param name="inUse">Whether the app uses the file with that mark.</param>
    private async Task CheckFileAsync(string sourcePath, string localFile, string checkKey, string autoKey, Func<Task> check, string caption, string question,
        string updated, string what, Func<string, string> content, Func<string, bool> inUse)
    {
        ISettingsService settingsService = app.Get<ISettingsService>();
        (Window window, QuestionSheet sheet) = ShowWindow();
        try
        {
            string before = File.Exists(localFile) ? File.ReadAllText(localFile) : "";
            string first = "StartUp" + Guid.NewGuid().ToString("N")[..8];
            AppEngine.GitHub.Put(Owner, Repo, Branch, sourcePath, content(first));
            string sourceUrl = $"/raw/{Owner}/{Repo}/refs/heads/{Branch}/{sourcePath}";

            settingsService.Set(checkKey, false);
            int requests = AppEngine.GitHub.Requests.Count;
            await Task.Run(check, Ct);
            Assert.DoesNotContain(sourceUrl, AppEngine.GitHub.Requests.Skip(requests));
            settingsService.Set(checkKey, true);

            Task no = Task.Run(check, Ct);
            await AnswerAsync(sheet, caption, question, "No");
            await Ui.PumpUntilAsync(() => no.IsCompleted, "the check to finish after No");
            await no;
            Assert.Equal(before, File.Exists(localFile) ? File.ReadAllText(localFile) : "");
            Assert.False(inUse(first));

            using (DialogLog dialogs = new(app))
            {
                Task yes = Task.Run(check, Ct);
                await AnswerAsync(sheet, caption, question, "Yes");
                await Ui.PumpUntilAsync(() => yes.IsCompleted, "the check to finish after Yes");
                await yes;
                Notice notice = Assert.Single(dialogs.Notices);
                Assert.Equal((caption, $"{updated}\nYou can enable auto {what} in Options > Application.", null), (notice.Caption, notice.Text, notice.Script));
            }
            Assert.Equal(content(first), File.ReadAllText(localFile));
            await Ui.PumpUntilAsync(() => inUse(first), "the app to use the new file");

            string second = "StartUp" + Guid.NewGuid().ToString("N")[..8] + "-auto";
            AppEngine.GitHub.Put(Owner, Repo, Branch, sourcePath, content(second));
            settingsService.Set(autoKey, true);
            using (DialogLog auto = new(app))
            {
                await Task.Run(check, Ct);
                Assert.Equal(0, auto.Questions);
                Notice notice = Assert.Single(auto.Notices);
                Assert.Equal((caption, $"{updated}\nYou can disable auto {what} in Options > Application."), (notice.Caption, notice.Text));
            }
            Assert.Equal(content(second), File.ReadAllText(localFile));
            await Ui.PumpUntilAsync(() => inUse(second), "the app to use the auto-updated file");
        }
        finally
        {
            AppEngine.GitHub.Put(Owner, Repo, Branch, sourcePath, null);
            window.Close();
        }
    }

    /// <summary>Commits <paramref name="script"/> and brings every Script on disk up to date with the Script Source, as an update does.</summary>
    private async Task SyncedWithAsync(FakeScript script)
    {
        // A Script that a failed test left running would refuse the update.
        using (EngineConnection connection = await ConnectAsync())
            await connection.ScriptStopAsync(Ct);
        AppEngine.GitHub.Commit(Owner, Repo, Branch, script);
        await Task.Run(app.Get<EngineScripts>().UpdateAsync, Ct);
        Assert.Equal(script.Content, ReadScript(script.Path));
    }

    /// <summary>Waits for the Question's sheet and clicks one of its buttons, as the developer would.</summary>
    private static async Task AnswerAsync(QuestionSheet sheet, string caption, string text, string choice)
    {
        await Ui.PumpUntilAsync(() => sheet.IsVisible && sheet.Shown?.Caption == caption, $"the {caption} sheet");
        Assert.Equal(text, sheet.MessageText.Text);
        Assert.Null(sheet.Shown!.Script);
        Ui.Click(sheet.Choices.Children.OfType<Button>().Single(b => Ui.Text(b) == choice));
        await Ui.PumpUntilAsync(() => !sheet.IsVisible || sheet.Shown?.Caption != caption, "the sheet to close");
    }

    private (Window, QuestionSheet) ShowWindow()
    {
        QuestionSheet sheet = new(Dialogs);
        Window window = new() { Width = 958, Height = 646, Content = new Panel { Children = { new Border(), sheet } } };
        window.Show();
        return (window, sheet);
    }

    private static string ReadScript(string path) => File.ReadAllText(Path.Combine(ClientFileSources.SkuaScriptsDIR, path));

    /// <summary>The Scripts downloaded from the Script Source since the request at <paramref name="index"/>.</summary>
    private static List<string> ScriptDownloadsSince(int index)
    {
        string prefix = $"/raw/{Owner}/{Repo}/refs/heads/{Branch}/";
        return AppEngine.GitHub.Requests.Skip(index)
            .Where(r => r.StartsWith(prefix, StringComparison.Ordinal) && r.EndsWith(".cs", StringComparison.Ordinal))
            .Select(r => r[prefix.Length..])
            .ToList();
    }

    private static async Task<EngineConnection> ConnectAsync() =>
        await EngineClient.TryConnectAsync(EngineEndpoint.FromEnvironment(), Ct) ?? throw new InvalidOperationException("The app's Engine didn't answer.");

    /// <summary>Sets the start-up checks' settings for a test, and puts every one of them back as it was when disposed.</summary>
    private sealed class Settings : IDisposable
    {
        private readonly ISettingsService _settings;
        private readonly Dictionary<string, bool> _was;

        public Settings(AppEngine app, params (string Key, bool Value)[] values)
        {
            _settings = app.Get<ISettingsService>();
            _was = CheckSettings.ToDictionary(k => k, k => _settings.Get<bool>(k));
            // Everything the test doesn't set is off, so only its own check runs.
            foreach (string key in CheckSettings.Where(k => k != "ChangeLogActivated"))
                _settings.Set(key, false);
            _settings.Set("ChangeLogActivated", true);
            foreach ((string key, bool value) in values)
                _settings.Set(key, value);
        }

        public void Dispose()
        {
            foreach ((string key, bool value) in _was)
                _settings.Set(key, value);
        }
    }

    /// <summary>Keeps files as they were, to put back once the test has changed them.</summary>
    private sealed class KeptFiles(params string[] files)
    {
        private readonly Dictionary<string, byte[]?> _kept = files.ToDictionary(f => f, f => File.Exists(f) ? File.ReadAllBytes(f) : null);

        public void Restore()
        {
            foreach ((string file, byte[]? content) in _kept)
            {
                if (content is null)
                    File.Delete(file);
                else
                    File.WriteAllBytes(file, content);
            }
        }
    }

    /// <summary>
    /// The classes in Core's skill sets at the end of each reload while this lives: <see cref="IAdvancedSkillContainer.LoadSkills"/> broadcasts
    /// the change last, and a sync or a save reloads them on another thread.
    /// </summary>
    private sealed class SkillLoads : IDisposable
    {
        private readonly List<HashSet<string>> _loads = [];

        public SkillLoads() =>
            WeakReferenceMessenger.Default.Register<SkillLoads, PropertyChangedMessage<List<AdvancedSkill>>>(this, static (r, m) => r.OnLoaded(m));

        /// <summary>Whether a reload has finished with the class <paramref name="className"/> in the sets.</summary>
        public bool Finished(string className)
        {
            lock (_loads)
                return _loads.Any(l => l.Contains(className));
        }

        /// <summary>Waits until no reload has finished for a while, so none is still running.</summary>
        public async Task SettledAsync()
        {
            int count = -1;
            Stopwatch quiet = Stopwatch.StartNew();
            await Ui.PumpUntilAsync(() =>
            {
                int now;
                lock (_loads)
                    now = _loads.Count;
                if (now != count)
                {
                    count = now;
                    quiet.Restart();
                }
                return quiet.Elapsed > TimeSpan.FromMilliseconds(500);
            }, "the skill sets to stop reloading");
        }

        private void OnLoaded(PropertyChangedMessage<List<AdvancedSkill>> message)
        {
            if (message.PropertyName != nameof(IAdvancedSkillContainer.LoadedSkills))
                return;
            HashSet<string> classes;
            try
            {
                classes = message.NewValue.Select(s => s.ClassName).ToHashSet();
            }
            catch (InvalidOperationException)
            {
                // Another reload changed the list meanwhile; its own message follows.
                classes = [];
            }
            lock (_loads)
                _loads.Add(classes);
        }

        public void Dispose() => WeakReferenceMessenger.Default.Unregister<PropertyChangedMessage<List<AdvancedSkill>>>(this);
    }

    /// <summary>The Questions the broker raises and the Notices it shows while this lives, in order.</summary>
    private sealed class DialogLog : IDisposable
    {
        private readonly ScriptDialogBroker _broker;
        private readonly List<Question> _questions = [];
        private readonly List<Notice> _notices = [];

        public DialogLog(AppEngine app)
        {
            _broker = app.Get<ScriptDialogBroker>();
            _broker.QuestionRaised += OnRaised;
            _broker.NoticeShown += OnShown;
        }

        public int Questions
        {
            get
            {
                lock (_questions)
                    return _questions.Count;
            }
        }

        public List<Notice> Notices
        {
            get
            {
                lock (_notices)
                    return [.. _notices];
            }
        }

        private void OnRaised(Question question)
        {
            lock (_questions)
                _questions.Add(question);
        }

        private void OnShown(Notice notice)
        {
            lock (_notices)
                _notices.Add(notice);
        }

        public void Dispose()
        {
            _broker.QuestionRaised -= OnRaised;
            _broker.NoticeShown -= OnShown;
        }
    }
}
