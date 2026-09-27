using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Services;

namespace Skua.App.Engine;

/// <summary>
/// Core's settings, in <c>&lt;SkuaDIR&gt;/Skua.settings.json</c> as for the Windows client. The Engine reads the file once, when it starts,
/// except for <see cref="AccountSetting.Key"/> and <see cref="ScriptSourceSetting.Key"/>, which <c>skua account</c> and <c>skua scripts source</c>
/// change while the Engine runs: they are read afresh whenever they are read, and before any change is saved, so a save never puts back an old
/// value.
/// </summary>
internal sealed class EngineSettingsService : ISettingsService
{
    private readonly UnifiedSettingsService _settings = new();

    public EngineSettingsService()
    {
        _settings.Initialize(AppRole.Client);
    }

    public T? Get<T>(string key)
    {
        if (key == AccountSetting.Key)
            ReloadAccountService();
        return _settings.Get<T>(key);
    }

    public T Get<T>(string key, T defaultValue)
    {
        if (key == AccountSetting.Key)
            ReloadAccountService();
        return _settings.Get(key, defaultValue);
    }

    public void Set<T>(string key, T value)
    {
        ReloadAccountService();
        ReloadScriptSource();
        _settings.Set(key, value);
    }

    private void ReloadAccountService() => _settings.GetClient().TestAccountService = AccountSetting.Read(ClientFileSources.SkuaDIR);

    /// <summary>Null while the setting is unset, so a save leaves it out and the Engine's default applies.</summary>
    private void ReloadScriptSource() =>
        _settings.GetShared().ScriptSource = ScriptSourceSetting.Read(ClientFileSources.SkuaDIR)?.ToCore();

    public void Initialize(AppRole role) => _settings.Initialize(role);

    public SharedSettings GetShared()
    {
        ReloadScriptSource();
        return _settings.GetShared();
    }

    public ClientSettings GetClient() => _settings.GetClient();

    public ManagerSettings GetManager() => _settings.GetManager();

    public void SetApplicationVersion()
    {
        ReloadAccountService();
        ReloadScriptSource();
        _settings.SetApplicationVersion();
    }
}
