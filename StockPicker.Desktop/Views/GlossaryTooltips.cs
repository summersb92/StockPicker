using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using StockPicker.Reference;

namespace StockPicker.Desktop.Views;

/// <summary>
/// Sources UI tooltips from the canonical <see cref="Glossary"/> so a term is documented
/// in exactly one place. Used for DataGrid column headers (via an explicit
/// column-index → Glossary-key map) and for standalone dialog labels.
/// </summary>
internal static class GlossaryTooltips
{
    /// <summary>
    /// Applies Glossary tooltips to the headers of <paramref name="grid"/>. Only columns
    /// that map to a real Glossary key are touched; every other header is left as-is.
    /// </summary>
    /// <remarks>
    /// Column indices match the <c>DataGrid.Columns</c> declaration order in the .axaml.
    /// Where an inline <c>TextBlock</c> header already exists (with a hard-coded tooltip),
    /// that tooltip is replaced by the Glossary one; plain-string headers are wrapped in a
    /// <c>TextBlock</c> that preserves their display text, so header-text-keyed behaviour
    /// (e.g. MainWindow's <c>GetColumnKey</c> column-order persistence) is unaffected.
    /// </remarks>
    internal static void Apply(DataGrid grid, IReadOnlyDictionary<int, string> map)
    {
        var cols = grid.Columns;
        foreach (var (index, key) in map)
        {
            if (index >= cols.Count) continue;
            if (!Glossary.TryGet(key, out var def) || def is null) continue;

            var col = cols[index];
            if (col.Header is TextBlock tb)
            {
                // Inline TextBlock header — retarget its tooltip to the Glossary.
                ToolTip.SetTip(tb, def.Tooltip);
            }
            else
            {
                // Plain-string header — wrap it so ToolTip.Tip attaches to a Control while
                // the displayed text stays identical.
                var text = col.Header as string ?? col.Header?.ToString() ?? key;
                var header = new TextBlock { Text = text };
                ToolTip.SetTip(header, def.Tooltip);
                col.Header = header;
            }
        }
    }

    /// <summary>
    /// Puts the Glossary tooltip for <paramref name="key"/> on <paramref name="control"/>
    /// (typically a dialog field label). No-op when the key has no Glossary entry.
    /// </summary>
    internal static void Apply(Control control, string key)
    {
        if (Glossary.TryGet(key, out var def) && def is not null)
            ToolTip.SetTip(control, def.Tooltip);
    }
}

/// <summary>
/// XAML-attachable bridge to <see cref="GlossaryTooltips.Apply(Control, string)"/> for
/// labels declared inside DataTemplates (which code-behind can't reach directly):
/// <c>views:GlossaryTip.Key="RecommendationMean"</c> sets the control's tooltip from the
/// canonical Glossary entry. Unknown keys are a silent no-op, matching Apply.
/// </summary>
public class GlossaryTip : AvaloniaObject
{
    public static readonly AttachedProperty<string?> KeyProperty =
        AvaloniaProperty.RegisterAttached<GlossaryTip, Control, string?>("Key");

    public static void SetKey(Control control, string? value) => control.SetValue(KeyProperty, value);
    public static string? GetKey(Control control) => control.GetValue(KeyProperty);

    static GlossaryTip()
    {
        KeyProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            if (args.NewValue is string key && !string.IsNullOrWhiteSpace(key))
                GlossaryTooltips.Apply(control, key);
        });
    }
}
