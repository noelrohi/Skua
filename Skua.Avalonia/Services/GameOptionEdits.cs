using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using Skua.Core.Interfaces;

namespace Skua.Avalonia.Services;

/// <summary>
/// Saves each game option the developer changes in the Game Options panel as it changes, in <c>UserOptions</c> where Core's Save keeps them,
/// so it survives a restart. Only the options an edit changed are saved, so what a running Script set stays unsaved, as on Windows.
/// </summary>
/// <remarks>
/// An edit's changes are the options that change on its thread while it runs, such as a check box's option and the one it clears
/// (Accept All Drops clears Reject All Drops). The panel's Save still saves every option, and Reset and Default save nothing, as on Windows.
/// </remarks>
public sealed class GameOptionEdits
{
    /// <summary>The setting Core's <c>ScriptOption.Save</c> writes and reads at start: one <c>name=value</c> line per option.</summary>
    public const string SettingKey = "UserOptions";

    private readonly IScriptOption _options;
    private readonly ISettingsService _settings;

    public GameOptionEdits(IScriptOption options, ISettingsService settings)
    {
        _options = options;
        _settings = settings;
    }

    /// <summary>Runs the developer's edit, then saves the options it changed.</summary>
    public void Edit(Action edit)
    {
        List<string> changed = [];
        int thread = Environment.CurrentManagedThreadId;

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread && e.PropertyName is { } name && !changed.Contains(name))
                changed.Add(name);
        }

        if (_options is not INotifyPropertyChanged source)
        {
            edit();
            return;
        }
        source.PropertyChanged += OnChanged;
        try
        {
            edit();
        }
        finally
        {
            source.PropertyChanged -= OnChanged;
        }
        Save(changed);
    }

    private void Save(IReadOnlyList<string> names)
    {
        List<PropertyInfo> saved = names
            .Select(n => _options.GetType().GetProperty(n))
            .OfType<PropertyInfo>()
            .Where(p => p.Name != nameof(IOptionDictionary.OptionDictionary) && p.CanRead && p.CanWrite)
            .ToList();
        if (saved.Count == 0)
            return;

        StringCollection lines = [];
        if (_settings.Get<StringCollection>(SettingKey) is { } stored)
        {
            foreach (string? line in stored)
            {
                if (!string.IsNullOrEmpty(line) && !saved.Any(p => line.StartsWith(p.Name + "=", StringComparison.Ordinal)))
                    lines.Add(line);
            }
        }
        // Formatted as ScriptOption.Save formats them, which is how it reads them back.
        foreach (PropertyInfo property in saved)
            lines.Add($"{property.Name}={property.GetValue(_options)}");
        _settings.Set(SettingKey, lines);
    }
}
