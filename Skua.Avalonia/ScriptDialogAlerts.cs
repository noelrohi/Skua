using Avalonia.Controls;

namespace Skua.Avalonia;

/// <summary>
/// Makes sure a Script Dialog is seen. While the app is frontmost, a Question brings the main window, and its sheet, to the front, reopening a
/// closed window. While it isn't, a Question or a Notice posts a macOS notification instead, and the sheet waits for the Dock icon.
/// </summary>
public sealed class ScriptDialogAlerts
{
    private readonly Window _main;
    private readonly Func<bool> _frontmost;
    private readonly Action<string, string> _notify;

    /// <param name="main">The main window, which carries the Question sheet.</param>
    /// <param name="frontmost">Whether the app is frontmost: one of its windows is active.</param>
    /// <param name="notify">Posts a notification with a title and a body.</param>
    public ScriptDialogAlerts(ScriptDialogsViewModel model, Window main, Func<bool> frontmost, Action<string, string> notify)
    {
        _main = main;
        _frontmost = frontmost;
        _notify = notify;
        model.QuestionRaised += OnQuestion;
        model.NoticeShown += OnNotice;
    }

    private void OnQuestion(MacOS.Services.Question question)
    {
        if (_frontmost())
        {
            _main.Show();
            _main.Activate();
            return;
        }
        _notify(question.Script is { } script ? $"Skua: {script} asks" : "Skua asks", Body(question.Caption, question.Text));
    }

    private void OnNotice(ShownNotice notice)
    {
        if (!_frontmost())
            _notify(notice.Script is { } script ? $"Skua: {script}" : "Skua", Body(notice.Caption, notice.Text));
    }

    /// <summary>A notification's body: the caption before the text, cut to what a banner shows.</summary>
    internal static string Body(string caption, string text)
    {
        string body = caption.Length > 0 ? $"{caption}: {text}" : text;
        return body.Length > 240 ? body[..239] + "…" : body;
    }
}
