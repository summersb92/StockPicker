using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace StockPicker.Controls
{
    /// <summary>
    /// Renders the News briefing markdown (which this app generates itself, so the
    /// dialect is known and small) into a WPF <see cref="FlowDocument"/> with:
    ///   • clickable ticker symbols → <c>selectCommand</c> (Details pane / chart)
    ///   • an inline "+ watch" link per pick → <c>watchCommand</c>
    /// Supported markdown: #/##/### headings, "- " bullets, **bold**, _italic_.
    /// The raw markdown string remains the copy/clipboard source of truth — this
    /// renderer is presentation only.
    /// </summary>
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

        private static readonly Brush LinkBrush    = new SolidColorBrush(Color.FromRgb(0x1A, 0x6B, 0xB5));
        private static readonly Brush SubtleBrush  = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly Brush HeadingBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));

        public static FlowDocument Render(string markdown, ICommand selectCommand, ICommand watchCommand)
        {
            var doc = new FlowDocument
            {
                FontFamily   = new FontFamily("Segoe UI"),
                FontSize     = 12.5,
                PagePadding  = new Thickness(10, 6, 10, 12),
                LineHeight   = double.NaN,
                // Pin the foreground: without this the document inherits whatever the
                // selected TabItem's foreground is (accent blue under the modern theme).
                Foreground   = new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x40)),
            };

            if (string.IsNullOrWhiteSpace(markdown))
                return doc;

            foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.TrimEnd();

                if (line.Length == 0) continue;                       // spacing handled by margins

                if (line.StartsWith("# "))
                {
                    doc.Blocks.Add(MakeHeading(line[2..], 19, new Thickness(0, 2, 0, 2)));
                }
                else if (line.StartsWith("## "))
                {
                    var p = MakeHeading(line[3..], 15.5, new Thickness(0, 12, 0, 3));
                    p.BorderBrush = SubtleBrush;
                    p.BorderThickness = new Thickness(0, 0, 0, 1);
                    p.Padding = new Thickness(0, 0, 0, 2);
                    doc.Blocks.Add(p);
                }
                else if (PickHeading.Match(line) is { Success: true } pick)
                {
                    doc.Blocks.Add(MakePickHeading(line, pick, selectCommand, watchCommand));
                }
                else if (line.StartsWith("### "))
                {
                    doc.Blocks.Add(MakeHeading(line[4..], 13.5, new Thickness(0, 8, 0, 1)));
                }
                else if (BulletPick.Match(line) is { Success: true } bullet)
                {
                    var p = NewBullet();
                    AddSymbolLink(p, bullet.Groups[1].Value, selectCommand, bold: true);
                    AddWatchLink(p, bullet.Groups[1].Value, watchCommand);
                    AppendInlines(p, bullet.Groups[2].Value);
                    doc.Blocks.Add(p);
                }
                else if (line.StartsWith("- "))
                {
                    var p = NewBullet();
                    AppendInlines(p, line[2..]);
                    doc.Blocks.Add(p);
                }
                else
                {
                    var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                    AppendInlines(p, line);
                    doc.Blocks.Add(p);
                }
            }

            return doc;
        }

        // ── Block builders ──────────────────────────────────────────────────────

        private static Paragraph MakeHeading(string text, double size, Thickness margin)
        {
            var p = new Paragraph
            {
                FontSize   = size,
                FontWeight = FontWeights.SemiBold,
                Foreground = HeadingBrush,
                Margin     = margin,
            };
            AppendInlines(p, text);
            return p;
        }

        /// <summary>"### 1. AAPL — Apple Inc." → number + ticker link + [+ watch] + rest.</summary>
        private static Paragraph MakePickHeading(
            string line, Match m, ICommand selectCommand, ICommand watchCommand)
        {
            var p = new Paragraph
            {
                FontSize   = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = HeadingBrush,
                Margin     = new Thickness(0, 8, 0, 1),
            };

            // Leading "N. " before the ticker.
            int tickerStart = m.Groups[1].Index;
            p.Inlines.Add(new Run(line[4..tickerStart]));

            AddSymbolLink(p, m.Groups[1].Value, selectCommand, bold: true);
            AddWatchLink(p, m.Groups[1].Value, watchCommand);
            AppendInlines(p, m.Groups[2].Value);
            return p;
        }

        private static Paragraph NewBullet() => new()
        {
            Margin = new Thickness(14, 0.5, 0, 0.5),
            Inlines = { new Run("•  ") { Foreground = SubtleBrush } },
        };

        // ── Inline builders ─────────────────────────────────────────────────────

        private static void AddSymbolLink(Paragraph p, string symbol, ICommand selectCommand, bool bold)
        {
            var link = new Hyperlink(new Run(symbol))
            {
                Command          = selectCommand,
                CommandParameter = symbol,
                Foreground       = LinkBrush,
                TextDecorations  = null,                 // underline on hover only feels cleaner
                ToolTip          = $"Show {symbol} in the Details pane",
                FontWeight       = bold ? FontWeights.SemiBold : FontWeights.Normal,
            };
            link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (_, _) => link.TextDecorations = null;
            p.Inlines.Add(link);
        }

        private static void AddWatchLink(Paragraph p, string symbol, ICommand watchCommand)
        {
            p.Inlines.Add(new Run(" "));
            var link = new Hyperlink(new Run("[+ watch]"))
            {
                Command          = watchCommand,
                CommandParameter = symbol,
                Foreground       = SubtleBrush,
                FontSize         = 10.5,
                FontWeight       = FontWeights.Normal,
                TextDecorations  = null,
                ToolTip          = $"Add {symbol} to the Watch list",
            };
            link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (_, _) => link.TextDecorations = null;
            p.Inlines.Add(link);
        }

        /// <summary>Appends text with **bold** and _italic_ spans resolved.</summary>
        private static void AppendInlines(Paragraph p, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            int pos = 0;
            foreach (Match m in BoldRegex.Matches(text))
            {
                if (m.Index > pos) AppendItalicAware(p, text[pos..m.Index]);
                p.Inlines.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeights.SemiBold });
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) AppendItalicAware(p, text[pos..]);
        }

        private static void AppendItalicAware(Paragraph p, string text)
        {
            int pos = 0;
            foreach (Match m in ItalicRegex.Matches(text))
            {
                if (m.Index > pos) p.Inlines.Add(new Run(text[pos..m.Index]));
                var inner = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                p.Inlines.Add(new Run(inner)
                {
                    FontStyle  = FontStyles.Italic,
                    Foreground = SubtleBrush,
                });
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) p.Inlines.Add(new Run(text[pos..]));
        }
    }
}
