using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skua.MacOS.Services;

namespace Skua.Avalonia;

/// <summary>
/// A sheet over the main window with the oldest pending Question: its caption, its text, a button per choice and a countdown to its timeout.
/// It covers the window's content while a Question is pending, and closes once the Question is answered, from here or anywhere else.
/// </summary>
public sealed class QuestionSheet : Panel
{
    private readonly ScriptDialogsViewModel _model;
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private Question? _shown;

    public QuestionSheet(ScriptDialogsViewModel model)
    {
        _model = model;
        IsVisible = false;
        // The dimmed window behind it takes no clicks, as behind a macOS sheet.
        Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));

        CaptionText = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        SourceText = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        MessageText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        CountdownText = new TextBlock { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };

        DockPanel footer = new();
        DockPanel.SetDock(Choices, Dock.Right);
        footer.Children.Add(Choices);
        footer.Children.Add(CountdownText);
        Children.Add(new Border
        {
            Width = 460,
            MaxWidth = 460,
            Margin = new Thickness(16, 0, 16, 16),
            Padding = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            BoxShadow = BoxShadows.Parse("0 8 24 0 #80000000"),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new StackPanel { Spacing = 2, Children = { CaptionText, SourceText } },
                    new ScrollViewer { MaxHeight = 240, Content = MessageText },
                    footer,
                },
            },
        });
        _countdown.Tick += (_, _) => UpdateCountdown();
    }

    public TextBlock CaptionText { get; }

    /// <summary>Which Script raised it, and how many more wait behind it.</summary>
    public TextBlock SourceText { get; }

    public SelectableTextBlock MessageText { get; }

    public TextBlock CountdownText { get; }

    /// <summary>A button per choice, in the Question's order.</summary>
    public StackPanel Choices { get; }

    /// <summary>The Question on the sheet, or null while it is hidden.</summary>
    public Question? Shown => _shown;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _model.PropertyChanged += OnModelChanged;
        _model.Questions.CollectionChanged += OnQuestionsChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _model.PropertyChanged -= OnModelChanged;
        _model.Questions.CollectionChanged -= OnQuestionsChanged;
        _countdown.Stop();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScriptDialogsViewModel.Current))
            Update();
    }

    private void OnQuestionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateSource();

    private void Update()
    {
        Question? question = _model.Current;
        if (question?.Id == _shown?.Id)
            return;
        _shown = question;
        IsVisible = question is not null;
        Choices.Children.Clear();
        if (question is null)
        {
            _countdown.Stop();
            return;
        }

        CaptionText.Text = question.Caption.Length > 0 ? question.Caption : "Question";
        MessageText.Text = question.Text;
        for (int i = 0; i < question.Choices.Count; i++)
        {
            int choice = i;
            Button button = new() { Content = question.Choices[i], MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
            // A click after another answer is ignored: the sheet moves on once that answer arrives.
            button.Click += (_, _) => _model.Answer(question, choice);
            Choices.Children.Add(button);
        }
        UpdateSource();
        UpdateCountdown();
        _countdown.Start();
    }

    private void UpdateSource()
    {
        if (_shown is not { } question)
            return;
        string from = question.Script is { } script ? $"From {script}" : "From Skua";
        int more = _model.Questions.Count - 1;
        SourceText.Text = more > 0 ? $"{from} · {more} more waiting" : from;
    }

    private void UpdateCountdown()
    {
        if (_shown is not { } question)
            return;
        TimeSpan left = question.ExpiresAt - DateTimeOffset.UtcNow;
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;
        string time = left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left:mm\\:ss}" : $"{(int)left.TotalMinutes}:{left:ss}";
        CountdownText.Text = $"No answer in {time} cancels it.";
    }
}
