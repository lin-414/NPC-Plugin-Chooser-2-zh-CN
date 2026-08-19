using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// Renders the Mod Issues Details cell as inlines so the repin remedy can sit,
/// colored, DIRECTLY UNDER the verdict's first line (the headline) — user-specified
/// placement. Detail stays one plain string (cache/CSV/tooltips unchanged); the
/// splice is purely visual: first line of DetailText, then RemedyText in green,
/// then the remainder. With no remedy the cell renders DetailText verbatim.
/// (On demoted rows whose Detail leads with a traits/ghost explanation line, the
/// remedy lands after that first line instead — still at the top of the cell.)
/// </summary>
public static class IssueDetailFormatter
{
    // MediumSeaGreen: legible on both the dark and light theme backgrounds.
    private static readonly Brush RemedyBrush = CreateFrozen(Color.FromRgb(0x3C, 0xB3, 0x71));

    private static Brush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static readonly DependencyProperty DetailTextProperty = DependencyProperty.RegisterAttached(
        "DetailText", typeof(string), typeof(IssueDetailFormatter), new PropertyMetadata(null, OnAnyChanged));

    public static string? GetDetailText(DependencyObject obj) => (string?)obj.GetValue(DetailTextProperty);
    public static void SetDetailText(DependencyObject obj, string? value) => obj.SetValue(DetailTextProperty, value);

    public static readonly DependencyProperty RemedyTextProperty = DependencyProperty.RegisterAttached(
        "RemedyText", typeof(string), typeof(IssueDetailFormatter), new PropertyMetadata(null, OnAnyChanged));

    public static string? GetRemedyText(DependencyObject obj) => (string?)obj.GetValue(RemedyTextProperty);
    public static void SetRemedyText(DependencyObject obj, string? value) => obj.SetValue(RemedyTextProperty, value);

    private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock textBlock) Rebuild(textBlock);
    }

    private static void Rebuild(TextBlock textBlock)
    {
        string detail = GetDetailText(textBlock) ?? string.Empty;
        string remedy = GetRemedyText(textBlock) ?? string.Empty;

        textBlock.Inlines.Clear();
        if (remedy.Length == 0)
        {
            textBlock.Inlines.Add(new Run(detail));
            return;
        }

        int firstBreak = detail.IndexOf('\n');
        string headline = firstBreak < 0 ? detail : detail[..firstBreak];
        string rest = firstBreak < 0 ? string.Empty : detail[(firstBreak + 1)..];

        if (headline.Length > 0)
        {
            textBlock.Inlines.Add(new Run(headline));
            textBlock.Inlines.Add(new LineBreak());
        }
        textBlock.Inlines.Add(new Run(remedy) { Foreground = RemedyBrush, FontWeight = FontWeights.SemiBold });
        if (rest.Length > 0)
        {
            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add(new Run(rest));
        }
    }
}
