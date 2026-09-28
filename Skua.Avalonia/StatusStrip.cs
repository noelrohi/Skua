using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Skua.Avalonia;

/// <summary>
/// The main window's status strip: the Engine Name and its host, the game state, and while logged in the account, server, map and cell
/// and level, and the running Script.
/// </summary>
public sealed class StatusStrip : Border
{
    private readonly StatusViewModel _model;
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    public StatusStrip(StatusViewModel model)
    {
        _model = model;
        Padding = new Thickness(8, 3);
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        _text.FontSize = 12;
        Child = _text;
        Update();
    }

    /// <summary>What the strip shows, as one line.</summary>
    public string Text => _text.Text ?? "";

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _model.PropertyChanged += OnModelChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        List<string> parts = [$"Engine {_model.EngineName} ({_model.Host})", _model.GameStateText];
        if (_model.Account is { } account)
            parts.Add(account);
        if (_model.Server is { } server)
            parts.Add(server);
        if (_model.Map is { } map)
            parts.Add(_model.Cell is { Length: > 0 } cell ? $"{map}, {cell}" : map);
        if (_model.Level is { } level)
            parts.Add($"level {level}");
        parts.Add(_model.Script is { } script ? $"Script: {script}" : "no Script");
        _text.Text = string.Join("  ·  ", parts);
    }
}
