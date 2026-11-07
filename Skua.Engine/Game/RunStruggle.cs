using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Engine.Logging;
using Skua.Engine.Scripts;

namespace Skua.Engine.Game;

/// <summary>
/// Turns a run's deaths piling up into one signal: it records <see cref="EventTypes.RunStruggling"/> once the player's deaths during a run reach
/// the threshold within the window, so a Hook can act on it. A death that finds fewer in the window, or another run, re-arms it.
/// </summary>
internal sealed class RunStruggle
{
    /// <summary>How many deaths within the window make a run struggle: 5 unless set; 0 turns it off.</summary>
    public const string DeathsVariable = "SKUA_STRUGGLE_DEATHS";

    /// <summary>How far back the deaths are counted: 300 s unless set.</summary>
    public const string WindowVariable = "SKUA_STRUGGLE_WINDOW_SEC";

    private readonly ScriptRuns _runs;
    private readonly EngineLogs _logs;
    private readonly CombatTally _tally;
    private readonly ScriptGoal _goal;
    private readonly IScriptMap _map;
    private readonly int _threshold;
    private readonly int _windowSec;
    private readonly object _lock = new();

    /// <summary>The run <see cref="EventTypes.RunStruggling"/> was last recorded for, until a death re-arms it.</summary>
    private int? _recorded;

    public RunStruggle(ScriptRuns runs, EngineLogs logs, CombatTally tally, ScriptGoal goal, IScriptMap map)
    {
        _runs = runs;
        _logs = logs;
        _tally = tally;
        _goal = goal;
        _map = map;
        _threshold = int.TryParse(Environment.GetEnvironmentVariable(DeathsVariable), out int deaths) && deaths >= 0 ? deaths : 5;
        _windowSec = int.TryParse(Environment.GetEnvironmentVariable(WindowVariable), out int sec) && sec > 0 ? sec : 300;
        if (_threshold > 0)
            tally.Died += Died;
    }

    private void Died(DateTime at)
    {
        try
        {
            ScriptStatusDto status = _runs.Status();
            if (status.State == ScriptState.Compiling || status.Run is not { } run)
                return;
            // Only the run's deaths: none from before it started.
            DateTime started = run.StartedAt.UtcDateTime;
            DateTime windowStart = at - TimeSpan.FromSeconds(_windowSec);
            int deaths = _tally.DeathsBetween(windowStart > started ? windowStart : started.AddTicks(-1), at);
            lock (_lock)
            {
                if (deaths < _threshold)
                {
                    _recorded = null;
                    return;
                }
                if (_recorded == run.Number)
                    return;
                _recorded = run.Number;
            }
            // Core's map, as the game last joined it: no read of the game on the thread that handles its calls.
            string? map = string.IsNullOrEmpty(_map.LastMap) ? null : _map.LastMap;
            _logs.Event(EventTypes.RunStruggling, new { run = run.Number, script = run.Script, deaths, windowSec = _windowSec, map, goal = _goal.WithGoal(run)?.Goal });
        }
        catch (Exception e)
        {
            EngineLog.Write($"Couldn't count the run's deaths: {e.Message}");
        }
    }
}
