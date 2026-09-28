using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Skua.Avalonia.Services;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models.GitHub;
using Skua.Engine;
using StreamJsonRpc;

namespace Skua.Avalonia;

/// <summary>
/// The checks the Windows app runs as it starts (<c>Application_Startup</c>), which the Mac App runs once its main window shows: the Scripts,
/// the AdvanceSkill sets and the junk items against the Script Source, each as its Application Options say, and the quest data every time.
/// Change Logs opens on the first start, as the Windows Manager opens it. <c>skua-engine</c> runs none of them.
/// </summary>
/// <remarks>
/// The checks run off the UI thread. Their Questions and Notices are the app's own Script Dialogs, so they show as the sheet and in Notices, and
/// never block a thread. The Scripts update through the Engine's <c>scripts_update</c>, which refuses while a Script runs and records the update
/// in the Script history.
/// </remarks>
public sealed class StartUpChecks
{
    private const string ScriptsCaption = "Script Update";
    private const string SkillSetsCaption = "AdvanceSkill Sets Update";
    private const string JunkItemsCaption = "Junk Items Update";

    private readonly IServiceProvider _services;
    private readonly ISettingsService _settings;
    private readonly IGetScriptsService _getScripts;
    private readonly EngineScripts _scripts;
    private readonly AvaloniaDialogService _dialogs;
    private readonly ILogService _log;

    public StartUpChecks(IServiceProvider services, ISettingsService settings, IGetScriptsService getScripts, EngineScripts scripts,
        AvaloniaDialogService dialogs, ILogService log)
    {
        _services = services;
        _settings = settings;
        _getScripts = getScripts;
        _scripts = scripts;
        _dialogs = dialogs;
        _log = log;
    }

    /// <summary>Opens Change Logs on the first start, then runs the checks off the UI thread; completes when they have all finished.</summary>
    /// <remarks>Call it on the UI thread.</remarks>
    public Task RunAsync()
    {
        ShowChangeLogsOnFirstStart();
        return Task.Run(() => Task.WhenAll(CheckScriptsAsync(), CheckSkillSetsAsync(), CheckJunkItemsAsync(), RefreshQuestDataAsync()));
    }

    /// <summary>Opens Change Logs unless it has opened before, with the Windows Manager's setting.</summary>
    public void ShowChangeLogsOnFirstStart()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_settings.Get<bool>("ChangeLogActivated"))
            return;
        AppMenu.ShowChangeLogs(_services, _services.GetRequiredService<AvaloniaWindowService>());
        _settings.Set("ChangeLogActivated", true);
    }

    /// <summary>
    /// When any Script in the Script Source is missing or differs from the one on disk, updates the Scripts, asking first unless auto update is
    /// on, and says how many it downloaded. Skipped while a Script runs.
    /// </summary>
    public async Task CheckScriptsAsync()
    {
        if (!_settings.Get<bool>("CheckBotScriptsUpdates"))
            return;
        if (_scripts.ScriptManager.ScriptRunning)
        {
            _log.DebugLog("Start-up check: skipped the Scripts check, since a Script is running.");
            return;
        }

        List<ScriptInfo> scripts;
        try
        {
            scripts = await _getScripts.FetchScriptsAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            _log.DebugLog($"Start-up check: the Script Source {_getScripts.Source} couldn't be read: {e.Message}");
            return;
        }
        if (!scripts.Any(s => !s.Downloaded || s.Outdated))
            return;

        bool auto = _settings.Get<bool>("AutoUpdateBotScripts");
        if (!auto && await _dialogs.AskAsync("Would you like to update your scripts?", ScriptsCaption) != true)
            return;

        ScriptsUpdateResult result;
        try
        {
            result = await _scripts.UpdateAsync();
        }
        catch (LocalRpcException e) when (e.ErrorCode == (int)ErrorCode.ScriptRunning)
        {
            _log.DebugLog($"Start-up check: skipped the Scripts update: {e.Message}");
            return;
        }
        catch (LocalRpcException e)
        {
            _dialogs.Notify($"The Scripts couldn't be updated: {e.Message}", ScriptsCaption);
            return;
        }
        if (result.Downloaded > 0)
            _dialogs.Notify($"Downloaded {result.Downloaded} script(s).\nYou can disable auto script updates in Options > Application.", ScriptsCaption);
    }

    /// <summary>When the Script Source's AdvanceSkill sets differ from the file, downloads them, asking first unless auto update is on, and syncs the skill sets.</summary>
    public Task CheckSkillSetsAsync() => CheckFileAsync(
        "CheckAdvanceSkillSetsUpdates", "AutoUpdateAdvanceSkillSetsUpdates", _getScripts.CheckAdvanceSkillSetsUpdates, _getScripts.UpdateSkillSetsFile,
        () => _services.GetRequiredService<IAdvancedSkillContainer>().SyncSkills(),
        SkillSetsCaption, "Would you like to update your AdvanceSkill Sets?", "AdvanceSkill Sets has been updated.", "AdvanceSkill Sets update error.",
        "AdvanceSkill Sets updates");

    /// <summary>When the Script Source's junk items differ from the file, downloads them, asking first unless auto update is on, and reloads the junk list.</summary>
    public Task CheckJunkItemsAsync() => CheckFileAsync(
        "CheckJunkItemsUpdates", "AutoUpdateJunkItems", _getScripts.CheckJunkItemsUpdates, _getScripts.UpdateJunkItemsFile,
        () => _services.GetRequiredService<IJunkService>().Load(),
        JunkItemsCaption, "Would you like to update your Junk Items list?", "Junk Items list has been updated.", "Junk Items update error.",
        "Junk Items updates");

    /// <summary>Downloads the quest data from the Script Source, as every start does on Windows; a failure is only logged.</summary>
    public async Task RefreshQuestDataAsync()
    {
        if (!await _getScripts.UpdateQuestDataFile())
            _log.DebugLog($"Start-up check: the quest data couldn't be read from the Script Source {_getScripts.Source}.");
    }

    /// <param name="check">The size of the Script Source's file when it differs from the one on disk, 0 when it doesn't, or -1 when it can't be read.</param>
    /// <param name="loaded">Makes the app use the new file.</param>
    private async Task CheckFileAsync(string checkKey, string autoKey, Func<Task<long>> check, Func<Task<bool>> update, Action loaded,
        string caption, string question, string updated, string failed, string what)
    {
        if (!_settings.Get<bool>(checkKey) || await check() <= 0)
            return;

        bool auto = _settings.Get<bool>(autoKey);
        if (!auto && await _dialogs.AskAsync(question, caption) != true)
            return;

        if (!await update())
        {
            _dialogs.Notify($"{failed}\nYou can disable auto {what} in Options > Application.", caption);
            return;
        }
        loaded();
        _dialogs.Notify($"{updated}\nYou can {(auto ? "disable" : "enable")} auto {what} in Options > Application.", caption);
    }
}
