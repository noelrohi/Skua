using Skua.Engine.Logging;
using Skua.Control;
using Skua.MacOS.Services;

namespace Skua.Engine.Scripts;

/// <summary><c>dialogs</c> and <c>dialog_answer</c>, and the <c>notice.shown</c> and <c>question.*</c> events, over the Script Dialog broker.</summary>
internal sealed class DialogOperations
{
    private readonly ScriptDialogBroker _broker;
    private readonly EngineLogs _logs;

    public DialogOperations(ScriptDialogBroker broker, EngineLogs logs)
    {
        _broker = broker;
        _logs = logs;
        broker.NoticeShown += notice => logs.Event(EventTypes.NoticeShown,
            new { caption = notice.Caption, text = notice.Text, thread = notice.Thread, script = notice.Script });
        broker.QuestionRaised += question => logs.Event(EventTypes.QuestionRaised, new
        {
            id = question.Id,
            caption = question.Caption,
            text = question.Text,
            choices = question.Choices,
            raisedAt = question.RaisedAt,
            expiresAt = question.ExpiresAt,
            thread = question.Thread,
            script = question.Script,
        });
        broker.QuestionAnswered += (question, choice, answerer) => logs.Event(EventTypes.QuestionAnswered,
            new { id = question.Id, choice = choice is { } index ? question.Choices[index] : null, answeredBy = AnsweredByOf(answerer) });
    }

    public IReadOnlyList<QuestionDto> Pending() => _broker.Pending().Select(Describe).ToList();

    public DialogAnswerResult Answer(int id, string choice)
    {
        (AnswerOutcome outcome, Question? question, string? chosen) = _broker.Answer(id, choice);
        return outcome switch
        {
            AnswerOutcome.Answered => new DialogAnswerResult(id, _logs.Scrub(chosen!)),
            AnswerOutcome.NotPending => throw RpcErrors.Of(ErrorCode.DialogNotPending,
                $"No Question {id} is pending: it was answered, it timed out, or it never existed. 'skua dialogs' lists the pending ones."),
            _ => throw RpcErrors.Of(ErrorCode.InvalidArgument,
                _logs.Scrub($"Question {id} doesn't offer '{choice}'; choose one of {string.Join(", ", question!.Choices)}.")),
        };
    }

    /// <summary>The Question as the Control Surface shows it: redacted, with its text cut to 64 KB, as its events record it.</summary>
    private QuestionDto Describe(Question question) => new(
        question.Id, _logs.Scrub(question.Caption), _logs.ScrubDialogText(question.Text), question.Choices.Select(_logs.Scrub).ToList(),
        question.RaisedAt, question.ExpiresAt, question.Thread, question.Script is { } script ? _logs.Scrub(script) : null);

    private static AnsweredBy AnsweredByOf(QuestionAnswerer answerer) => answerer switch
    {
        QuestionAnswerer.Agent => AnsweredBy.Agent,
        QuestionAnswerer.Timeout => AnsweredBy.Timeout,
        _ => AnsweredBy.Fallback,
    };
}
