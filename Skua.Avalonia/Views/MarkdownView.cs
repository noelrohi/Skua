using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Skua.Avalonia.Views;

/// <summary>
/// Shows a Markdown page, as <c>Skua.WPF</c>'s About and Change Logs do with MdXaml: headings, paragraphs, lists, code blocks, rules, bold,
/// italics, inline code and links. A link runs <see cref="LinkCommand"/> with its target, as MdXaml's hyperlinks run the view model's
/// <c>NavigateCommand</c>. HTML tags are dropped and an image shows its alternative text.
/// </summary>
public sealed partial class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public static readonly StyledProperty<ICommand?> LinkCommandProperty = AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(LinkCommand));

    public MarkdownView()
    {
        Spacing = 6;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public ICommand? LinkCommand
    {
        get => GetValue(LinkCommandProperty);
        set => SetValue(LinkCommandProperty, value);
    }

    /// <summary>The link buttons the page shows, in order.</summary>
    public List<HyperlinkButton> Links { get; } = [];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty || change.Property == LinkCommandProperty)
            Render();
    }

    private void Render()
    {
        Children.Clear();
        Links.Clear();
        List<string> paragraph = [];
        StringBuilder? code = null;
        foreach (string raw in (Markdown ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                Flush(paragraph);
                if (code is null)
                {
                    code = new();
                }
                else
                {
                    Children.Add(CodeBlock(code.ToString().TrimEnd('\n')));
                    code = null;
                }
                continue;
            }
            if (code is not null)
            {
                code.Append(raw).Append('\n');
                continue;
            }

            string line = HtmlTag().Replace(raw, "").Trim();
            if (line.Length == 0)
            {
                Flush(paragraph);
            }
            else if (Heading().Match(line) is { Success: true } heading)
            {
                Flush(paragraph);
                int level = heading.Groups[1].Length;
                Children.Add(Text(heading.Groups[2].Value, FontWeight.Bold, level switch { 1 => 24, 2 => 20, 3 => 17, _ => 15 }));
            }
            else if (Rule().IsMatch(line))
            {
                Flush(paragraph);
                Children.Add(new Separator());
            }
            else if (ListItem().Match(line) is { Success: true } item)
            {
                Flush(paragraph);
                string indent = new(' ', Math.Min(raw.Length - raw.TrimStart().Length, 8));
                string bullet = item.Groups[1].Value is "-" or "*" or "+" ? "•" : item.Groups[1].Value;
                Children.Add(Text($"{indent}{bullet} {item.Groups[2].Value}", FontWeight.Normal, 14));
            }
            else
            {
                paragraph.Add(line);
            }
        }
        Flush(paragraph);
        if (code is not null)
            Children.Add(CodeBlock(code.ToString().TrimEnd('\n')));
    }

    private void Flush(List<string> paragraph)
    {
        if (paragraph.Count == 0)
            return;
        Children.Add(Text(string.Join(" ", paragraph), FontWeight.Normal, 14));
        paragraph.Clear();
    }

    private TextBlock Text(string markdown, FontWeight weight, double size)
    {
        TextBlock text = new() { TextWrapping = TextWrapping.Wrap, FontWeight = weight, FontSize = size };
        text.Inlines!.AddRange(Inlines(markdown));
        return text;
    }

    private static Border CodeBlock(string code) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8),
        Child = new SelectableTextBlock { Text = code, FontFamily = new FontFamily("Menlo, monospace"), TextWrapping = TextWrapping.Wrap },
    };

    /// <summary>The inlines of one block's text: the first span that opens earliest wins, and the text inside bold and italics is parsed again.</summary>
    private List<Inline> Inlines(string markdown)
    {
        List<Inline> inlines = [];
        int at = 0;
        foreach (Match match in InlineSpan().Matches(markdown))
        {
            if (match.Index > at)
                inlines.Add(new Run(markdown[at..match.Index]));
            at = match.Index + match.Length;
            if (match.Groups["bolditalic"].Success)
                inlines.Add(Styled(new Bold(), match.Groups["bolditalic"].Value, italic: true));
            else if (match.Groups["bold"].Success)
                inlines.Add(Styled(new Bold(), match.Groups["bold"].Value));
            else if (match.Groups["italic"].Success)
                inlines.Add(Styled(new Italic(), match.Groups["italic"].Value));
            else if (match.Groups["code"].Success)
                inlines.Add(new Run(match.Groups["code"].Value) { FontFamily = new FontFamily("Menlo, monospace") });
            else if (match.Groups["image"].Success)
                inlines.Add(new Run(match.Groups["alt"].Value));
            else
                inlines.Add(Link(match.Groups["text"].Value, match.Groups["url"].Value));
        }
        if (at < markdown.Length)
            inlines.Add(new Run(markdown[at..]));
        return inlines;
    }

    private Span Styled(Span span, string markdown, bool italic = false)
    {
        span.Inlines.AddRange(Inlines(markdown));
        if (italic)
            span.FontStyle = FontStyle.Italic;
        return span;
    }

    private InlineUIContainer Link(string text, string url)
    {
        HyperlinkButton link = new()
        {
            Content = text.Length > 0 ? text : url,
            Command = LinkCommand,
            CommandParameter = url,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        ToolTip.SetTip(link, url);
        Links.Add(link);
        return new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom };
    }

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(?:-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\*\*\*(?<bolditalic>.+?)\*\*\*|\*\*(?<bold>.+?)\*\*|(?<![\w*])\*(?<italic>[^*\s][^*]*?)\*(?![\w*])|`(?<code>[^`]+)`|(?<image>!)\[(?<alt>[^\]]*)\]\([^)]*\)|\[(?<text>[^\]]*)\]\((?<url>[^)\s]+)[^)]*\)")]
    private static partial Regex InlineSpan();
}
