using System.Windows;
using System.Windows.Media;

namespace NewQ.App.Controls;

/// <summary>
/// Adds an icon (a glyph of the app's icon font) to any button using the NewQ button templates:
/// <c>&lt;Button ctl:ButtonIcon.Glyph="{StaticResource IconAudio}" Content="Audio" /&gt;</c>.
/// Without content the button becomes icon-only (give it a ToolTip).
/// </summary>
public static class ButtonIcon
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(ButtonIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Colour of the glyph only (e.g. a semantic colour on a neutral button); null = the button's text colour.</summary>
    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached(
        "Brush", typeof(Brush), typeof(ButtonIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static string? GetGlyph(DependencyObject d) => (string?)d.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject d, string? value) => d.SetValue(GlyphProperty, value);

    public static Brush? GetBrush(DependencyObject d) => (Brush?)d.GetValue(BrushProperty);
    public static void SetBrush(DependencyObject d, Brush? value) => d.SetValue(BrushProperty, value);
}
