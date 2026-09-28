using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Avalonia.Services;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.ViewModels;
using Skua.MacOS.Services;

namespace Skua.Avalonia.Tests;

/// <summary>
/// Script Dialogs in the Mac App: a Question's sheet, answered from the window, <c>dialog_answer</c>, the terminal, the timeout or a stop,
/// whichever comes first, and Notices that never wait. Against the app's Engine and the fake Game Host.
/// </summary>
[Collection(nameof(GameViewTests))]
public sealed class ScriptDialogTests(AppEngine app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<Window> _dialogs = [];

    [AvaloniaFact]
    public async Task A_Question_shows_a_sheet_and_answering_it_there_resumes_the_Script_as_answered_by_user()
    {
        AppEngine.WriteScript("Tests/SheetAsk.cs", Script("""
            bool? answer = bot.ShowMessageBox("Buy the Class for 500 AC?", "Confirm", true);
            bot.Log($"answer {(answer is null ? "null" : answer.ToString())}");
            """));
        (Window window, QuestionSheet sheet, _) = ShowWindow();
        using EngineConnection connection = await ConnectAsync();

        ScriptStartResult start = await connection.ScriptStartAsync("Tests/SheetAsk.cs", cancellationToken: Ct);
        await Ui.PumpUntilAsync(() => sheet.IsVisible && sheet.Shown?.Caption == "Confirm", "the Question's sheet");

        Assert.Equal("Buy the Class for 500 AC?", sheet.MessageText.Text);
        Assert.Equal(["Yes", "No"], Buttons(sheet).Select(Ui.Text));
        Assert.Equal("From Tests/SheetAsk.cs", sheet.SourceText.Text);
        Assert.Matches(@"^No answer in (2:00|1:5\d) cancels it\.$", sheet.CountdownText.Text);
        Ui.Click(Buttons(sheet)[1]);
        await Ui.PumpUntilAsync(() => !sheet.IsVisible, "the sheet to close");

        ScriptWaitResult wait = await connection.ScriptWaitAsync(60, Ct);
        Assert.Equal(ScriptWaitReason.Ended, wait.Reason);
        Assert.Contains(await ScriptLinesAsync(connection, start.Run), l => l == "answer False");
        LogEntryDto answered = Assert.Single(await EventsAsync(connection, EventTypes.QuestionAnswered, start.Run));
        Assert.Equal(("No", "user"), (Get(answered, "choice"), Get(answered, "answeredBy")));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dialog_answer_from_the_CLI_first_closes_the_sheet_and_a_late_click_is_ignored()
    {
        AppEngine.WriteScript("Tests/SheetCli.cs", Script("""
            DialogResult result = bot.ShowMessageBox("Which way?", "Route", "Alpha", "Beta");
            bot.Log($"chose {result.Text}");
            """));
        (Window window, QuestionSheet sheet, _) = ShowWindow();
        ScriptDialogsViewModel dialogs = app.Get<ScriptDialogsViewModel>();
        using EngineConnection connection = await ConnectAsync();

        ScriptStartResult start = await connection.ScriptStartAsync("Tests/SheetCli.cs", cancellationToken: Ct);
        await Ui.PumpUntilAsync(() => sheet.Shown?.Caption == "Route", "the Question's sheet");
        Question question = sheet.Shown!;
        Button alpha = Buttons(sheet)[0];

        (int exitCode, string output) = await ScriptsPanelTests.CliAsync(["dialogs", "answer", question.Id.ToString(), "beta"]);
        Assert.True(exitCode == 0, output);
        // The click lands after the CLI's answer: the first answer won.
        alpha.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(dialogs.Answer(question, 0));
        await Ui.PumpUntilAsync(() => !sheet.IsVisible, "the sheet to close");

        await connection.ScriptWaitAsync(60, Ct);
        Assert.Contains(await ScriptLinesAsync(connection, start.Run), l => l == "chose Beta");
        LogEntryDto answered = Assert.Single(await EventsAsync(connection, EventTypes.QuestionAnswered, start.Run));
        Assert.Equal(("Beta", "agent"), (Get(answered, "choice"), Get(answered, "answeredBy")));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_timeout_and_a_stop_still_answer_with_the_fallback_and_close_the_sheet()
    {
        AppEngine.WriteScript("Tests/SheetTimeout.cs", Script("""
            bool? answer = bot.ShowMessageBox("Nobody answers this.", "Timeout", true);
            bot.Log($"timeout answer {(answer is null ? "null" : answer.ToString())}");
            """));
        AppEngine.WriteScript("Tests/SheetStop.cs", Script("""
            bot.ShowMessageBox("Stop answers this.", "Stop", true);
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));
        (Window window, QuestionSheet sheet, _) = ShowWindow();
        using EngineConnection connection = await ConnectAsync();

        ScriptStartResult timedOut = await connection.ScriptStartAsync("Tests/SheetTimeout.cs", dialogTimeoutSec: 1, cancellationToken: Ct);
        await Ui.PumpUntilAsync(() => sheet.Shown?.Caption == "Timeout", "the timing-out Question's sheet");
        await Ui.PumpUntilAsync(() => !sheet.IsVisible, "the sheet to close at the timeout");
        await connection.ScriptWaitAsync(60, Ct);
        Assert.Contains(await ScriptLinesAsync(connection, timedOut.Run), l => l == "timeout answer null");
        Assert.Equal((null, "timeout"), Answer(Assert.Single(await EventsAsync(connection, EventTypes.QuestionAnswered, timedOut.Run))));

        ScriptStartResult stopped = await connection.ScriptStartAsync("Tests/SheetStop.cs", cancellationToken: Ct);
        await Ui.PumpUntilAsync(() => sheet.Shown?.Caption == "Stop", "the stopped Question's sheet");
        await connection.ScriptStopAsync(Ct);
        await Ui.PumpUntilAsync(() => !sheet.IsVisible, "the sheet to close at the stop");
        Assert.Equal((null, "fallback"), Answer(Assert.Single(await EventsAsync(connection, EventTypes.QuestionAnswered, stopped.Run))));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Notices_show_in_the_list_with_a_badge_and_never_stall_the_Script_even_on_the_timer_thread()
    {
        // #32's test with the app host: a handler's Notice on the timer thread mustn't stall the handlers.
        AppEngine.WriteScript("Tests/SheetNotices.cs", Script("""
            bot.ShowMessageBox("Line one of the Notice.\nLine two.", "Heads up");
            int ticks = 0;
            bot.Handlers.RegisterHandler(1, b =>
            {
                b.ShowMessageBox($"tick {Interlocked.Increment(ref ticks)}", "Handler");
                return true;
            });
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref ticks) < 5 && waited.Elapsed.TotalSeconds < 20)
                Thread.Sleep(20);
            bot.Handlers.Clear();
            bot.Log($"ticks {(Volatile.Read(ref ticks) >= 5 ? "reached" : "stalled")}");
            """));
        (Window window, QuestionSheet sheet, NoticesButton notices) = ShowWindow();
        ScriptDialogsViewModel dialogs = app.Get<ScriptDialogsViewModel>();
        dialogs.ClearNotices();
        using EngineConnection connection = await ConnectAsync();

        ScriptStartResult start = await connection.ScriptStartAsync("Tests/SheetNotices.cs", cancellationToken: Ct);
        await connection.ScriptWaitAsync(60, Ct);

        Assert.Contains(await ScriptLinesAsync(connection, start.Run), l => l == "ticks reached");
        await Ui.PumpUntilAsync(() => dialogs.Notices.Count >= 6, "the Notices in the list");
        Assert.False(sheet.IsVisible);
        ShownNotice headsUp = dialogs.Notices.Last();
        Assert.Equal(("Heads up", "Line one of the Notice.\nLine two.", "Tests/SheetNotices.cs", "Line one of the Notice."),
            (headsUp.Caption, headsUp.Text, headsUp.Script, headsUp.Summary));
        Assert.All(dialogs.Notices.SkipLast(1), n => Assert.Equal("Handler", n.Caption));
        Assert.True(notices.BadgeBorder.IsVisible);
        Assert.Equal(dialogs.Notices.Count.ToString(), notices.Badge.Text);

        // Seeing the list reads them; opening one shows its full text to select and copy.
        notices.Flyout!.ShowAt(notices);
        await Ui.PumpUntilAsync(() => !notices.BadgeBorder.IsVisible, "the badge to clear");
        notices.Flyout.Hide();
        Window full = notices.Open(headsUp);
        Assert.Equal(headsUp.Text, Ui.Find<SelectableTextBlock>(full, t => t.Name == "Text")!.Text);
        Assert.NotNull(Ui.Find<Button>(full, b => b.Name == "Copy"));
        full.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Starting_a_second_Script_from_the_window_asks_on_a_sheet_without_blocking_the_UI_thread()
    {
        // #78's freeze: the Scripts panel asks whether to stop the running Script on the UI thread, which must keep running to show the sheet.
        AppEngine.WriteScript("Tests/SheetFirst.cs", Script("""
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));
        string second = AppEngine.WriteScript("Tests/SheetSecond.cs", Script("""
            bot.Log("second started");
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));
        (Window window, QuestionSheet sheet, _) = ShowWindow();
        app.Get<ScriptLoaderViewModel>();
        using EngineConnection connection = await ConnectAsync();
        await connection.ScriptStartAsync("Tests/SheetFirst.cs", cancellationToken: Ct);
        await Ui.PumpUntilAsync(() => app.Get<IScriptManager>().ScriptRunning, "the first Script to run");

        // The question is raised in a job on the UI thread; from here, all the test sees of that thread is what it runs meanwhile.
        Stopwatch asked = Stopwatch.StartNew();
        Task<QuestionDto> answered = Task.Run(async () =>
        {
            using EngineConnection agent = await ConnectAsync();
            QuestionDto question = await PendingAsync(agent, "Script Error");
            // Runs on the UI thread while it waits for the answer: it isn't blocked, and the sheet shows the Question.
            Dispatcher.UIThread.Invoke(() =>
            {
                Assert.True(sheet.IsVisible);
                Assert.Equal(question.Id, sheet.Shown!.Id);
                Assert.Equal(["No", "Yes"], Buttons(sheet).Select(Ui.Text));
                Ui.Click(Buttons(sheet)[1]);
            });
            return question;
        }, Ct);
        Dispatcher.UIThread.Post(() => StrongReferenceMessenger.Default.Send<StartScriptMessage, int>(new(second), (int)MessageChannels.ScriptStatus));
        await Ui.PumpUntilAsync(() => answered.IsCompleted, "the sheet to be answered");

        QuestionDto raised = await answered;
        Assert.Null(raised.Script);
        Assert.Contains("Do you want to stop it and start SheetSecond.cs?", raised.Text);
        Assert.True(asked.Elapsed < TimeSpan.FromSeconds(30), $"asked for {asked.Elapsed}");
        await Ui.PumpUntilAsync(() => !sheet.IsVisible, "the sheet to close");
        // Yes stops the first Script and, as on Windows, starts the second 5 s later.
        Task secondRuns = Task.Run(async () =>
        {
            while ((await connection.ScriptStatusAsync(Ct)) is not { State: ScriptState.Running, Run.Script: var script } || !script.EndsWith("SheetSecond.cs", StringComparison.Ordinal))
                await Task.Delay(50, Ct);
        }, Ct);
        await Ui.PumpUntilAsync(() => secondRuns.IsCompleted, "the second Script to run");
        LogEntryDto answer = (await EventsAsync(connection, EventTypes.QuestionAnswered, null)).Last(e => Get(e, "id") == raised.Id.ToString());
        Assert.Equal(("Yes", "user"), (Get(answer, "choice"), Get(answer, "answeredBy")));
        await connection.ScriptStopAsync(Ct);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Question_the_UI_thread_raises_can_be_answered_by_an_agent()
    {
        (Window window, QuestionSheet sheet, _) = ShowWindow();
        IDialogService service = app.Get<IDialogService>();
        Assert.IsType<AvaloniaDialogService>(service);

        Task<(int, string)> agent = Task.Run(async () =>
        {
            using EngineConnection connection = await ConnectAsync();
            QuestionDto question = await PendingAsync(connection, "From the window");
            return await ScriptsPanelTests.CliAsync(["dialogs", "answer", question.Id.ToString(), "Keep"]);
        }, Ct);
        DialogResult result = service.ShowMessageBox("Keep it?", "From the window", "Drop", "Keep");

        Assert.Equal(("Keep", 1), (result.Text, result.Value));
        (int exitCode, string output) = await agent;
        Assert.True(exitCode == 0, output);
        await Ui.PumpUntilAsync(() => !sheet.IsVisible, "the sheet to close");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Skua_script_start_follow_in_a_terminal_still_prompts_and_the_first_answer_wins()
    {
        AppEngine.WriteScript("Tests/SheetFollow.cs", Script("""
            bool? first = bot.ShowMessageBox("First question?", "One", true);
            bool? second = bot.ShowMessageBox("Second question?", "Two", true);
            bot.Log($"answers {first} {second}");
            """));
        (Window window, QuestionSheet sheet, _) = ShowWindow();
        ScriptDialogsViewModel dialogs = app.Get<ScriptDialogsViewModel>();

        // script(1) gives the CLI a terminal, so it asks each Question.
        ProcessStartInfo startInfo = new("/usr/bin/script", ["-q", "/dev/null", Path.Combine(AppContext.BaseDirectory, "skua"), "script", "start", "Tests/SheetFollow.cs", "--follow", "--no-update"])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment[EngineEndpoint.SkuaDirVariable] = AppEngine.SkuaDir;
        startInfo.Environment[EngineClientOptions.EngineExecutableVariable] = "/usr/bin/false";
        using Process follow = Process.Start(startInfo)!;
        StringBuilderReader output = new(follow.StandardOutput);
        try
        {
            // The window answers the first before the terminal does.
            await Ui.PumpUntilAsync(() => sheet.Shown?.Caption == "One" && output.Text.Contains("Answer Question", StringComparison.Ordinal), "the first Question's sheet and prompt");
            Assert.True(dialogs.Answer(sheet.Shown!, 0));
            await Ui.PumpUntilAsync(() => output.Text.Contains("answered by user: Yes", StringComparison.Ordinal), "the terminal to see the window's answer");

            // The terminal answers the second before the window does.
            await Ui.PumpUntilAsync(() => sheet.Shown?.Caption == "Two", "the second Question's sheet");
            Question secondQuestion = sheet.Shown!;
            await Ui.PumpUntilAsync(() => output.Text.Contains($"Answer Question {secondQuestion.Id},", StringComparison.Ordinal), "the second prompt");
            await follow.StandardInput.WriteLineAsync("2");
            await follow.StandardInput.FlushAsync(Ct);
            await Ui.PumpUntilAsync(() => !sheet.IsVisible, "the sheet to close");
            Assert.False(dialogs.Answer(secondQuestion, 0));
            await Ui.PumpUntilAsync(() => follow.HasExited, "the follow to end");

            Assert.True(follow.ExitCode == 0, output.Text);
            Assert.Contains("answered by agent: No", output.Text);
            Assert.Contains("answers True False", output.Text);
        }
        finally
        {
            if (!follow.HasExited)
                follow.Kill(entireProcessTree: true);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task ShowDialog_from_a_Scripts_thread_shows_a_real_dialog_that_waits_for_its_view_to_close_it()
    {
        IDialogService service = app.Get<IDialogService>();
        app.Get<AvaloniaDialogService>().WindowCreated = _dialogs.Add;
        InputDialogViewModel input = new("Quantity", "How many to buy?", "Quantity", numericInputOnly: false);
        using PanelTests.BindingErrors errors = new();

        Task<bool?> shown = Task.Run(() => service.ShowDialog(input), Ct);
        await Ui.PumpUntilAsync(() => Dialog() is not null, "the input dialog");
        DialogWindow dialog = Dialog()!;
        Assert.Equal("Quantity", dialog.Title);
        Assert.False(shown.IsCompleted);
        Ui.Find<TextBox>(dialog, t => t.Name == "Input")!.Text = "42";
        Ui.Click(Ui.Find<Button>(dialog, b => b.Name == "Confirm")!);
        await Ui.PumpUntilAsync(() => shown.IsCompleted, "ShowDialog to return");
        Assert.Equal((true, "42"), (await shown, input.DialogTextInput));

        // A Script's options, saved by the callback as the dialog closes, however it closes.
        Skua.Core.Options.ScriptOptionContainer container = new(service) { Storage = "dialog-test-" + Guid.NewGuid().ToString("N")[..8] };
        container.Options.Add(new Skua.Core.Options.Option<bool>("fast", "Go fast", "Skips the long way.", false));
        container.Options.Add(new Skua.Core.Options.Option<string>("target", "Target", "Whom to fight.", "Frogzard") { Category = "Combat" });
        List<OptionContainerItemViewModel>? saved = null;
        OptionContainerViewModel options = new(container);
        Task<bool?> configured = Task.Run(() => service.ShowDialog(options, vm => saved = vm.Options), Ct);
        await Ui.PumpUntilAsync(() => Dialog() is { DataContext: OptionContainerViewModel }, "the options dialog");
        dialog = Dialog()!;
        Assert.Equal("Options", dialog.Title);
        await Ui.PumpUntilAsync(() => Ui.Find<CheckBox>(dialog) is not null, "the options' rows");
        Ui.Find<CheckBox>(dialog)!.IsChecked = true;
        dialog.Close();
        await Ui.PumpUntilAsync(() => configured.IsCompleted, "ShowDialog to return");
        Assert.Null(await configured);
        Assert.Equal(true, saved!.Single(o => o.Option.Name == "fast").Value);
        Assert.True(errors.Lines.Count == 0, string.Join("\n", errors.Lines));

        // A view model without a view isn't shown, as headless.
        Assert.Null(service.ShowDialog(new object()));
    }

    private DialogWindow? Dialog() => _dialogs.OfType<DialogWindow>().LastOrDefault(d => d.IsVisible);

    /// <summary>A window with the sheet and the Notices over the app's Script Dialogs, as the main window has them.</summary>
    private (Window, QuestionSheet, NoticesButton) ShowWindow()
    {
        ScriptDialogsViewModel dialogs = app.Get<ScriptDialogsViewModel>();
        QuestionSheet sheet = new(dialogs);
        NoticesButton notices = new(dialogs);
        DockPanel.SetDock(notices, Dock.Bottom);
        Window window = new() { Width = 958, Height = 646, Content = new Panel { Children = { new DockPanel { Children = { notices, new Border() } }, sheet } } };
        window.Show();
        return (window, sheet, notices);
    }

    private static List<Button> Buttons(QuestionSheet sheet) => sheet.Choices.Children.OfType<Button>().ToList();

    private static async Task<QuestionDto> PendingAsync(EngineConnection connection, string caption)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            if ((await connection.DialogsAsync(Ct)).Questions.FirstOrDefault(q => q.Caption == caption) is { } question)
                return question;
            if (waited.Elapsed > Ui.Timeout)
                throw new TimeoutException($"No Question '{caption}' became pending.");
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<List<string>> ScriptLinesAsync(EngineConnection connection, int run) =>
        (await AllAsync(connection, LogKind.Script)).Where(e => e.Run == run).Select(e => e.Text ?? "").ToList();

    private static async Task<List<LogEntryDto>> EventsAsync(EngineConnection connection, string type, int? run) =>
        (await AllAsync(connection, LogKind.Events)).Where(e => e.Type == type && (run is null || e.Run == run)).ToList();

    private static async Task<List<LogEntryDto>> AllAsync(EngineConnection connection, LogKind kind)
    {
        List<LogEntryDto> entries = [];
        string? cursor = null;
        while (true)
        {
            LogPage page = await connection.LogsAsync(kind, cursor, 1000, Ct);
            entries.AddRange(page.Entries);
            cursor = page.Next;
            if (page.Entries.Count == 0)
                return entries;
        }
    }

    private static string? Get(LogEntryDto entry, string property) =>
        entry.Data is { } data && data.TryGetProperty(property, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;

    private static (string?, string?) Answer(LogEntryDto entry) => (Get(entry, "choice"), Get(entry, "answeredBy"));

    private static string Script(string body) => $$"""
        using System.Threading;
        using Skua.Core.Interfaces;
        using Skua.Core.Models;

        public class TestScript
        {
            public void ScriptMain(IScriptInterface bot)
            {
                {{body}}
            }
        }
        """;

    private static async Task<EngineConnection> ConnectAsync() =>
        await EngineClient.TryConnectAsync(EngineEndpoint.FromEnvironment(), Ct) ?? throw new InvalidOperationException("The app's Engine didn't answer.");

    /// <summary>Reads a process's output as it comes, for waits on what it has printed so far.</summary>
    private sealed class StringBuilderReader
    {
        private readonly System.Text.StringBuilder _text = new();

        public StringBuilderReader(StreamReader reader) => _ = Task.Run(async () =>
        {
            char[] buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                lock (_text)
                    _text.Append(buffer, 0, read);
            }
        });

        public string Text
        {
            get
            {
                lock (_text)
                    return _text.ToString();
            }
        }
    }
}
