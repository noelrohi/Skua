using System.ComponentModel;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Engine.Scripts;
using StreamJsonRpc;

namespace Skua.Engine;

/// <summary>
/// The Engine's Script operations for the Mac App's own panels, so a start, a stop or an update from the window is the same operation a
/// Control Surface makes with <c>script_start</c>, <c>script_stop</c> or <c>scripts_update</c>; a reset follows <c>scripts_update</c>'s rules.
/// </summary>
public sealed class EngineScripts
{
    private readonly IScriptManager _core;
    private Attached? _attached;

    public EngineScripts(IScriptManager core)
    {
        _core = core;
        ScriptManager = new WindowScriptManager(this);
    }

    /// <summary>
    /// Core's Script manager as the window's Scripts panel uses it: a start is refused while the Engine runs or starts a Script, holds
    /// its action slot or updates the Scripts, as <c>script_start</c> is, and a stop from outside the Script's own thread is a <c>script_stop</c>, so the run ends as
    /// stopped rather than completed.
    /// </summary>
    public IScriptManager ScriptManager { get; }

    /// <summary>Syncs the Scripts folder with the Script Source, as <c>skua scripts update</c> does.</summary>
    /// <exception cref="LocalRpcException">It was refused (a Script runs, another update runs) or the Script Source couldn't be read.</exception>
    public Task<ScriptsUpdateResult> UpdateAsync() => Operations.Source.UpdateAsync();

    /// <summary>
    /// Deletes the local Scripts, the junk items list aside, and downloads every Script from the Script Source again, as the Windows Manager's
    /// Reset Scripts does. Only the Mac App resets; no Control Surface can.
    /// </summary>
    /// <exception cref="LocalRpcException">It was refused (a Script runs, an update runs) or the Script Source couldn't be read.</exception>
    /// <exception cref="IOException">A local Script couldn't be deleted.</exception>
    public Task<ScriptsUpdateResult> ResetAsync() => Operations.Source.ResetAsync();

    /// <summary>Whether the Script running now started without anyone asking, such as CoreBots' restart after a relogin (#144).</summary>
    public bool RunningUnasked => _attached?.Runs.Unasked ?? false;

    internal void Attach(
        ScriptOperations scripts, ScriptSourceOperations source, ScriptRuns runs, ActionSlot slot, ActionSlot scriptsSlot, SemaphoreSlim compiling) =>
        _attached = new Attached(scripts, source, runs, slot, scriptsSlot, compiling);

    private Attached Operations => _attached ?? throw new InvalidOperationException("The Engine hasn't started.");

    private sealed record Attached(
        ScriptOperations Scripts, ScriptSourceOperations Source, ScriptRuns Runs, ActionSlot Slot, ActionSlot ScriptsSlot, SemaphoreSlim Compiling);

    /// <summary>Forwards to Core's manager, except for starts, stops and compiles, which go through the Engine's rules.</summary>
    private sealed class WindowScriptManager : IScriptManager
    {
        /// <summary>The name Core gives the thread a Script runs on.</summary>
        private const string ScriptThread = "Script Thread";

        private readonly EngineScripts _owner;

        public WindowScriptManager(EngineScripts owner)
        {
            _owner = owner;
            // Raised as this manager's own, so a binding to it sees its changes.
            Core.PropertyChanged += (_, e) => PropertyChanged?.Invoke(this, e);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private IScriptManager Core => _owner._core;

        public CancellationTokenSource? ScriptCts => Core.ScriptCts;
        public bool ShouldExit => Core.ShouldExit;
        public bool ScriptRunning => Core.ScriptRunning;
        public string LoadedScript => Core.LoadedScript;
        public string CompiledScript => Core.CompiledScript;

        public IScriptOptionContainer? Config
        {
            get => Core.Config;
            set => Core.Config = value;
        }

        public Task RestartScriptAsync() => Core.RestartScriptAsync();

        public void SetLoadedScript(string path) => Core.SetLoadedScript(path);

        public void LoadScriptConfig(object? script) => Core.LoadScriptConfig(script);

        /// <remarks>Core compiles one thing at a time, so this waits for a compile of the Engine's own.</remarks>
        public object? Compile(string source)
        {
            SemaphoreSlim compiling = _owner.Operations.Compiling;
            compiling.Wait();
            try
            {
                return Core.Compile(source);
            }
            finally
            {
                compiling.Release();
            }
        }

        /// <returns>Core's failure, or the Engine's refusal, whose message says what to do.</returns>
        public async Task<Exception?> StartScript()
        {
            Attached engine = _owner.Operations;
            string action = $"start {ScriptPaths.Name(Core.LoadedScript)}";
            IDisposable lease;
            IDisposable scriptsLease;
            try
            {
                engine.Runs.EnsureIdle(action);
                lease = engine.Slot.Take(action);
            }
            catch (LocalRpcException e)
            {
                return e;
            }
            try
            {
                scriptsLease = engine.ScriptsSlot.Take(action);
            }
            catch (LocalRpcException e)
            {
                lease.Dispose();
                return e;
            }

            using (lease)
            using (scriptsLease)
            {
                await engine.Compiling.WaitAsync();
                try
                {
                    using (engine.Runs.WindowStart())
                        return await Core.StartScript();
                }
                finally
                {
                    engine.Compiling.Release();
                }
            }
        }

        /// <remarks>A Script stopping itself stays Core's own stop, so its run still ends as completed.</remarks>
        public async ValueTask StopScript(bool runScriptStoppingEvent = true)
        {
            if (Thread.CurrentThread.Name == ScriptThread)
                await Core.StopScript(runScriptStoppingEvent);
            else
                await _owner.Operations.Scripts.StopAsync(CancellationToken.None);
        }
    }
}
