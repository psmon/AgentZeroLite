using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AgentZeroWpf.UI.Components;

public partial class MarkdownViewer : UserControl
{
    public static readonly DependencyProperty MarkdownTextProperty =
        DependencyProperty.Register(nameof(MarkdownText), typeof(string), typeof(MarkdownViewer),
            new PropertyMetadata(null, OnMarkdownTextChanged));

    public string? MarkdownText
    {
        get => (string?)GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    public MarkdownViewer()
    {
        InitializeComponent();
    }

    private static void OnMarkdownTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer viewer)
            viewer.RenderMarkdown(e.NewValue as string ?? "");
    }

    private void RenderMarkdown(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D4D4D4")!),
            Background = Brushes.Transparent,
            PagePadding = new Thickness(4),
        };

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        bool inCodeBlock = false;
        var codeLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            // Code block toggle
            if (line.TrimStart().StartsWith("```"))
            {
                if (inCodeBlock)
                {
                    doc.Blocks.Add(CreateCodeBlock(string.Join("\n", codeLines)));
                    codeLines.Clear();
                    inCodeBlock = false;
                }
                else
                {
                    inCodeBlock = true;
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeLines.Add(line);
                continue;
            }

            // Frontmatter (---) skip
            if (line.Trim() == "---") continue;

            // Empty line
            if (string.IsNullOrWhiteSpace(line))
            {
                doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 2, 0, 2) });
                continue;
            }

            // Headings
            if (line.StartsWith("### "))
            {
                doc.Blocks.Add(CreateHeading(line[4..], 3));
                continue;
            }
            if (line.StartsWith("## "))
            {
                doc.Blocks.Add(CreateHeading(line[3..], 2));
                continue;
            }
            if (line.StartsWith("# "))
            {
                doc.Blocks.Add(CreateHeading(line[2..], 1));
                continue;
            }

            // Bullet list
            if (line.TrimStart().StartsWith("- "))
            {
                int indent = line.Length - line.TrimStart().Length;
                string content = line.TrimStart()[2..];
                var para = new Paragraph
                {
                    Margin = new Thickness(indent * 4 + 8, 1, 0, 1),
                    TextIndent = -12,
                };
                para.Inlines.Add(new Run("  ") { Foreground = Brushes.Transparent });
                AddInlineMarkdown(para.Inlines, content);
                doc.Blocks.Add(para);
                continue;
            }

            // Regular paragraph
            var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            AddInlineMarkdown(p.Inlines, line);
            doc.Blocks.Add(p);
        }

        // Unclosed code block
        if (inCodeBlock && codeLines.Count > 0)
            doc.Blocks.Add(CreateCodeBlock(string.Join("\n", codeLines)));

        rtbContent.Document = doc;
    }

    private static Paragraph CreateHeading(string text, int level)
    {
        double size = level switch { 1 => 18, 2 => 15, _ => 13 };
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F3F3F3")!);

        var p = new Paragraph
        {
            Margin = new Thickness(0, level == 1 ? 8 : 6, 0, 4),
            FontSize = size,
            FontWeight = FontWeights.Bold,
            Foreground = brush,
        };
        p.Inlines.Add(new Run(text));
        return p;
    }

    private static Block CreateCodeBlock(string code)
    {
        var p = new Paragraph
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252526")!),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 4, 0, 4),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F3F3F3")!),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")!),
            BorderThickness = new Thickness(1),
        };
        p.Inlines.Add(new Run(code));
        return p;
    }

    private static void AddInlineMarkdown(InlineCollection inlines, string text)
    {
        // Process **bold**, `code`, and plain text
        var pattern = @"(\*\*(.+?)\*\*)|(`(.+?)`)";
        int lastIndex = 0;

        foreach (Match match in Regex.Matches(text, pattern))
        {
            // Add text before match
            if (match.Index > lastIndex)
                inlines.Add(new Run(text[lastIndex..match.Index]));

            if (match.Groups[2].Success)
            {
                // Bold
                inlines.Add(new Run(match.Groups[2].Value)
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")!),
                });
            }
            else if (match.Groups[4].Success)
            {
                // Inline code
                inlines.Add(new Run(match.Groups[4].Value)
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")!),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F3F3F3")!),
                    FontFamily = new FontFamily("Consolas"),
                });
            }

            lastIndex = match.Index + match.Length;
        }

        // Remaining text
        if (lastIndex < text.Length)
            inlines.Add(new Run(text[lastIndex..]));
    }
}
