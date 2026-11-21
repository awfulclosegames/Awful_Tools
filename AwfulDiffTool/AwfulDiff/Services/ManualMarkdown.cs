using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AwfulDiff.Services;

/// <summary>One navigable heading in the rendered manual (level 2 = top section, level 3 = subsection).</summary>
public sealed class ManualSection
{
    public required string Title { get; init; }
    public required int Level { get; init; }
    public required UIElement Element { get; init; }
}

/// <summary>The parsed manual: the rendered content root plus its navigable sections.</summary>
public sealed class ManualContent
{
    public required UIElement Root { get; init; }
    public required List<ManualSection> Sections { get; init; }
}

/// <summary>
/// Lightweight Markdown → WPF renderer for the bundled user manual.
/// Emits ordinary visual elements (TextBlock / Grid / Border) rather than a FlowDocument so
/// every section heading is a real UIElement we can position with TranslatePoint for index
/// navigation. Supports exactly the subset the manual uses: headings, paragraphs, bullet and
/// numbered lists, tables, fenced code blocks, block quotes, horizontal rules, and inline
/// **bold** / *italic* / `code` / [text](#anchor). No third-party markdown dependency.
/// </summary>
public static class ManualMarkdown
{
    private static readonly FontFamily CodeFont = new("Consolas, Cascadia Code, monospace");

    /// <summary>Inline markup, tried in order: **bold**, *italic*, `code`, [text](target).</summary>
    private static readonly Regex InlinePattern = new(
        @"(\*\*.+?\*\*)|(\*[^*]+?\*)|(`[^`]+?`)|(\[[^\]]+\]\([^)]+\))",
        RegexOptions.Compiled);

    /// <summary>Read the embedded UserManual.md from this assembly.</summary>
    public static string LoadEmbeddedManual()
    {
        var asm = typeof(ManualMarkdown).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("UserManual.md", StringComparison.OrdinalIgnoreCase));
        if (name == null)
            return "# User Manual\n\n(The manual resource is missing from this build.)";

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Render the markdown into a visual tree. `onAnchorClick` (optional) is invoked with the
    /// anchor id when an in-document [text](#anchor) link is clicked.
    /// </summary>
    public static ManualContent Parse(string markdown, Action<string>? onAnchorClick = null)
    {
        var root = new StackPanel { Margin = new Thickness(16, 8, 24, 16) };
        var sections = new List<ManualSection>();

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) { i++; continue; }

            // ── Fenced code block ──
            if (line.StartsWith("```"))
            {
                var sb = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```"))
                { sb.AppendLine(lines[i]); i++; }
                i++; // consume the closing fence
                root.Children.Add(CodeBlock(sb.ToString().TrimEnd('\n')));
                continue;
            }

            // ── Heading ──
            if (line.StartsWith('#'))
            {
                int level = 0;
                while (level < line.Length && line[level] == '#') level++;
                var text = line.Substring(level).Trim();
                var heading = MakeHeading(text, level);
                root.Children.Add(heading);
                if (level is 2 or 3)
                    sections.Add(new ManualSection { Title = text, Level = level, Element = heading });
                i++;
                continue;
            }

            // ── Horizontal rule ──
            if (Regex.IsMatch(line, @"^-{3,}$")) { root.Children.Add(MakeHr()); i++; continue; }

            // ── Table: header row followed by a |---| separator row ──
            if (line.StartsWith('|') && i + 1 < lines.Length &&
                Regex.IsMatch(lines[i + 1].Trim(), @"^\|?[\s:|-]+\|?$") && lines[i + 1].Contains('-'))
            {
                var header = SplitRow(line);
                i += 2; // skip header + separator
                var rows = new List<string[]>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    rows.Add(SplitRow(lines[i].Trim()));
                    i++;
                }
                root.Children.Add(MakeTable(header, rows, onAnchorClick));
                continue;
            }

            // ── Block quote ──
            if (line.StartsWith('>'))
            {
                var sb = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    var t = lines[i].TrimStart();
                    sb.AppendLine(t.TrimStart('>').Trim());
                    i++;
                }
                root.Children.Add(MakeBlockQuote(sb.ToString(), onAnchorClick));
                continue;
            }

            // ── Bullet list: one two-column grid per item so wrapped lines hang under the text ──
            if (Regex.IsMatch(line, @"^-\s+"))
            {
                var list = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
                while (i < lines.Length && Regex.IsMatch(lines[i].Trim(), @"^-\s+"))
                {
                    var item = new Grid();
                    item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var bullet = new TextBlock
                    {
                        Text = "•",
                        Margin = new Thickness(0, 0, 8, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    bullet.SetResourceReference(TextElement.ForegroundProperty, "TextSecondary");
                    Grid.SetColumn(bullet, 0);
                    item.Children.Add(bullet);

                    var t = new TextBlock { TextWrapping = TextWrapping.Wrap };
                    AppendInline(t.Inlines, Regex.Replace(lines[i].Trim(), @"^-\s+", ""), onAnchorClick);
                    Grid.SetColumn(t, 1);
                    item.Children.Add(t);

                    list.Children.Add(item);
                    i++;
                }
                root.Children.Add(list);
                continue;
            }

            // ── Numbered list: keep the literal numbers as indented paragraphs ──
            if (Regex.IsMatch(line, @"^\d+\.\s+"))
            {
                while (i < lines.Length && Regex.IsMatch(lines[i].Trim(), @"^\d+\.\s+"))
                {
                    var t = new TextBlock { Margin = new Thickness(16, 0, 0, 2), TextWrapping = TextWrapping.Wrap };
                    AppendInline(t.Inlines, lines[i].Trim(), onAnchorClick);
                    root.Children.Add(t);
                    i++;
                }
                continue;
            }

            // ── Paragraph: merge consecutive plain text lines ──
            // The first line always joins the paragraph (it reached here without matching any other
            // block — e.g. a stray '|' line that isn't a table header), so the outer loop is
            // guaranteed to advance even for input no branch above claimed.
            var psb = new StringBuilder();
            bool firstLine = true;
            while (i < lines.Length)
            {
                var t = lines[i].Trim();
                if (!firstLine && (t.Length == 0 || t.StartsWith('#') || t.StartsWith("```") || t.StartsWith('|') ||
                    t.StartsWith('>') || Regex.IsMatch(t, @"^(-|\d+\.)\s+")))
                    break;
                psb.AppendLine(t);
                i++;
                firstLine = false;
            }
            var para = new TextBlock { Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
            AppendInline(para.Inlines, psb.ToString().TrimEnd(), onAnchorClick);
            root.Children.Add(para);
        }

        return new ManualContent { Root = root, Sections = sections };
    }

    /// <summary>GitHub-style anchor slug for a heading ("5. Command Line & VCS Integration" → "5-command-line--vcs-integration").</summary>
    public static string Slug(string heading)
    {
        var sb = new StringBuilder(heading.Length);
        foreach (var ch in heading.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == ' ')
                sb.Append(ch);
        return sb.ToString().Trim().Replace(' ', '-');
    }

    // ── Block builders ────────────────────────────────────────────────────────

    private static TextBlock MakeHeading(string text, int level)
    {
        var t = new TextBlock
        {
            Margin = level <= 2 ? new Thickness(0, 18, 0, 6) : new Thickness(0, 12, 0, 4),
            FontSize = level switch { 1 => 20, 2 => 16, _ => 13.5 },
            FontWeight = level == 1 ? FontWeights.Bold : FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        if (level <= 2)
            t.SetResourceReference(TextElement.ForegroundProperty, "TextAccent");
        AppendInline(t.Inlines, text, null);
        return t;
    }

    private static Border MakeHr()
    {
        var b = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
        b.SetResourceReference(Border.BackgroundProperty, "BorderSubtle");
        return b;
    }

    private static TextBlock CodeBlock(string code)
    {
        var t = new TextBlock
        {
            FontFamily = CodeFont,
            FontSize = 12.5,
            Margin = new Thickness(0, 6, 0, 6),
            Padding = new Thickness(8, 6, 8, 6),
            TextWrapping = TextWrapping.Wrap,
        };
        t.SetResourceReference(TextBlock.BackgroundProperty, "BgPanel");
        var first = true;
        foreach (var l in code.Split('\n'))
        {
            if (!first) t.Inlines.Add(new LineBreak());
            t.Inlines.Add(new Run(l));
            first = false;
        }
        return t;
    }

    private static Border MakeBlockQuote(string text, Action<string>? onAnchorClick)
    {
        var inner = new TextBlock
        {
            Margin = new Thickness(8, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        };
        inner.SetResourceReference(TextElement.ForegroundProperty, "TextSecondary");
        AppendInline(inner.Inlines, text, onAnchorClick);

        var b = new Border
        {
            Margin = new Thickness(0, 6, 0, 6),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Child = inner,
        };
        b.SetResourceReference(Border.BorderBrushProperty, "BorderGlow");
        return b;
    }

    private static Grid MakeTable(string[] header, List<string[]> rows, Action<string>? onAnchorClick)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };

        for (int c = 0; c < header.Length; c++)
            // First column hugs its content; the rest share the remaining width.
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star)
            });

        int r = 0;
        grid.RowDefinitions.Add(new RowDefinition());
        AddTableRow(grid, header, isHeader: true, onAnchorClick, row: r++);
        foreach (var row in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            AddTableRow(grid, row, isHeader: false, onAnchorClick, row: r++);
        }

        return grid;
    }

    private static void AddTableRow(Grid grid, string[] cells, bool isHeader, Action<string>? onAnchorClick, int row)
    {
        for (int c = 0; c < cells.Length; c++)
        {
            var cell = new Border { Padding = new Thickness(6, 4, 6, 4) };
            if (isHeader)
            {
                cell.BorderThickness = new Thickness(0, 0, 0, 1);
                cell.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");
            }

            var t = new TextBlock { TextWrapping = TextWrapping.Wrap };
            if (isHeader) t.FontWeight = FontWeights.SemiBold;
            AppendInline(t.Inlines, cells[c], onAnchorClick);
            cell.Child = t;

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }
    }

    private static string[] SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];

        // A '|' inside a backtick code span is literal cell content (e.g. `[||] Side`), not a
        // separator — tracking backtick parity keeps such rows from shattering into extra cells
        // that WPF then stacks on top of the first column.
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool inCode = false;
        foreach (var ch in t)
        {
            if (ch == '`') { inCode = !inCode; sb.Append(ch); continue; }
            if (ch == '|' && !inCode)
            {
                cells.Add(sb.ToString().Trim());
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        cells.Add(sb.ToString().Trim());
        return cells.ToArray();
    }

    // ── Inline parsing ────────────────────────────────────────────────────────

    /// <summary>Append `text` to `inlines`, rendering **bold**, *italic*, `code` and [t](#a) links.</summary>
    private static void AppendInline(InlineCollection inlines, string text, Action<string>? onAnchorClick)
    {
        int pos = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > pos)
                inlines.Add(new Run(text.Substring(pos, m.Index - pos)));

            if (m.Groups[1].Success) // **bold** — span so nested inline markup still works
            {
                var span = new Span { FontWeight = FontWeights.Bold };
                AppendInline(span.Inlines, m.Groups[1].Value.Trim('*'), onAnchorClick);
                inlines.Add(span);
            }
            else if (m.Groups[2].Success) // *italic*
            {
                var span = new Span { FontStyle = FontStyles.Italic };
                AppendInline(span.Inlines, m.Groups[2].Value.Trim('*'), onAnchorClick);
                inlines.Add(span);
            }
            else if (m.Groups[3].Success) // `code`
            {
                var run = new Run(m.Groups[3].Value.Trim('`')) { FontFamily = CodeFont };
                run.SetResourceReference(Inline.BackgroundProperty, "BgPanel");
                inlines.Add(run);
            }
            else if (m.Groups[4].Success) // [text](target)
            {
                var inner = m.Groups[4].Value;
                var closeBracket = inner.IndexOf(']');
                var openParen = inner.IndexOf('(');
                var linkText = inner.Substring(1, closeBracket - 1);
                var linkTarget = inner.Substring(openParen + 1, inner.Length - openParen - 2);

                if (linkTarget.StartsWith('#') && onAnchorClick != null)
                {
                    var anchor = linkTarget[1..];
                    var run = new Run(linkText) { TextDecorations = TextDecorations.Underline };
                    run.SetResourceReference(TextElement.ForegroundProperty, "TextAccent");
                    var link = new Hyperlink(run);
                    link.Click += (_, _) => onAnchorClick(anchor);
                    inlines.Add(link);
                }
                else
                {
                    inlines.Add(new Run(linkText));
                }
            }

            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            inlines.Add(new Run(text.Substring(pos)));
    }
}
