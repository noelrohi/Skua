using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views.Skills;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models.Skills;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Tests;

/// <summary>The Skills window: the skill set editor, its use rules dialog, and the saved sets with their copy and paste.</summary>
/// <remarks>
/// In the Game View tests' collection, as they share the one Engine. Each test names its sets uniquely, so the sets other tests saved don't
/// matter, and the clipboard is a headless window's, never the Mac's pasteboard.
/// </remarks>
[Collection(nameof(GameViewTests))]
public sealed class SkillsTests(AppEngine app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task The_Skills_menu_item_opens_the_Skills_window()
    {
        MainMenuViewModel viewModel = app.Get<MainMenuViewModel>();
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        NativeMenuItem skills = MainMenus.Native(viewModel, windows).Items.OfType<NativeMenuItem>().Single(i => i.Header == "Skills").Menu!.Items.OfType<NativeMenuItem>().Single();
        Assert.True(skills.IsEnabled);
        windows.OpenWindow("Skills")?.Close();
        await Ui.PumpUntilAsync(() => windows.OpenWindow("Skills") is null, "any earlier Skills window to go");

        skills.Command!.Execute(null);
        await Ui.PumpUntilAsync(() => windows.OpenWindow("Skills") is not null, "the Skills window");
        HostWindow window = windows.OpenWindow("Skills")!;
        try
        {
            Assert.Equal("Advanced Skills", window.Title);
            Assert.IsType<AdvancedSkillsViewModel>(window.DataContext);
            await FoundAsync<AdvancedSkillsView>(window);
        }
        finally
        {
            window.Close();
        }
        await Ui.PumpUntilAsync(() => windows.OpenWindow("Skills") is null, "the closed window to be forgotten");
    }

    [AvaloniaFact]
    public async Task A_skill_set_built_in_the_editor_saves_and_the_running_Script_loads_it_and_its_edits()
    {
        string className = "Editor Class " + Guid.NewGuid().ToString("N")[..6];
        string go = Path.Combine(AppEngine.SkuaDir, $"skills-go-{Guid.NewGuid():N}");
        string loaded = go + ".loaded";
        // As CoreBots does when it equips a class: the Script loads the class's set by name, each time it's told to.
        AppEngine.WriteScript("Tests/LoadsSkills.cs", $$"""
            using System.IO;
            using System.Threading;
            using Skua.Core.Interfaces;
            using Skua.Core.Models.Skills;

            public class TestScript
            {
                public void ScriptMain(IScriptInterface bot)
                {
                    int loads = 0;
                    while (!bot.ShouldExit)
                    {
                        if (File.Exists(@"{{go}}") && File.ReadAllText(@"{{go}}").Length > loads)
                        {
                            loads = File.ReadAllText(@"{{go}}").Length;
                            bot.Skills.LoadAdvanced("{{className}}", false, ClassUseMode.Solo);
                            File.WriteAllText(@"{{loaded}}", $"{loads} {bot.Skills.SkillTimeout} {bot.Skills.SkillUseMode} {bot.Skills.OverrideProvider!.SkillCount}");
                        }
                        Thread.Sleep(50);
                    }
                }
            }
            """);
        using EngineConnection connection = await ConnectAsync();
        await StopAnyScriptAsync(connection);
        (Window window, AdvancedSkillsView skills) = await OpenAsync();
        using PanelTests.BindingErrors errors = new();
        List<(Key, PhysicalKey, RawInputModifiers)> held = [];
        try
        {
            await connection.ScriptStartAsync("Tests/LoadsSkills.cs", cancellationToken: Ct);
            await Ui.PumpUntilAsync(() => app.Get<IScriptManager>().ScriptRunning, "the Script to run");

            skills.FindControl<Expander>("EditExpander")!.IsExpanded = true;
            AdvancedSkillEditorView editor = await FoundAsync<AdvancedSkillEditorView>(skills);
            AdvancedSkillEditorViewModel model = (AdvancedSkillEditorViewModel)editor.DataContext!;
            SkillRulesView rules = Ui.Find<SkillRulesView>(editor)!;
            model.CurrentSkillsList.Clear();

            // Skill 1 with a health rule, then 2 and 3 with none.
            rules.FindControl<CheckBox>("UseRule")!.IsChecked = true;
            rules.FindControl<TextBox>("Health")!.Text = "50";
            AddSkill(editor, "1");
            model.UseRules.ResetUseRulesCommand.Execute(null);
            AddSkill(editor, "2");
            AddSkill(editor, "3");
            ListBox list = editor.FindControl<ListBox>("SkillsList")!;
            await Ui.PumpUntilAsync(() => Shown(list).SequenceEqual(["1 - [Health > 50%]", "2", "3"]), "the three skills in the list");

            // ⌘↓ moves the selected skill down; ⌫ removes the selected one.
            list.SelectedIndex = 0;
            await FocusRowAsync(list, 0);
            Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.Meta, held);
            await Ui.PumpUntilAsync(() => Shown(list).SequenceEqual(["2", "1 - [Health > 50%]", "3"]), "skill 1 moved down");
            list.SelectedIndex = 2;
            await FocusRowAsync(list, 2);
            Press(window, Key.Back, PhysicalKey.Backspace, RawInputModifiers.None, held);
            await Ui.PumpUntilAsync(() => Shown(list).SequenceEqual(["2", "1 - [Health > 50%]"]), "skill 3 removed");

            // Wait for Cooldown and Use if Available are one setting, shown both ways.
            Click(editor.FindControl<CheckBox>("WaitForCooldown")!);
            await Ui.PumpUntilAsync(() => model.UseWaitModeBool && editor.FindControl<CheckBox>("UseIfAvailable")!.IsChecked == false, "Wait for Cooldown");
            Click(editor.FindControl<CheckBox>("UseIfAvailable")!);
            await Ui.PumpUntilAsync(() => !model.UseWaitModeBool && editor.FindControl<CheckBox>("WaitForCooldown")!.IsChecked == false, "Use if Available");
            Click(editor.FindControl<CheckBox>("WaitForCooldown")!);
            editor.FindControl<CheckBox>("ResetOnTargetChange")!.IsChecked = true;
            editor.FindControl<TextBox>("SkillTimeout")!.Text = "150";
            editor.FindControl<TextBox>("ClassName")!.Text = className;
            editor.FindControl<ComboBox>("ClassUseMode")!.SelectedItem = nameof(ClassUseMode.Solo);
            Ui.Click(editor.FindControl<Button>("SaveSkills")!);

            SavedAdvancedSkillsView saved = Ui.Find<SavedAdvancedSkillsView>(skills)!;
            await Ui.PumpUntilAsync(() => saved.Shown.Any(s => s.ClassName == className), "the saved set in the list");
            AdvancedSkill set = saved.Shown.Single(s => s.ClassName == className);
            Assert.Equal(("2 | 1 H>50%", 150, ClassUseMode.Solo, SkillUseMode.WaitForCooldown, true),
                (set.Skills, set.SkillTimeout, set.ClassUseMode, set.SkillUseMode, set.ResetComboOnTargetChange));

            // The running Script loads the set as saved.
            await File.AppendAllTextAsync(go, "1", Ct);
            await Ui.PumpUntilAsync(() => Read(loaded) == "1 150 WaitForCooldown 2", "the Script to load the saved set");

            // Choosing the saved set and editing it: a double-click opens it in the editor; the Script's next load has the edit.
            model.CurrentSkillsList.Clear();
            skills.FindControl<Expander>("EditExpander")!.IsExpanded = false;
            ListBox savedList = saved.FindControl<ListBox>("SkillsList")!;
            saved.FindControl<TextBox>("SearchBox")!.Text = className;
            await Ui.PumpUntilAsync(() => saved.Shown.Select(s => s.ClassName).SequenceEqual([className]), "the search to show only the set");
            savedList.SelectedItem = set;
            savedList.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
            await Ui.PumpUntilAsync(() => skills.FindControl<Expander>("EditExpander")!.IsExpanded && Shown(list).SequenceEqual(["2", "1 - [Health > 50%]"]),
                "the set open in the editor");
            Assert.Equal((className, nameof(ClassUseMode.Solo), "150"),
                (editor.FindControl<TextBox>("ClassName")!.Text, editor.FindControl<ComboBox>("ClassUseMode")!.SelectedItem, editor.FindControl<TextBox>("SkillTimeout")!.Text));
            AddSkill(editor, "4");
            editor.FindControl<TextBox>("SkillTimeout")!.Text = "175";
            Ui.Click(editor.FindControl<Button>("SaveSkills")!);
            await Ui.PumpUntilAsync(() => saved.Shown.Any(s => s.ClassName == className && s.SkillTimeout == 175), "the edited set in the list");
            Assert.Single(app.Get<IAdvancedSkillContainer>().LoadedSkills, s => s.ClassName == className);

            await File.AppendAllTextAsync(go, "2", Ct);
            await Ui.PumpUntilAsync(() => Read(loaded) == "2 175 WaitForCooldown 3", "the Script to load the edited set");
            Assert.True(errors.Lines.Count == 0, string.Join("\n", errors.Lines));
        }
        finally
        {
            Release(window, held);
            await StopAnyScriptAsync(connection);
            RemoveSets(className);
            window.Close();
            File.Delete(go);
            File.Delete(loaded);
        }
    }

    [AvaloniaFact]
    public async Task Return_on_a_skill_edits_its_rules_in_a_dialog_and_Confirm_gives_the_skill_the_edit()
    {
        (Window window, AdvancedSkillsView skills) = await OpenAsync();
        List<Window> dialogs = [];
        AvaloniaDialogService dialogService = app.Get<AvaloniaDialogService>();
        Action<Window>? previous = dialogService.WindowCreated;
        dialogService.WindowCreated = dialogs.Add;
        List<(Key, PhysicalKey, RawInputModifiers)> held = [];
        using PanelTests.BindingErrors errors = new();
        try
        {
            skills.FindControl<Expander>("EditExpander")!.IsExpanded = true;
            AdvancedSkillEditorView editor = await FoundAsync<AdvancedSkillEditorView>(skills);
            AdvancedSkillEditorViewModel model = (AdvancedSkillEditorViewModel)editor.DataContext!;
            model.CurrentSkillsList.Clear();
            model.UseRules.ResetUseRulesCommand.Execute(null);
            AddSkill(editor, "2");
            ListBox list = editor.FindControl<ListBox>("SkillsList")!;
            await Ui.PumpUntilAsync(() => Shown(list).SequenceEqual(["2"]), "the skill in the list");

            // The UI thread runs the dialog in a nested frame, so it's driven from here through the dispatcher.
            Task edited = Task.Run(async () =>
            {
                while (Dispatcher.UIThread.Invoke(() => dialogs.OfType<DialogWindow>().FirstOrDefault(d => d.IsVisible) is null))
                    await Task.Delay(20, Ct);
                Dispatcher.UIThread.Invoke(() =>
                {
                    DialogWindow dialog = dialogs.OfType<DialogWindow>().First(d => d.IsVisible);
                    Assert.Equal("Edit Rules", dialog.Title);
                    SkillRulesView dialogRules = Ui.Find<SkillRulesView>(dialog)!;
                    dialogRules.FindControl<CheckBox>("UseRule")!.IsChecked = true;
                    dialogRules.FindControl<ToggleButton>("ManaCompare")!.IsChecked = false;
                    dialogRules.FindControl<TextBox>("Mana")!.Text = "30";
                    // Two aura checks, OR'd; the checks' rows bind too.
                    dialogRules.FindControl<CheckBox>("MultiAura")!.IsChecked = true;
                    dialogRules.FindControl<ComboBox>("Operator")!.SelectedIndex = 1;
                    Ui.Click(dialogRules.FindControl<Button>("AddAura")!);
                    Ui.Click(dialogRules.FindControl<Button>("AddAura")!);
                    SkillRulesViewModel rules = (SkillRulesViewModel)dialogRules.DataContext!;
                    rules.MultiAuraChecks[0].AuraName = "Focus";
                    rules.MultiAuraChecks[0].StackCount = 3;
                    rules.MultiAuraChecks[1].AuraName = "Stale";
                    Ui.Click(Ui.Find<Button>(dialog, b => b.Name == "Confirm")!);
                });
            }, Ct);
            list.SelectedIndex = 0;
            await FocusRowAsync(list, 0);
            Dispatcher.UIThread.Post(() => Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None, held));
            await Ui.PumpUntilAsync(() => edited.IsCompleted, "the rules dialog to be confirmed");
            await edited;

            await Ui.PumpUntilAsync(() => Shown(list).SequenceEqual(["2 - [Mana < 30%] - [Multi-Aura (OR) 'Focus'>3 'Stale'>0]"]), "the skill with its new rules");
            Assert.Equal("2 M<30% MA>\"Focus\" 3: MA>\"Stale\" 0", model.CurrentSkillsList[0].Convert());
            // The editor's own rules, for the next skill added, are untouched.
            Assert.False(model.UseRules.UseRuleBool);
            Assert.True(errors.Lines.Count == 0, string.Join("\n", errors.Lines));
        }
        finally
        {
            Release(window, held);
            dialogService.WindowCreated = previous;
            foreach (Window dialog in dialogs.Where(d => d.IsVisible))
                dialog.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_saved_skill_set_copies_to_the_clipboard_and_pastes_back()
    {
        string id = Guid.NewGuid().ToString("N")[..6];
        AdvancedSkill first = new($"Copied Class {id}", "1 | 2 H<40% | 3", 120, ClassUseMode.Farm, SkillUseMode.WaitForCooldown, true);
        AdvancedSkill second = new($"Copied Class {id}", "4 | 3", 250, ClassUseMode.Base, SkillUseMode.UseIfAvailable);
        IAdvancedSkillContainer container = app.Get<IAdvancedSkillContainer>();
        (Window window, AdvancedSkillsView skills) = await OpenAsync();
        List<(Key, PhysicalKey, RawInputModifiers)> held = [];
        try
        {
            container.LoadedSkills.Add(first);
            container.LoadedSkills.Add(second);
            container.Save();
            SavedAdvancedSkillsView saved = await FoundAsync<SavedAdvancedSkillsView>(skills);
            saved.Clipboard = new AvaloniaClipboardService(() => window.Clipboard);
            saved.FindControl<TextBox>("SearchBox")!.Text = $"copied class {id}";
            await Ui.PumpUntilAsync(() => saved.Shown.Count == 2, "the two sets, found by the search");

            // ⌘C copies the selected sets as text.
            ListBox list = saved.FindControl<ListBox>("SkillsList")!;
            list.SelectedItems!.Add(saved.Shown.Single(s => s.ClassUseMode == ClassUseMode.Farm));
            list.SelectedItems!.Add(saved.Shown.Single(s => s.ClassUseMode == ClassUseMode.Base));
            // The list refills, as it does when Core reloads the sets in the background, and keeps each selected once.
            saved.FindControl<TextBox>("SearchBox")!.Text = $"Copied Class {id}";
            Assert.Equal(2, list.SelectedItems!.Count);
            await FocusRowAsync(list, 0);
            Press(window, Key.C, PhysicalKey.C, RawInputModifiers.Meta, held);
            string copied = await ClipboardTextAsync(window, t => t.Contains(id), "the sets on the clipboard");
            using (JsonDocument json = JsonDocument.Parse(copied))
            {
                Assert.Equal(["Base", "Farm"], json.RootElement.EnumerateArray().Select(s => s.GetProperty("classUseMode").GetString()).Order());
                JsonElement farm = json.RootElement.EnumerateArray().Single(s => s.GetProperty("classUseMode").GetString() == "Farm");
                Assert.Equal(("1 | 2 H<40% | 3", 120, "WaitForCooldown", true),
                    (farm.GetProperty("skills").GetString(), farm.GetProperty("skillTimeout").GetInt32(), farm.GetProperty("skillUseMode").GetString(),
                        farm.GetProperty("resetComboOnTargetChange").GetBoolean()));
            }

            // Removed, then pasted back: saved again as they were.
            list.SelectedItems!.Clear();
            RemoveSets(first.ClassName);
            await Ui.PumpUntilAsync(() => saved.Shown.Count == 0 && !container.LoadedSkills.Any(s => s.ClassName == first.ClassName), "the sets removed");

            // An empty list still takes the focus, as a click on it gives it, so it can take a paste.
            Assert.True(list.Focus(), "the empty list takes the focus");
            Press(window, Key.V, PhysicalKey.V, RawInputModifiers.Meta, held);
            await Ui.PumpUntilAsync(() => saved.Shown.Count == 2, "the pasted sets in the list");
            AdvancedSkill farmSet = saved.Shown.Single(s => s.ClassUseMode == ClassUseMode.Farm);
            Assert.Equal((first.Skills, 120, SkillUseMode.WaitForCooldown, true), (farmSet.Skills, farmSet.SkillTimeout, farmSet.SkillUseMode, farmSet.ResetComboOnTargetChange));
            AdvancedSkill baseSet = saved.Shown.Single(s => s.ClassUseMode == ClassUseMode.Base);
            Assert.Equal((second.Skills, 250, SkillUseMode.UseIfAvailable, false), (baseSet.Skills, baseSet.SkillTimeout, baseSet.SkillUseMode, baseSet.ResetComboOnTargetChange));
            await Ui.PumpUntilAsync(() => Read(Path.Combine(AppEngine.SkuaDir, "UserAdvancedSkills.json")).Contains(first.ClassName), "the pasted sets in the saved file");

            // Pasting a set whose class and mode are saved replaces it, and text that isn't sets pastes nothing.
            saved.Clipboard.SetText(SkillSetText.From([new AdvancedSkill(first.ClassName, "0 | 1", 90, ClassUseMode.Farm, SkillUseMode.UseIfAvailable)]));
            await ClipboardTextAsync(window, t => t.Contains("\"0 | 1\""), "the edited set on the clipboard");
            Press(window, Key.V, PhysicalKey.V, RawInputModifiers.Meta, held);
            await Ui.PumpUntilAsync(() => saved.Shown.Count == 2 && saved.Shown.Single(s => s.ClassUseMode == ClassUseMode.Farm).Skills == "0 | 1", "the set replaced");
            saved.Clipboard.SetText("not a skill set");
            await ClipboardTextAsync(window, t => t == "not a skill set", "text on the clipboard");
            Press(window, Key.V, PhysicalKey.V, RawInputModifiers.Meta, held);
            Assert.Equal(2, container.LoadedSkills.Count(s => s.ClassName == first.ClassName));
        }
        finally
        {
            Release(window, held);
            RemoveSets(first.ClassName);
            window.Close();
        }
    }

    [Fact]
    public void Skill_set_text_round_trips_every_field_and_reads_one_set_or_nothing()
    {
        AdvancedSkill set = new("Void Highlord", "1 | 2 | 3 WW500 | 4 A>\"Void Aura\" 5 TARGET", 175, ClassUseMode.Ultra, SkillUseMode.WaitForCooldown, true);

        AdvancedSkill back = Assert.Single(SkillSetText.Parse(SkillSetText.From([set])));

        Assert.Equal((set.ClassName, set.Skills, set.SkillTimeout, set.ClassUseMode, set.SkillUseMode, set.ResetComboOnTargetChange),
            (back.ClassName, back.Skills, back.SkillTimeout, back.ClassUseMode, back.SkillUseMode, back.ResetComboOnTargetChange));
        Assert.Equal(ClassUseMode.Solo, Assert.Single(SkillSetText.Parse("""{"className":"Legion Revenant","classUseMode":"solo","skills":"3 | 1 | 2 | 4"}""")).ClassUseMode);
        Assert.Empty(SkillSetText.Parse("1 | 2 | 3"));
        Assert.Empty(SkillSetText.Parse("""[{"className":"","skills":"1"}]"""));
        Assert.Empty(SkillSetText.Parse("""[{"className":"A","classUseMode":"Nope","skills":"1"}]"""));
    }

    private void RemoveSets(string className)
    {
        IAdvancedSkillContainer container = app.Get<IAdvancedSkillContainer>();
        if (container.LoadedSkills.RemoveAll(s => s.ClassName == className) > 0)
            container.Save();
    }

    private static void AddSkill(AdvancedSkillEditorView editor, string skill) =>
        Ui.Click(Ui.Find<Button>(editor.FindControl<StackPanel>("AddSkillButtons")!, b => b.CommandParameter as string == skill)!);

    /// <summary>Clicks a check box as a pointer does: it toggles, keeping its binding, then says it was clicked.</summary>
    private static void Click(CheckBox box)
    {
        box.SetCurrentValue(ToggleButton.IsCheckedProperty, box.IsChecked != true);
        box.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Focuses a list's row, as a click on it does, once the list has laid it out.</summary>
    /// <remarks>
    /// Through the list first: a row that is already its window's focus, as a window a test before showed can leave it, keeps the keyboard
    /// on another window when focused again, and the keys then go to this window alone.
    /// </remarks>
    private static async Task FocusRowAsync(ListBox list, int index)
    {
        await Ui.PumpUntilAsync(() => list.ContainerFromIndex(index) is not null, $"row {index} of the list");
        list.Focus();
        Assert.True(list.ContainerFromIndex(index)!.Focus(), $"row {index} takes the focus");
    }

    private static IEnumerable<string> Shown(ListBox list) => list.Items.OfType<SkillItemViewModel>().Select(s => s.DisplayString);

    /// <summary>
    /// The window's clipboard text once it satisfies <paramref name="done"/>. Read without the service's nested frame, which a test's own
    /// polling would re-enter while a copy is still in flight.
    /// </summary>
    private static async Task<string> ClipboardTextAsync(Window window, Func<string, bool> done, string what)
    {
        string text = "";
        Task<string?> read = window.Clipboard!.TryGetTextAsync();
        await Ui.PumpUntilAsync(() =>
        {
            if (!read.IsCompleted)
                return false;
            text = read.Result ?? "";
            if (done(text))
                return true;
            read = window.Clipboard!.TryGetTextAsync();
            return false;
        }, what);
        return text;
    }

    private static string Read(string file) => File.Exists(file) ? File.ReadAllText(file) : "";

    /// <summary>Presses a key and releases it, noting it as held meanwhile so a failure between the two still releases it.</summary>
    private static void Press(Window window, Key key, PhysicalKey physical, RawInputModifiers modifiers, List<(Key, PhysicalKey, RawInputModifiers)> held)
    {
        held.Add((key, physical, modifiers));
        window.KeyPress(key, modifiers, physical, null!);
        window.KeyRelease(key, modifiers, physical, null!);
        held.Remove((key, physical, modifiers));
    }

    private static void Release(Window window, List<(Key, PhysicalKey, RawInputModifiers)> held)
    {
        foreach ((Key key, PhysicalKey physical, RawInputModifiers modifiers) in held)
            window.KeyRelease(key, modifiers, physical, null!);
        held.Clear();
    }

    private static async Task StopAnyScriptAsync(EngineConnection connection)
    {
        if ((await connection.ScriptStatusAsync(Ct)).State != ScriptState.Idle)
            await connection.ScriptStopAsync(Ct);
    }

    private async Task<(Window, AdvancedSkillsView)> OpenAsync()
    {
        // Core's main menu registers the managed windows as it is made.
        app.Get<MainMenuViewModel>();
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        // A window a test before closed stays known until it has gone; a new one is wanted, not that one.
        windows.OpenWindow("Skills")?.Close();
        await Ui.PumpUntilAsync(() => windows.OpenWindow("Skills") is null, "any earlier Skills window to go");
        windows.ShowManagedWindow("Skills");
        await Ui.PumpUntilAsync(() => windows.OpenWindow("Skills") is not null, "the Skills window");
        Window window = windows.OpenWindow("Skills")!;
        return (window, await FoundAsync<AdvancedSkillsView>(window));
    }

    private static async Task<T> FoundAsync<T>(global::Avalonia.Visual root) where T : global::Avalonia.Visual
    {
        await Ui.PumpUntilAsync(() => Ui.Find<T>(root) is not null, $"a {typeof(T).Name}");
        return Ui.Find<T>(root)!;
    }

    private static async Task<EngineConnection> ConnectAsync() =>
        await EngineClient.TryConnectAsync(EngineEndpoint.FromEnvironment(), Ct) ?? throw new InvalidOperationException("The app's Engine didn't answer.");
}
