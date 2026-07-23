using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace StockPicker.Desktop.Controls;

/// <summary>
/// Renders the News briefing markdown (which this app generates itself, so the
/// dialect is known and small) into an Avalonia control tree with:
///   • clickable ticker symbols → <c>selectCommand</c> (Details pane / chart)
///   • an inline "+ watch" link per pick → <c>watchCommand</c>
/// Supported markdown: #/##/### headings, "- " bullets, **bold**, _italic_.
/// The raw markdown string remains the copy/clipboard source of truth — this
/// renderer is presentation only.
/// </summary>
/// <remarks>
/// WPF-ADAPTATION (Avalonia port of <c>StockPicker/Controls/NewsBriefingRenderer.cs</c>):
/// Avalonia has no <c>FlowDocument</c>. The parsing logic (regexes, per-line dispatch,
/// **bold**/_italic_ resolution) is carried over verbatim; only the OUTPUT target changed.
/// <list type="bullet">
///   <item><b>New signature:</b> <c>static Control Render(string markdown, ICommand
///     selectCommand, ICommand watchCommand)</c> — returns a <see cref="StackPanel"/> of
///     <see cref="TextBlock"/> blocks (was <c>FlowDocument</c> of <c>Block</c>s). Callers
///     host it in a <c>ScrollViewer</c> (the WPF host was a <c>FlowDocumentScrollViewer</c>).</item>
///   <item><c>Paragraph</c> → <c>TextBlock</c> (TextWrapping=Wrap); <c>Block.Margin</c> →
///     <c>TextBlock.Margin</c>; the <c>##</c> bottom border → a wrapping <see cref="Border"/>.</item>
///   <item><c>Run</c>/bold/italic → Avalonia <c>Run</c> with <c>FontWeight</c>/<c>FontStyle</c>
///     (same <c>Inlines</c> model, in <c>Avalonia.Controls.Documents</c>).</item>
///   <item>WPF <c>Hyperlink</c> (no Avalonia equivalent inside inline text) → an
///     <c>InlineUIContainer</c> hosting a link-styled <see cref="TextBlock"/> whose
///     <c>PointerPressed</c> executes the SAME <see cref="ICommand"/> (with the symbol as
///     parameter). Hover underline via PointerEntered/Exited mirrors the WPF MouseEnter/Leave
///     behaviour; the WPF <c>ToolTip</c> maps to <c>ToolTip.SetTip</c>.</item>
///   <item>LinkBrush/SubtleBrush/HeadingBrush retuned to the ModernTheme palette tokens
///     (Accent #2563EB, TextTertiary #8A93A2, Text #1B2430).</item>
/// </list>
/// </remarks>
public static class NewsBriefingRenderer
{
    // "### 1. AAPL — Apple Inc." → capture the ticker right after the number.
    private static readonly Regex PickHeading =
        new(@"^###\s*\d+\.\s+([A-Z][A-Z0-9.\-]{0,6})\b(.*)$", RegexOptions.Compiled);

    // "- **AAPL** (Apple Inc.) — BUY, score 3.2" → per-strategy bullet picks.
    private static readonly Regex BulletPick =
        new(@"^- \*\*([A-Z][A-Z0-9.\-]{0,6})\*\*(.*)$", RegexOptions.Compiled);

    private static readonly Regex BoldRegex   = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    // Both _..._ and single *...* italics (the builder writes "via *Strategy*").
    private static readonly Regex ItalicRegex = new(@"_([^_]+)_|\*([^*]+)\*", RegexOptions.Compiled);

    // Palette — mirrors the ModernTheme tokens (see Themes/ModernTheme.axaml).
    private static readonly IBrush LinkBrush    = new ImmutableSolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)); // App.AccentBrush
    private static readonly IBrush SubtleBrush  = new ImmutableSolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA2)); // App.TextTertiaryBrush
    private static readonly IBrush HeadingBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x30)); // App.TextBrush
    private static readonly IBrush BodyBrush    = new ImmutableSolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x40)); // pinned body colour

    private static readonly FontFamily Font = new("Segoe UI");

    /// <summary>Builds the briefing view. Host the returned control in a ScrollViewer.</summary>
    public static Control Render(string markdown, ICommand selectCommand, ICommand watchCommand)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(10, 6, 10, 12), // was FlowDocument.PagePadding
        };
        // Inherited text defaults (was FlowDocument.FontFamily/FontSize/Foreground).
        panel.SetValue(TextElement.FontFamilyProperty, Font);
        panel.SetValue(TextElement.FontSizeProperty, 12.5);
        panel.SetValue(TextElement.ForegroundProperty, BodyBrush);

        if (string.IsNullOrWhiteSpace(markdown))
            return panel;

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();

            if (line.Length == 0) continue;                       // spacing handled by margins

            if (line.StartsWith("# "))
            {
                panel.Children.Add(MakeHeading(line[2..], 19, new Thickness(0, 2, 0, 2)));
            }
            else if (line.StartsWith("## "))
            {
                var heading = MakeHeading(line[3..], 15.5, new Thickness(0));
                // WPF set BorderBrush/BorderThickness/Padding on the Paragraph itself.
                var border = new Border
                {
                    BorderBrush     = SubtleBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding         = new Thickness(0, 0, 0, 2),
                    Margin          = new Thickness(0, 12, 0, 3),
                    Child           = heading,
                };
                panel.Children.Add(border);
            }
            else if (PickHeading.Match(line) is { Success: true } pick)
            {
                panel.Children.Add(MakePickHeading(line, pick, selectCommand, watchCommand));
            }
            else if (line.StartsWith("### "))
            {
                panel.Children.Add(MakeHeading(line[4..], 13.5, new Thickness(0, 8, 0, 1)));
            }
            else if (BulletPick.Match(line) is { Success: true } bullet)
            {
                var tb = NewBullet();
                AddSymbolLink(tb, bullet.Groups[1].Value, selectCommand, bold: true);
                AddWatchLink(tb, bullet.Groups[1].Value, watchCommand);
                AppendInlines(tb, bullet.Groups[2].Value);
                panel.Children.Add(tb);
            }
            else if (line.StartsWith("- "))
            {
                var tb = NewBullet();
                AppendInlines(tb, line[2..]);
                panel.Children.Add(tb);
            }
            else
            {
                var tb = NewBlock(new Thickness(0, 1, 0, 1));
                AppendInlines(tb, line);
                panel.Children.Add(tb);
            }
        }

        return panel;
    }

    // ── Block builders ──────────────────────────────────────────────────────

    private static TextBlock NewBlock(Thickness margin) => new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin       = margin,
    };

    private static TextBlock MakeHeading(string text, double size, Thickness margin)
    {
        var tb = new TextBlock
        {
            FontSize     = size,
            FontWeight   = FontWeight.SemiBold,
            Foreground   = HeadingBrush,
            Margin       = margin,
            TextWrapping = TextWrapping.Wrap,
        };
        AppendInlines(tb, text);
        return tb;
    }

    /// <summary>"### 1. AAPL — Apple Inc." → number + ticker link + [+ watch] + rest.</summary>
    private static TextBlock MakePickHeading(
        string line, Match m, ICommand selectCommand, ICommand watchCommand)
    {
        var tb = new TextBlock
        {
            FontSize     = 13.5,
            FontWeight   = FontWeight.SemiBold,
            Foreground   = HeadingBrush,
            Margin       = new Thickness(0, 8, 0, 1),
            TextWrapping = TextWrapping.Wrap,
        };

        // Leading "N. " before the ticker.
        int tickerStart = m.Groups[1].Index;
        tb.Inlines!.Add(new Run(line[4..tickerStart]));

        AddSymbolLink(tb, m.Groups[1].Value, selectCommand, bold: true);
        AddWatchLink(tb, m.Groups[1].Value, watchCommand);
        AppendInlines(tb, m.Groups[2].Value);
        return tb;
    }

    private static TextBlock NewBullet()
    {
        var tb = new TextBlock
        {
            Margin       = new Thickness(14, 0.5, 0, 0.5),
            TextWrapping = TextWrapping.Wrap,
        };
        tb.Inlines!.Add(new Run("•  ") { Foreground = SubtleBrush });
        return tb;
    }

    // ── Inline builders ─────────────────────────────────────────────────────

    private static void AddSymbolLink(TextBlock tb, string symbol, ICommand selectCommand, bool bold)
    {
        var link = MakeLink(
            symbol,
            fontSize: bold ? 13.5 : 12.5,
            bold: bold,
            brush: LinkBrush,
            tooltip: $"Show {symbol} in the Details pane",
            command: selectCommand,
            parameter: symbol);
        tb.Inlines!.Add(new InlineUIContainer(link));
    }

    private static void AddWatchLink(TextBlock tb, string symbol, ICommand watchCommand)
    {
        tb.Inlines!.Add(new Run(" "));
        var link = MakeLink(
            "[+ watch]",
            fontSize: 10.5,
            bold: false,
            brush: SubtleBrush,
            tooltip: $"Add {symbol} to the Watch list",
            command: watchCommand,
            parameter: symbol);
        tb.Inlines!.Add(new InlineUIContainer(link));
    }

    /// <summary>
    /// A link-styled TextBlock: coloured, hand cursor, underline on hover, click → command.
    /// (Replaces WPF <c>Hyperlink</c>, which has no inline-text equivalent in Avalonia.)
    /// </summary>
    private static TextBlock MakeLink(
        string text, double fontSize, bool bold, IBrush brush, string tooltip,
        ICommand command, string parameter)
    {
        var link = new TextBlock
        {
            Text                = text,
            Foreground          = brush,
            FontSize            = fontSize,
            FontWeight          = bold ? FontWeight.SemiBold : FontWeight.Normal,
            Cursor              = new Cursor(StandardCursorType.Hand),
            VerticalAlignment   = VerticalAlignment.Center,
        };
        ToolTip.SetTip(link, tooltip);

        link.PointerPressed += (_, e) =>
        {
            if (command.CanExecute(parameter)) command.Execute(parameter);
            e.Handled = true;
        };
        link.PointerEntered += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.PointerExited  += (_, _) => link.TextDecorations = null;
        return link;
    }

    /// <summary>Appends text with **bold** and _italic_ spans resolved.</summary>
    private static void AppendInlines(TextBlock tb, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        int pos = 0;
        foreach (Match m in BoldRegex.Matches(text))
        {
            if (m.Index > pos) AppendItalicAware(tb, text[pos..m.Index]);
            tb.Inlines!.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeight.SemiBold });
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) AppendItalicAware(tb, text[pos..]);
    }

    private static void AppendItalicAware(TextBlock tb, string text)
    {
        int pos = 0;
        foreach (Match m in ItalicRegex.Matches(text))
        {
            if (m.Index > pos) tb.Inlines!.Add(new Run(text[pos..m.Index]));
            var inner = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            tb.Inlines!.Add(new Run(inner)
            {
                FontStyle  = FontStyle.Italic,
                Foreground = SubtleBrush,
            });
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) tb.Inlines!.Add(new Run(text[pos..]));
    }
}
