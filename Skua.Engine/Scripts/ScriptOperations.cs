using System.Diagnostics;
using System.Globalization;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.MacOS.Services;
using StreamJsonRpc;

namespace Skua.Engine.Scripts;

/// <summary><c>script_options</c>, <c>script_start</c>, <c>script_stop</c>, <c>script_status</c> and <c>script_wait</c>, through Core's own Script manager.</summary>
internal sealed class ScriptOperations
{
    public const int DefaultDialogTimeoutSec = 120;
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(300);

    private static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(2);

    /// <summary>How long <c>script_stop</c> waits for a Script that is still compiling to start, so it can be stopped.</summary>
    private static readonly TimeSpan CompileWait = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollStep = TimeSpan.FromMilliseconds(50);

    private readonly IScriptManager _manager;
    private readonly ScriptRuns _runs;
    private readonly ScriptDialogBroker _dialogs;
    private readonly ActionSlot _slot;
    private readonly ActionSlot _scriptsSlot;
    private readonly SemaphoreSlim _compiling;

    /// <param name="slot">The Engine's action slot.</param>
    /// <param name="scriptsSlot">The Engine's Scripts slot, so a Script never compiles during a Scripts update.</param>
    /// <param name="compiling">Held around every compile, since Core's Script manager compiles one thing at a time.</param>
    public ScriptOperations(IScriptManager manager, ScriptRuns runs, ScriptDialogBroker dialogs, ActionSlot slot, ActionSlot scriptsSlot, SemaphoreSlim compiling)
    {
        _manager = manager;
        _runs = runs;
        _dialogs = dialogs;
        _slot = slot;
        _scriptsSlot = scriptsSlot;
        _compiling = compiling;
    }

    public async Task<ScriptOptionsResult> OptionsAsync(string script)
    {
        (string name, string file) = ScriptPaths.Resolve(script);
        string action = $"list the options of {name}";
        _runs.EnsureIdle(action);
        using IDisposable lease = _slot.Take(action);
        using IDisposable scriptsLease = _scriptsSlot.Take(action);
        IScriptOptionContainer config = await LoadConfigAsync(name, file);
        return new ScriptOptionsResult(name, config.Storage, Options(config).Select(o => Describe(config, o.Key, o.Option)).ToList());
    }

    public async Task<ScriptStartResult> StartAsync(string script, IReadOnlyDictionary<string, string>? options, DialogMode? dialogs, int? dialogTimeoutSec)
    {
        if (dialogTimeoutSec < 1)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"dialogTimeoutSec must be at least 1, not {dialogTimeoutSec}.");
        (string name, string file) = ScriptPaths.Resolve(script);
        string action = $"start {name}";
        _runs.EnsureIdle(action);
        using IDisposable lease = _slot.Take(action);
        using IDisposable scriptsLease = _scriptsSlot.Take(action);

        int run = _runs.Begin(name, dialogs ?? DialogMode.Ask, dialogTimeoutSec ?? DefaultDialogTimeoutSec);
        try
        {
            if (options is { Count: > 0 })
            {
                IScriptOptionContainer config = await LoadConfigAsync(name, file);
                try
                {
                    Store(name, config, options);
                }
                catch (Exception e) when (e is not LocalRpcException)
                {
                    throw StartFailure(name, e);
                }
            }

            Exception? failure;
            await _compiling.WaitAsync();
            try
            {
                _manager.SetLoadedScript(file);
                failure = await _manager.StartScript();
            }
            finally
            {
                _compiling.Release();
            }
            if (failure is not null)
                throw StartFailure(name, failure);
        }
        catch
        {
            _runs.Abandon();
            throw;
        }

        _runs.Launched();
        // The Script Thread makes its cancellation source as it starts; a stop before then wouldn't reach it.
        Stopwatch waited = Stopwatch.StartNew();
        while (_manager.ScriptCts is null && _manager.ScriptRunning && waited.Elapsed < LaunchWait)
            await Task.Delay(PollStep);
        return new ScriptStartResult(run, _runs.Status());
    }

    /// <summary>Stops the run cooperatively with Core's own stop, which waits for the thread, interrupts it and gives up after about 10 s.</summary>
    public async Task<ScriptStopResult> StopAsync(CancellationToken cancellationToken)
    {
        // A Script still compiling can't be stopped yet; stop it once it has started.
        Stopwatch waited = Stopwatch.StartNew();
        while (_runs.Status().State == ScriptState.Compiling && waited.Elapsed < CompileWait)
            await Task.Delay(PollStep, cancellationToken);

        (bool wasRunning, bool mustStop) = _runs.RequestStop();
        // Also a thread whose earlier stop timed out.
        if (mustStop || (!wasRunning && _manager.ScriptRunning))
            await _manager.StopScript();
        bool ended = !_manager.ScriptRunning;
        if (mustStop && !ended)
            _runs.StopTimedOut();
        return new ScriptStopResult(wasRunning, ended, _runs.Status());
    }

    public ScriptStatusDto Status() => _runs.Status();

    public async Task<ScriptWaitResult> WaitAsync(int? timeoutSec, CancellationToken cancellationToken)
    {
        if (timeoutSec < 0)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"timeoutSec must be at least 0, not {timeoutSec}.");
        TimeSpan timeout = timeoutSec is { } seconds ? TimeSpan.FromSeconds(seconds) : DefaultWait;
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            Task raised = _dialogs.NextRaised;
            if (_dialogs.Pending().Count > 0)
                return new ScriptWaitResult(ScriptWaitReason.Question, _runs.Status());
            (bool inProgress, Task changed) = _runs.Watch();
            if (!inProgress)
                return new ScriptWaitResult(ScriptWaitReason.Ended, _runs.Status());
            TimeSpan left = timeout - waited.Elapsed;
            if (left <= TimeSpan.Zero)
                return new ScriptWaitResult(ScriptWaitReason.Timeout, _runs.Status());
            try
            {
                await Task.WhenAny(raised, changed).WaitAsync(left, cancellationToken);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    /// <summary>Compiles the Script and loads its options with their stored values, as Core does before a start.</summary>
    private async Task<IScriptOptionContainer> LoadConfigAsync(string name, string file)
    {
        await _compiling.WaitAsync();
        try
        {
            _manager.SetLoadedScript(file);
            string source = await File.ReadAllTextAsync(file);
            object? compiled = await Task.Run(() => _manager.Compile(source));
            if (compiled is null)
                throw RpcErrors.Of(ErrorCode.CompileFailed, $"{name} compiled to nothing; it needs a public class.");
            _manager.LoadScriptConfig(compiled);
            return _manager.Config!;
        }
        catch (Exception e) when (e is not LocalRpcException)
        {
            throw StartFailure(name, e);
        }
        finally
        {
            _compiling.Release();
        }
    }

    private static LocalRpcException StartFailure(string name, Exception failure)
    {
        switch (failure)
        {
            case FileNotFoundException or DirectoryNotFoundException:
                return RpcErrors.Of(ErrorCode.ScriptNotFound, $"{name} is gone: {failure.Message}");
            case ScriptCompileException compile:
                return CompileFailure.Of(name, compile.Message);
            case not null when failure.Message == "Script already running.":
                return RpcErrors.Of(ErrorCode.ScriptRunning, $"Can't start {name}: Core already runs a Script; stop it first with 'skua script stop'.");
            default:
                // Core loads the Script's options while it starts it, so a Script with malformed options fails here too.
                string message = $"{failure.GetType().Name}: {failure.Message}";
                return RpcErrors.Of(ErrorCode.CompileFailed, $"{name} couldn't be started: {message}", [message]);
        }
    }

    /// <summary>
    /// Checks every value first, then stores them all in the Script's options storage at once. Keys may repeat, as several options can share a
    /// name; only a key that names one option, and not one of a Script's text entries, can be set.
    /// </summary>
    private static void Store(string name, IScriptOptionContainer config, IReadOnlyDictionary<string, string> values)
    {
        ILookup<string, IOption> byKey = Options(config).ToLookup(o => o.Key, o => o.Option, StringComparer.OrdinalIgnoreCase);
        List<(IOption Option, string Value)> checkedValues = [];
        foreach ((string key, string value) in values)
        {
            IOption[] matches = byKey[key].ToArray();
            if (matches.Length == 0)
                throw RpcErrors.Of(ErrorCode.InvalidArgument, $"{name} has no option '{key}'; 'skua script options {name}' lists them.");
            if (matches.All(IsText))
                throw RpcErrors.Of(ErrorCode.InvalidArgument, $"'{key}' is text {name} shows among its options, not an option, so it can't be set.");
            if (matches.Length > 1)
                throw RpcErrors.Of(ErrorCode.InvalidArgument, $"{name} has {matches.Length} options named '{key}', so it can't tell which one to set.");
            IOption option = matches[0];
            if (option.Transient)
                throw RpcErrors.Of(ErrorCode.InvalidArgument, $"'{key}' is a transient option of {name}: it resets on every start, so it can't be set.");
            string stored = Parse(option, value)
                ?? throw RpcErrors.Of(ErrorCode.InvalidArgument, $"'{value}' isn't a valid {TypeName(option.Type)} for '{key}'{ChoicesText(option.Type)}.");
            checkedValues.Add((option, stored));
        }
        foreach ((IOption option, string value) in checkedValues)
            config.OptionValues[option] = value;
        config.Save();
    }

    /// <summary>The value as Core stores it, or null when it isn't one of the option's type.</summary>
    private static string? Parse(IOption option, string value)
    {
        Type type = option.Type;
        if (type.IsEnum)
            return Choices(type).FirstOrDefault(c => string.Equals(c, value.Replace('_', ' ').Trim(), StringComparison.OrdinalIgnoreCase));
        if (type == typeof(string))
            return value;
        try
        {
            // Core reads values back with the current culture.
            return Convert.ToString(Convert.ChangeType(value.Trim(), type, CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Every option with its key: its name, or <c>&lt;group&gt;:&lt;name&gt;</c> in a group, as Core's options storage names it.</summary>
    private static IEnumerable<(string Key, IOption Option)> Options(IScriptOptionContainer config) =>
        config.Options.Select(o => (o.Name, o))
            .Concat(config.MultipleOptions.SelectMany(group => group.Value.Select(o => ($"{group.Key}:{o.Name}", o))));

    /// <summary>
    /// Whether the option only shows text in Core's options window, as the "Mode Explanation" entries every merge shop shares do: a blank
    /// name, so nothing reads it.
    /// </summary>
    private static bool IsText(IOption option) => string.IsNullOrWhiteSpace(option.Name);

    private static ScriptOptionDto Describe(IScriptOptionContainer config, string key, IOption option)
    {
        string defaultValue = Display(option, option.DefaultValue?.ToString() ?? "");
        string value = config.OptionValues.TryGetValue(option, out string? stored) ? Display(option, stored) : defaultValue;
        return new ScriptOptionDto(
            key, option.Category, option.Name, string.IsNullOrEmpty(option.DisplayName) ? option.Name : option.DisplayName,
            string.IsNullOrWhiteSpace(option.Description) ? null : option.Description, TypeName(option.Type), value, defaultValue,
            option.Type.IsEnum ? Choices(option.Type) : null, option.Transient, IsText(option));
    }

    /// <summary>Enum values show with spaces for underscores, as Core's options window shows and stores them.</summary>
    private static string Display(IOption option, string value) => option.Type.IsEnum ? value.Replace('_', ' ') : value;

    private static List<string> Choices(Type type) => Enum.GetNames(type).Select(n => n.Replace('_', ' ')).ToList();

    private static string ChoicesText(Type type) => type.IsEnum ? $"; choose one of {string.Join(", ", Choices(type))}" : "";

    private static string TypeName(Type type) => type switch
    {
        _ when type.IsEnum => "enum",
        _ when type == typeof(bool) => "bool",
        _ when type == typeof(string) || type == typeof(char) => "string",
        _ when type == typeof(float) || type == typeof(double) || type == typeof(decimal) => "number",
        _ when type.IsPrimitive => "int",
        _ => type.Name,
    };
}
