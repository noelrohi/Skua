using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Skua.MacOS.Services;

namespace Skua.Avalonia;

/// <summary>
/// The Script Dialogs the Mac App shows, over the Engine's one <see cref="ScriptDialogBroker"/>: the pending Questions, which the window can
/// answer as <c>dialog_answer</c> can, and the Notices shown so far (ADR 0006, "Script Dialogs keep one broker").
/// </summary>
/// <remarks>
/// Its collections and properties change on the UI thread only. The broker raises its events under its lock, on the thread that raised the
/// dialog, so each is queued to the UI thread rather than handled there.
/// </remarks>
public sealed partial class ScriptDialogsViewModel : ObservableObject
{
    /// <summary>The most Notices kept: a handler can show one on every tick.</summary>
    public const int MaxNotices = 500;

    private readonly ScriptDialogBroker _broker;

    public ScriptDialogsViewModel(ScriptDialogBroker broker)
    {
        _broker = broker;
        // Subscribed first, so a Question raised meanwhile is either listed or queued, never lost; the queued raise of a listed one is ignored.
        broker.QuestionRaised += q => Dispatcher.UIThread.Post(() => OnRaised(q));
        broker.QuestionAnswered += (q, _, _) => Dispatcher.UIThread.Post(() => OnAnswered(q.Id));
        broker.NoticeShown += n => Dispatcher.UIThread.Post(() => OnNotice(n));
        foreach (Question question in broker.Pending())
            Questions.Add(question);
        Current = Questions.FirstOrDefault();
    }

    /// <summary>The pending Questions, oldest first.</summary>
    public ObservableCollection<Question> Questions { get; } = [];

    /// <summary>The Question the sheet shows: the oldest pending one, or null when none is.</summary>
    [ObservableProperty]
    private Question? _current;

    /// <summary>The Notices shown, newest first, up to <see cref="MaxNotices"/>.</summary>
    public ObservableCollection<ShownNotice> Notices { get; } = [];

    /// <summary>How many Notices arrived since <see cref="MarkNoticesRead"/>; the badge shows it.</summary>
    [ObservableProperty]
    private int _unread;

    /// <summary>Runs on the UI thread as a Question becomes pending; the app shows its window or posts a notification.</summary>
    public event Action<Question>? QuestionRaised;

    /// <summary>Runs on the UI thread as a Notice arrives; the app posts a notification when it isn't frontmost.</summary>
    public event Action<ShownNotice>? NoticeShown;

    /// <summary>
    /// Answers a pending Question from the window, as <c>answeredBy: user</c>. The first answer wins, so a click after another answer is
    /// ignored.
    /// </summary>
    /// <returns>Whether this answer was the one taken.</returns>
    public bool Answer(Question question, int choice) => _broker.Answer(question.Id, choice, QuestionAnswerer.User);

    public void MarkNoticesRead() => Unread = 0;

    public void ClearNotices()
    {
        Notices.Clear();
        Unread = 0;
    }

    private void OnRaised(Question question)
    {
        // Already answered (its answer is queued behind this), or listed at construction.
        if (!_broker.Pending().Any(q => q.Id == question.Id) || Questions.Any(q => q.Id == question.Id))
            return;
        int index = 0;
        while (index < Questions.Count && Questions[index].Id < question.Id)
            index++;
        Questions.Insert(index, question);
        Current = Questions[0];
        QuestionRaised?.Invoke(question);
    }

    private void OnAnswered(int id)
    {
        if (Questions.FirstOrDefault(q => q.Id == id) is not { } question)
            return;
        Questions.Remove(question);
        Current = Questions.FirstOrDefault();
    }

    private void OnNotice(Notice notice)
    {
        ShownNotice shown = new(notice.Caption, notice.Text, notice.Script, DateTimeOffset.Now);
        Notices.Insert(0, shown);
        while (Notices.Count > MaxNotices)
            Notices.RemoveAt(Notices.Count - 1);
        Unread = Math.Min(Unread + 1, MaxNotices);
        NoticeShown?.Invoke(shown);
    }
}

/// <summary>A Notice as the list shows it.</summary>
/// <param name="Script">The Script of the run it was shown in, or null outside a run.</param>
/// <param name="ShownAt">When it arrived, in local time.</param>
public sealed record ShownNotice(string Caption, string Text, string? Script, DateTimeOffset ShownAt)
{
    /// <summary>Its first line, cut to fit a list row.</summary>
    public string Summary
    {
        get
        {
            string line = Text.AsSpan().TrimStart().ToString().Split('\n', 2)[0].TrimEnd('\r');
            return line.Length > 120 ? line[..119] + "…" : line;
        }
    }
}
