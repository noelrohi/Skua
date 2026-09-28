namespace Skua.MacOS.Services;

/// <summary>How the Questions raised now are answered, and what they belong to.</summary>
/// <param name="Ask">Whether a Question waits for an answer; otherwise it gets the fallback at once.</param>
/// <param name="Timeout">How long a Question waits before it gets the fallback.</param>
/// <param name="Script">The Script of the run in progress, or null outside a run.</param>
/// <param name="Run">The run in progress, or null outside a run; <see cref="ScriptDialogBroker.ResolveRun"/> answers its Questions.</param>
public sealed record QuestionPolicy(bool Ask, TimeSpan Timeout, string? Script, int? Run);

/// <summary>Who answered a Question.</summary>
public enum QuestionAnswerer
{
    /// <summary><see cref="ScriptDialogBroker.Answer"/>.</summary>
    Agent,

    /// <summary>Nobody, before the Question's timeout.</summary>
    Timeout,

    /// <summary>Nobody was asked: the policy doesn't ask, the run was stopping, or the waiting thread was interrupted.</summary>
    Fallback,

    /// <summary>The developer, in the Mac App's window.</summary>
    User,
}

/// <summary>A Notice a Script showed.</summary>
/// <param name="Thread">The name of the thread that showed it.</param>
public sealed record Notice(string Caption, string Text, string Thread, string? Script);

/// <summary>A Question a Script raised.</summary>
/// <param name="Id">Unique for the broker's lifetime.</param>
/// <param name="ExpiresAt">When it gets the fallback; <see cref="RaisedAt"/> when the policy doesn't ask.</param>
/// <param name="Thread">The name of the thread that raised it, which waits for the answer.</param>
public sealed record Question(
    int Id, string Caption, string Text, IReadOnlyList<string> Choices, DateTimeOffset RaisedAt, DateTimeOffset ExpiresAt, string Thread,
    string? Script, int? Run);

/// <summary>The outcome of <see cref="ScriptDialogBroker.Answer"/>.</summary>
public enum AnswerOutcome
{
    Answered,

    /// <summary>No Question with that id is pending.</summary>
    NotPending,

    /// <summary>The Question doesn't offer that choice; it stays pending.</summary>
    UnknownChoice,
}

/// <summary>
/// The Script Dialog broker: Notices pass straight through, and a Question blocks only the thread that raised it until a Control Surface
/// answers it, it times out, or it is resolved with the fallback. The first answer wins.
/// </summary>
/// <remarks>
/// The events are raised under the broker's lock, so they arrive in order (a Question's raise before its answer); handlers mustn't call back into the broker.
/// </remarks>
public sealed class ScriptDialogBroker
{
    /// <summary>The longest a wait can be.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly object _lock = new();
    private readonly SortedDictionary<int, Waiter> _pending = [];

    /// <summary>The runs <see cref="ResolveRun"/> answered, whose later Questions get the fallback at once until <see cref="Reopen"/>.</summary>
    private readonly HashSet<int> _closedRuns = [];
    private int _lastId;
    private TaskCompletionSource _raised = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How a Question outside any run is answered: it waits 120 s.</summary>
    public static readonly QuestionPolicy OutsideRun = new(true, TimeSpan.FromSeconds(120), null, null);

    /// <summary>Decides how each Question is answered, when it is raised. By default, as <see cref="OutsideRun"/>.</summary>
    public Func<QuestionPolicy> Policy { get; set; } = static () => OutsideRun;

    public event Action<Notice>? NoticeShown;

    public event Action<Question>? QuestionRaised;

    /// <summary>A Question was answered: with the index of the choice, or null for the fallback.</summary>
    public event Action<Question, int?, QuestionAnswerer>? QuestionAnswered;

    /// <summary>Completes when the next Question becomes pending. Take it before looking at <see cref="Pending"/>, then wait on it.</summary>
    public Task NextRaised
    {
        get
        {
            lock (_lock)
                return _raised.Task;
        }
    }

    /// <summary>The pending Questions, oldest first.</summary>
    public IReadOnlyList<Question> Pending()
    {
        lock (_lock)
            return _pending.Values.Select(w => w.Question).ToList();
    }

    /// <summary>Shows a Notice; it never waits.</summary>
    public void Notice(string caption, string text) => Notice(caption, text, Policy());

    /// <summary>
    /// Shows a Notice that belongs to what <paramref name="policy"/> says rather than to what <see cref="Policy"/> does: for the Mac App's own
    /// Notices, which come from no Script whatever runs.
    /// </summary>
    public void Notice(string caption, string text, QuestionPolicy policy)
    {
        Notice notice = new(caption, text, ThreadName(), policy.Script);
        lock (_lock)
            NoticeShown?.Invoke(notice);
    }

    /// <summary>
    /// Raises a Question and blocks the calling thread until it is answered; returns the index of the choice, or null for the fallback.
    /// </summary>
    /// <exception cref="ThreadInterruptedException">The thread was interrupted while it waited; the Question got the fallback.</exception>
    public int? Ask(string caption, string text, IReadOnlyList<string> choices)
    {
        if (Raise(caption, text, choices, Policy(), out TimeSpan wait) is not { } waiter)
            return null;

        try
        {
            if (!waiter.Answered.Wait(wait))
                Resolve(waiter.Question.Id, null, QuestionAnswerer.Timeout);
        }
        finally
        {
            // Interrupted: the thread is being stopped, so nobody is asked any more.
            Resolve(waiter.Question.Id, null, QuestionAnswerer.Fallback);
            waiter.Answered.Dispose();
        }
        return waiter.Choice;
    }

    /// <summary>
    /// Raises a Question without blocking any thread, answered as <paramref name="policy"/> says rather than as <see cref="Policy"/> does:
    /// for a Question the Mac App's own UI thread raises, which mustn't wait on the broker. The task completes with the index of the choice,
    /// or null for the fallback.
    /// </summary>
    /// <param name="cancellationToken">Gives the Question the fallback, e.g. when the app quits.</param>
    public Task<int?> AskAsync(string caption, string text, IReadOnlyList<string> choices, QuestionPolicy policy, CancellationToken cancellationToken = default)
    {
        if (Raise(caption, text, choices, policy, out TimeSpan wait) is not { } waiter)
            return Task.FromResult<int?>(null);

        int id = waiter.Question.Id;
        Timer timeout = new(_ => Resolve(id, null, QuestionAnswerer.Timeout), null, wait, Timeout.InfiniteTimeSpan);
        CancellationTokenRegistration cancelled = cancellationToken.Register(() => Resolve(id, null, QuestionAnswerer.Fallback));
        return waiter.Completion.Task.ContinueWith(answered =>
        {
            timeout.Dispose();
            cancelled.Dispose();
            waiter.Answered.Dispose();
            return answered.Result;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Answers a pending Question with one of its choices, ignoring case; the first answer wins. Returns the Question, unless none with that id
    /// is pending, and the choice as it offers it.
    /// </summary>
    public (AnswerOutcome Outcome, Question? Question, string? Choice) Answer(int id, string choice)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(id, out Waiter? waiter))
                return (AnswerOutcome.NotPending, null, null);
            IReadOnlyList<string> choices = waiter.Question.Choices;
            for (int i = 0; i < choices.Count; i++)
            {
                if (string.Equals(choices[i], choice.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Resolve(id, i, QuestionAnswerer.Agent);
                    return (AnswerOutcome.Answered, waiter.Question, choices[i]);
                }
            }
            return (AnswerOutcome.UnknownChoice, waiter.Question, null);
        }
    }

    /// <summary>Answers a pending Question with the choice at <paramref name="choice"/>; the first answer wins.</summary>
    /// <returns>Whether it answered: false when no Question with that id is pending, or it has no such choice.</returns>
    public bool Answer(int id, int choice, QuestionAnswerer answeredBy)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(id, out Waiter? waiter) || choice < 0 || choice >= waiter.Question.Choices.Count)
                return false;
            Resolve(id, choice, answeredBy);
            return true;
        }
    }

    /// <summary>
    /// Answers every pending Question of the run with the fallback, so the threads waiting on them go on, and every later one at once
    /// until <see cref="Reopen"/>.
    /// </summary>
    public void ResolveRun(int run)
    {
        lock (_lock)
        {
            _closedRuns.Add(run);
            foreach (Waiter waiter in _pending.Values.Where(w => w.Question.Run == run).ToList())
                Resolve(waiter.Question.Id, null, QuestionAnswerer.Fallback);
        }
    }

    /// <summary>The run goes on after <see cref="ResolveRun"/>, e.g. restarted after a relogin: its Questions wait again.</summary>
    public void Reopen(int run)
    {
        lock (_lock)
            _closedRuns.Remove(run);
    }

    /// <summary>Makes a Question pending; returns null, with the Question already answered by the fallback, when the policy doesn't ask.</summary>
    private Waiter? Raise(string caption, string text, IReadOnlyList<string> choices, QuestionPolicy policy, out TimeSpan wait)
    {
        lock (_lock)
        {
            // The run may have been resolved since its policy was read.
            bool ask = policy.Ask && !(policy.Run is { } run && _closedRuns.Contains(run));
            wait = ask ? (policy.Timeout > MaxWait ? MaxWait : policy.Timeout) : TimeSpan.Zero;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Waiter waiter = new(new Question(++_lastId, caption, text, [.. choices], now, now + wait, ThreadName(), policy.Script, policy.Run));
            QuestionRaised?.Invoke(waiter.Question);
            if (!ask)
            {
                QuestionAnswered?.Invoke(waiter.Question, null, QuestionAnswerer.Fallback);
                waiter.Answered.Dispose();
                return null;
            }
            _pending.Add(waiter.Question.Id, waiter);
            _raised.SetResult();
            _raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return waiter;
        }
    }

    private void Resolve(int id, int? choice, QuestionAnswerer answerer)
    {
        lock (_lock)
        {
            if (!_pending.Remove(id, out Waiter? waiter))
                return;
            waiter.Choice = choice;
            QuestionAnswered?.Invoke(waiter.Question, choice, answerer);
            waiter.Answered.Set();
            waiter.Completion.SetResult(choice);
        }
    }

    private static string ThreadName() => Thread.CurrentThread.Name is { Length: > 0 } name ? name : $"thread {Environment.CurrentManagedThreadId}";

    private sealed class Waiter(Question question)
    {
        public Question Question { get; } = question;
        public ManualResetEventSlim Answered { get; } = new();

        /// <summary>Completes as <see cref="Answered"/> is set, with <see cref="Choice"/>; its continuations never run under the lock.</summary>
        public TaskCompletionSource<int?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int? Choice { get; set; }
    }
}
