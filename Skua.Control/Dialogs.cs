namespace Skua.Control;

/// <summary>A pending Question: a Script Dialog that waits for a Control Surface to answer it.</summary>
/// <param name="Id">What names it in <c>dialog_answer</c>; unique for the Engine's lifetime.</param>
/// <param name="Text">The message, cut to 64 KB.</param>
/// <param name="Choices">The buttons it offers, in order: <c>Yes</c> and <c>No</c>, or the Script's own.</param>
/// <param name="ExpiresAt">When it gets the fallback answer unless answered first.</param>
/// <param name="Thread">The name of the thread that raised it, which waits for the answer; e.g. <c>Script Thread</c>.</param>
/// <param name="Script">The Script of the run it was raised in, as <c>script_start</c> named it, or null outside a run.</param>
public sealed record QuestionDto(
    int Id, string Caption, string Text, IReadOnlyList<string> Choices, DateTimeOffset RaisedAt, DateTimeOffset ExpiresAt, string Thread, string? Script);

/// <summary>The reply to <c>dialogs</c>.</summary>
/// <param name="Questions">The pending Questions, oldest first.</param>
public sealed record DialogsResult(IReadOnlyList<QuestionDto> Questions);

/// <summary>The reply to <c>dialog_answer</c>.</summary>
/// <param name="Choice">The choice as the Question offers it, whatever its case in the request.</param>
public sealed record DialogAnswerResult(int Id, string Choice);

/// <summary>Who answered a Question, as <c>question.answered</c> reports it.</summary>
public enum AnsweredBy
{
    /// <summary><c>dialog_answer</c> answered it.</summary>
    Agent,

    /// <summary>Nobody answered before the run's dialog timeout, so it got the fallback.</summary>
    Timeout,

    /// <summary>It got the fallback without waiting: the run's dialog mode is <see cref="DialogMode.Cancel"/>, or the run was stopping.</summary>
    Fallback,

    /// <summary>The developer answered it in the Mac App's window.</summary>
    User,
}
