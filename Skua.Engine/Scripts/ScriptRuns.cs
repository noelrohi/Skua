using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Engine.Game;
using Skua.Engine.Logging;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.MacOS.Services;

namespace Skua.Engine.Scripts;

/// <summary>
/// The Engine's Script state machine: <c>idle | compiling | running | stopping</c>, the run in progress and the last run's outcome.
/// It is the one source of <c>script_status</c> and of the <c>script.*</c> events, and it tags log entries with the run number.
/// </summary>
/// <remarks>
/// Core runs the Script; this follows it through Core's messages, so a start by anyone (the Engine, Core's auto-relogin, an <c>eval</c>)
/// is seen. A run ends once Core has finished with its thread. When Core's auto-relogin stops a Script to restart it after logging
/// back in, the run goes on, and the restart counts as a relogin of the same run.
/// It also decides how Questions are answered: by the dialog mode and timeout of the run in progress, and with the fallback once the run
/// is stopping or has ended, so no thread of it stays blocked on one.
/// Locks are taken in the order ScriptRuns, then the Script Dialog broker, then the logs; none of them calls back up that order.
/// </remarks>
internal sealed class ScriptRuns
{
    private readonly object _lock = new();
    private readonly EngineLogs _logs;
    private readonly IScriptManager _manager;
    private readonly IScriptOption _options;
    private readonly ScriptDialogBroker _dialogs;
    private readonly Func<HeldItemsDto?> _held;
    private readonly bool _keepLagKillerOn;

    private ScriptState _state = ScriptState.Idle;
    private Run? _run;
    private int _lastNumber;

    /// <summary>How often a run that started before the player was in game looks for what they hold.</summary>
    private static readonly TimeSpan HeldPoll = TimeSpan.FromMilliseconds(500);
    private ScriptRunResultDto? _lastRun;

    /// <summary>Core has sent its stopped message for a thread; the thread is over once Core no longer reports a Script running.</summary>
    private bool _threadEnding;

    /// <summary>A stop timed out and the thread still runs, so the Engine stays <see cref="ScriptState.Stopping"/> until it ends.</summary>
    private bool _stuck;

    /// <summary>The next start Core reports is the window's.</summary>
    private bool _windowStarting;

    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="held">What the player holds, which <c>script.started</c> carries; read outside the lock, as it reads the game.</param>
    /// <param name="keepLagKillerOn">Turns the lag killer back on after each run, for an Engine that never shows the game.</param>
    public ScriptRuns(EngineLogs logs, IScriptManager manager, IScriptOption options, ScriptDialogBroker dialogs, Func<HeldItemsDto?> held, bool keepLagKillerOn)
    {
        _held = held;
        _keepLagKillerOn = keepLagKillerOn;
        _logs = logs;
        _manager = manager;
        _options = options;
        _dialogs = dialogs;
        dialogs.Policy = DialogPolicy;

        IMessenger messenger = StrongReferenceMessenger.Default;
        int status = (int)MessageChannels.ScriptStatus;
        messenger.Register<ScriptRuns, ScriptStartedMessage, int>(this, status, static (r, _) => r.OnStarted());
        messenger.Register<ScriptRuns, ScriptErrorMessage, int>(this, status, static (r, m) => r.OnError(m.Exception));
        messenger.Register<ScriptRuns, ScriptStoppedMessage, int>(this, status, static (r, m) => r.OnThreadStopped(m.ToldToStop));
        int game = (int)MessageChannels.GameEvents;
        messenger.Register<ScriptRuns, ReloginStoppingScriptMessage, int>(this, game, static (r, _) => r.OnReloginStopping());
        messenger.Register<ScriptRuns, ReloginFinishedMessage, int>(this, game, static (r, m) => r.OnReloginFinished(m.Success));
        manager.PropertyChanged += OnManagerChanged;
    }

    /// <summary>
    /// Takes over the last run of the Engine this one replaced, as the last run; runs go on numbering from it, so a caller waiting for that
    /// run's outcome can't take a later run for it.
    /// </summary>
    public void TakeOver(ScriptRunResultDto lastRun)
    {
        lock (_lock)
        {
            _lastRun = lastRun;
            _lastNumber = Math.Max(_lastNumber, lastRun.Number);
        }
    }

    /// <param name="held">Whether the run carries what the player held as it started, as only <c>script_status</c> gives it.</param>
    public ScriptStatusDto Status(bool held = false)
    {
        lock (_lock)
        {
            ScriptStatusDto status = StatusLocked();
            return held && _run is { } run && status.Run is { } dto ? status with { Run = dto with { Held = run.Held } } : status;
        }
    }

    /// <summary>Refuses <paramref name="action"/> with <see cref="ErrorCode.ScriptRunning"/> unless no Script runs.</summary>
    public void EnsureIdle(string action)
    {
        lock (_lock)
        {
            if (_state != ScriptState.Idle || _manager.ScriptRunning)
                throw RpcErrors.Of(ErrorCode.ScriptRunning, $"Can't {action} while {Running()}; stop it first with 'skua script stop'.");
        }
    }

    /// <summary>Starts a run for <c>script_start</c>, in <see cref="ScriptState.Compiling"/>, and returns its number.</summary>
    public int Begin(string script, DialogMode dialogs, int dialogTimeoutSec)
    {
        lock (_lock)
        {
            if (_state != ScriptState.Idle || _manager.ScriptRunning)
                throw RpcErrors.Of(ErrorCode.ScriptRunning, $"Can't start {script} while {Running()}; stop it first with 'skua script stop'.");
            _run = new Run(++_lastNumber, script, dialogs, dialogTimeoutSec) { Asked = true };
            _logs.Run = _run.Number;
            SetState(ScriptState.Compiling);
            return _run.Number;
        }
    }

    /// <summary>The run <see cref="Begin"/> started never launched: its Script didn't compile or couldn't start.</summary>
    public void Abandon()
    {
        lock (_lock)
        {
            if (_run is not { Started: false })
                return;
            _run = null;
            _logs.Run = null;
            SetState(ScriptState.Idle);
        }
    }

    /// <summary>Core has launched the Script Thread of the run <see cref="Begin"/> started.</summary>
    public void Launched()
    {
        // Core's started message, sent before the thread starts, has normally reported it started.
        lock (_lock)
        {
            if (_run is not { Started: false })
                return;
        }
        HeldItemsDto? held = _held();
        bool ended;
        lock (_lock)
            ended = StartOursLocked(held);
        if (ended)
            TurnLagKillerBackOn();
    }

    /// <summary>Marks the start Core reports next as the window's, until the lease is disposed.</summary>
    public IDisposable WindowStart()
    {
        lock (_lock)
            _windowStarting = true;
        return new Lease(() =>
        {
            lock (_lock)
                _windowStarting = false;
        });
    }

    /// <summary>
    /// Whether the Script thread running now started without anyone asking: Core's auto-relogin restarted it, or the Script itself (CoreBots,
    /// after a relogin) or an <c>eval</c> started it. Not for a start the Scripts panel or <c>script_start</c> asked for.
    /// </summary>
    public bool Unasked
    {
        get
        {
            lock (_lock)
                return _manager.ScriptRunning && _run is { ReloginPending: false } run && (!run.Asked || run.Relogins > 0);
        }
    }

    /// <summary>
    /// Marks the run as stopping for <c>script_stop</c>. Returns whether a run was in progress, and whether Core must stop its thread:
    /// not when there is no run, or when its thread already ended for an auto-relogin, which then ends the run as stopped.
    /// </summary>
    public (bool WasRunning, bool MustStop) RequestStop()
    {
        lock (_lock)
        {
            if (_run is null)
                return (false, false);
            if (!_run.Started)
                return (true, false);
            _run.StopRequested = true;
            _dialogs.ResolveRun(_run.Number);
            if (!(_run.ReloginPending && _run.ThreadEnded))
            {
                SetState(ScriptState.Stopping);
                return (true, true);
            }
            CancelRestart();
            FinishLocked(ScriptOutcome.Stopped);
        }
        // The lag killer is already back on since the relogin's stop.
        return (true, false);
    }

    /// <summary>Core gave up stopping the run's thread: the run ends as <see cref="ScriptOutcome.StopTimedOut"/> and the Engine stays stopping.</summary>
    public void StopTimedOut()
    {
        lock (_lock)
        {
            if (_run is null || !_manager.ScriptRunning)
                return;
            _stuck = true;
            FinishLocked(ScriptOutcome.StopTimedOut);
        }
    }

    /// <summary>Whether a run is in progress, and a task that completes on the next change of the state or the run.</summary>
    public (bool InProgress, Task Changed) Watch()
    {
        lock (_lock)
            return (_run is not null, _changed.Task);
    }

    private void OnStarted()
    {
        HeldItemsDto? held = _held();
        bool ended = false;
        lock (_lock)
        {
            if (_state == ScriptState.Compiling)
            {
                ended = StartOursLocked(held);
            }
            else if (_run is { ReloginPending: true })
            {
                _run.ReloginPending = false;
                _run.ThreadEnded = false;
                _run.ThreadToldToStop = false;
                _run.Relogins++;
                _dialogs.Reopen(_run.Number);
                LogStarted(_run, restart: true, held);
                SetState(ScriptState.Running);
            }
            else if (_run is null && !_stuck)
            {
                // Started outside script_start: by the window, or by an eval or the Script itself.
                _run = new Run(++_lastNumber, ScriptPaths.Name(_manager.LoadedScript), DialogMode.Ask, ScriptOperations.DefaultDialogTimeoutSec) { Asked = _windowStarting };
                _windowStarting = false;
                _logs.Run = _run.Number;
                ended = StartOursLocked(held);
            }
        }
        if (ended)
            TurnLagKillerBackOn();
    }

    private void OnError(Exception exception)
    {
        Exception thrown = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
        lock (_lock)
        {
            if (_run is null)
                return;
            _run.Error = $"{thrown.GetType().Name}: {thrown.Message}";
            _logs.Event(EventTypes.ScriptError, new { run = _run.Number, script = _run.Script, error = _run.Error, stack = thrown.StackTrace ?? "" });
        }
    }

    private void OnThreadStopped(bool toldToStop)
    {
        lock (_lock)
        {
            _threadEnding = true;
            if (_run is not null)
                _run.ThreadToldToStop = toldToStop;
        }
    }

    /// <summary>Core reports no Script running only once the thread's last cleanup is done, so the run ends here.</summary>
    private void OnManagerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IScriptManager.ScriptRunning) || _manager.ScriptRunning)
            return;
        bool ended = false;
        lock (_lock)
        {
            if (!_threadEnding)
                return;
            _threadEnding = false;

            if (_run is null)
            {
                if (_stuck)
                {
                    _stuck = false;
                    EngineLog.Write("The Script thread that didn't stop in time has ended.");
                    SetState(ScriptState.Idle);
                    ended = true;
                }
            }
            else if (!_run.Started)
            {
                // Launched() or Core's started message ends it once it has been reported started.
                _run.ThreadEnded = true;
            }
            else if (_run.ReloginPending && !_run.StopRequested)
            {
                _run.ThreadEnded = true;
                Notify();
                ended = true;
            }
            else
            {
                if (_run.ReloginPending)
                    CancelRestart();
                FinishLocked(Outcome(_run));
                ended = true;
            }
        }
        if (ended)
            TurnLagKillerBackOn();
    }

    private void OnReloginStopping()
    {
        lock (_lock)
        {
            if (_run is { Started: true, StopRequested: false })
            {
                _run.ReloginPending = true;
                _dialogs.ResolveRun(_run.Number);
                Notify();
            }
        }
    }

    private void OnReloginFinished(bool ok)
    {
        bool ended = false;
        lock (_lock)
        {
            if (_run is { ReloginPending: true })
            {
                _run.Error = ok
                    ? "Core's auto-relogin logged back in but couldn't restart the Script; see the debug log."
                    : "Core's auto-relogin failed, so the Script wasn't restarted.";
                FinishLocked(ScriptOutcome.Error);
                ended = true;
            }
        }
        if (ended)
            TurnLagKillerBackOn();
    }

    /// <summary>Reports a start the Engine made (or first saw) as started; returns whether its thread had already ended, which ends the run.</summary>
    private bool StartOursLocked(HeldItemsDto? held)
    {
        if (_run is not { Started: false } run)
            return false;
        run.Started = true;
        run.StartedAt = DateTimeOffset.UtcNow;
        run.Clock.Restart();
        run.Held = held;
        LogStarted(run, restart: false, held);
        if (held is null)
            HoldLater(run);
        SetState(ScriptState.Running);
        if (!run.ThreadEnded)
            return false;
        FinishLocked(Outcome(run));
        return true;
    }

    /// <summary>
    /// For a run that started before the player was in game, as a Script that logs in itself does, takes what the player holds once they are,
    /// and records it as <c>script.held</c>; it gives up when the run ends.
    /// </summary>
    private void HoldLater(Run run) => _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(HeldPoll);
            lock (_lock)
            {
                if (_run != run)
                    return;
            }
            if (_held() is not { } held)
                continue;
            lock (_lock)
            {
                if (_run != run || run.Held is not null)
                    return;
                run.Held = held;
                _logs.Event(EventTypes.ScriptHeld, new { run = run.Number, script = run.Script, inventory = held.Inventory, temp = held.Temp, bank = held.Bank });
            }
            return;
        }
    });

    private void LogStarted(Run run, bool restart, HeldItemsDto? held) => _logs.Event(EventTypes.ScriptStarted,
        new { run = run.Number, script = run.Script, restart, inventory = held?.Inventory, temp = held?.Temp, bank = held?.Bank });

    /// <remarks>A Script that stops itself is stopped, unless it threw; <c>script_stop</c> wins over an error it causes.</remarks>
    private static ScriptOutcome Outcome(Run run) =>
        run.StopRequested ? ScriptOutcome.Stopped
        : run.Error is not null ? ScriptOutcome.Error
        : run.ThreadToldToStop ? ScriptOutcome.Stopped
        : ScriptOutcome.Completed;

    /// <summary>Ends the run: records it as the last run and emits <c>script.stopped</c>, still tagged with its number.</summary>
    private void FinishLocked(ScriptOutcome outcome)
    {
        Run run = _run!;
        double duration = Math.Round(run.Clock.Elapsed.TotalSeconds, 1);
        _lastRun = new ScriptRunResultDto(run.Number, run.Script, outcome, run.Error, run.StartedAt, duration, run.Relogins);
        _logs.Event(EventTypes.ScriptStopped, run.Error is null
            ? new { run = run.Number, script = run.Script, outcome, durationSec = duration, relogins = run.Relogins }
            : (object)new { run = run.Number, script = run.Script, outcome, durationSec = duration, relogins = run.Relogins, error = run.Error });
        _dialogs.ResolveRun(run.Number);
        _run = null;
        _logs.Run = null;
        SetState(_stuck ? ScriptState.Stopping : ScriptState.Idle);
    }

    /// <summary>
    /// The run in progress decides how a Question is answered, and a run that is stopping never waits for one; outside a run a Question waits
    /// the default timeout.
    /// </summary>
    private QuestionPolicy DialogPolicy()
    {
        lock (_lock)
        {
            return _run is { } run
                ? new QuestionPolicy(
                    run.Dialogs == DialogMode.Ask && !run.StopRequested && !run.ReloginPending, TimeSpan.FromSeconds(run.DialogTimeoutSec), run.Script, run.Number)
                : new QuestionPolicy(true, TimeSpan.FromSeconds(ScriptOperations.DefaultDialogTimeoutSec), null, null);
        }
    }

    /// <summary>
    /// The run was stopped while Core's auto-relogin waits to restart it. Core restarts whatever Script is loaded, so none is:
    /// its restart then fails to read the file and runs nothing.
    /// </summary>
    private void CancelRestart()
    {
        EngineLog.Write("The stopped run's Script won't be restarted by Core's auto-relogin.");
        _manager.SetLoadedScript("");
    }

    /// <summary>After a Script stops, Core turns the lag killer off; skua-engine never shows the game, so it goes back on.</summary>
    private void TurnLagKillerBackOn()
    {
        if (!_keepLagKillerOn)
            return;
        try
        {
            _options.LagKiller = true;
        }
        catch (Exception e)
        {
            EngineLog.Write($"Couldn't turn the lag killer back on: {e.Message}");
        }
    }

    private void SetState(ScriptState state)
    {
        _state = state;
        Notify();
    }

    private void Notify()
    {
        _changed.TrySetResult();
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private string Running() => _run is { } run ? $"{run.Script} is {(_state == ScriptState.Compiling ? "compiling" : "running")}" : "a Script is running";

    private ScriptStatusDto StatusLocked() => new(
        _state,
        _run is { } run ? new ScriptRunDto(
            run.Number, run.Script, run.StartedAt, run.Relogins, run.ReloginPending, run.Dialogs, run.DialogTimeoutSec, Math.Round(run.Clock.Elapsed.TotalSeconds, 1))
            : null,
        _lastRun);

    private sealed class Lease(Action end) : IDisposable
    {
        public void Dispose() => end();
    }

    private sealed class Run(int number, string script, DialogMode dialogs, int dialogTimeoutSec)
    {
        public int Number { get; } = number;
        public string Script { get; } = script;
        public DialogMode Dialogs { get; } = dialogs;
        public int DialogTimeoutSec { get; } = dialogTimeoutSec;
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public bool Asked { get; init; }
        public bool Started { get; set; }
        public bool ThreadEnded { get; set; }
        public bool StopRequested { get; set; }

        /// <summary>Its thread ended after being told to stop, by <c>script_stop</c> or by the Script itself (#162).</summary>
        public bool ThreadToldToStop { get; set; }
        public bool ReloginPending { get; set; }
        public int Relogins { get; set; }
        public string? Error { get; set; }

        /// <summary>What the player held as it started, or first in game during it; null until then.</summary>
        public HeldItemsDto? Held { get; set; }
    }
}
