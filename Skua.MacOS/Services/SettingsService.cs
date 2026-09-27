using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Services;

namespace Skua.MacOS.Services;

/// <summary>Core's settings file in the Skua data folder, with the client role; the same as the Windows app's.</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly UnifiedSettingsService _unifiedService = new();

    public SettingsService()
    {
        _unifiedService.Initialize(AppRole.Client);
    }

    public T? Get<T>(string key) => _unifiedService.Get<T>(key);

    public T Get<T>(string key, T defaultValue) => _unifiedService.Get(key, defaultValue);

    public void Set<T>(string key, T value) => _unifiedService.Set(key, value);

    public void Initialize(AppRole role) => _unifiedService.Initialize(role);

    public SharedSettings GetShared() => _unifiedService.GetShared();

    public ClientSettings GetClient() => _unifiedService.GetClient();

    public ManagerSettings GetManager() => _unifiedService.GetManager();

    public void SetApplicationVersion() => _unifiedService.SetApplicationVersion();
}
