using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Services;

namespace Skua.App.Engine;

/// <summary>
/// Core's settings, in <c>&lt;SkuaDIR&gt;/Skua.settings.json</c> as for the Windows client. The Engine reads the file once, when it starts,
/// except for <see cref="AccountSetting.Key"/>, which <c>skua account</c> changes while the Engine runs: it is read afresh whenever it is read,
/// and before any change is saved, so a save never puts back an old value.
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
        _settings.Set(key, value);
    }

    private void ReloadAccountService() => _settings.GetClient().TestAccountService = AccountSetting.Read(ClientFileSources.SkuaDIR);

    public void Initialize(AppRole role) => _settings.Initialize(role);

    public SharedSettings GetShared() => _settings.GetShared();

    public ClientSettings GetClient() => _settings.GetClient();

    public ManagerSettings GetManager() => _settings.GetManager();

    public void SetApplicationVersion() => _settings.SetApplicationVersion();
}
