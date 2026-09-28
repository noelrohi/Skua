using System.Text.Json;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Avalonia.Services;

/// <summary>
/// The Engine's settings as the Mac App's view models see them: the GitHub token goes to Keychain (<see cref="GitHubToken"/>) instead of
/// <c>Skua.settings.json</c>, and the app's own settings, which Core's models have no property for, are kept in the client section's extra
/// data. Everything else is the Engine's.
/// </summary>
public sealed class AppSettingsService : ISettingsService
{
    /// <summary>The names of the windows that stay on top (<see cref="TopMost"/>).</summary>
    public const string TopMostKey = "MacTopMostWindows";

    private readonly ISettingsService _settings;
    private readonly GitHubToken _gitHub;

    public AppSettingsService(ISettingsService settings, GitHubToken gitHub)
    {
        _settings = settings;
        _gitHub = gitHub;
    }

    public T? Get<T>(string key) => key switch
    {
        GitHubToken.SettingKey => _gitHub.Token is T token ? token : default,
        TopMostKey => Extra<T>(key),
        _ => _settings.Get<T>(key),
    };

    public T Get<T>(string key, T defaultValue)
    {
        if (key is not (GitHubToken.SettingKey or TopMostKey))
            return _settings.Get(key, defaultValue);
        T? value = Get<T>(key);
        return value is null || value.Equals(default(T)) ? defaultValue : value;
    }

    public void Set<T>(string key, T value)
    {
        switch (key)
        {
            case GitHubToken.SettingKey:
                _gitHub.Save(value as string);
                break;
            case TopMostKey:
                (_settings.GetClient().ExtensionData ??= [])[key] = value!;
                // Saving a key Core has no property for saves the file as it is, the extra data with it.
                _settings.Set(key, value);
                break;
            default:
                _settings.Set(key, value);
                break;
        }
    }

    public void Initialize(AppRole role) => _settings.Initialize(role);

    public SharedSettings GetShared() => _settings.GetShared();

    public ClientSettings GetClient() => _settings.GetClient();

    public ManagerSettings GetManager() => _settings.GetManager();

    public void SetApplicationVersion() => _settings.SetApplicationVersion();

    /// <summary>A value of the client section's extra data: as saved in this run, or as read from the file; the default when it isn't a <typeparamref name="T"/>.</summary>
    private T? Extra<T>(string key)
    {
        switch (_settings.GetClient().ExtensionData?.GetValueOrDefault(key))
        {
            case T value:
                return value;
            case JsonElement json:
                try
                {
                    return json.Deserialize<T>();
                }
                catch (JsonException)
                {
                    return default;
                }
            default:
                return default;
        }
    }
}
