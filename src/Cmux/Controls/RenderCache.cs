using System.Windows;
using System.Windows.Media;

namespace Cmux.Controls;

/// <summary>
/// Process-wide brush and typeface caches shared by all terminal controls.
/// Brushes are frozen and keyed by color; the cache is bounded so truecolor output
/// cannot grow it without limit. UI thread only.
/// </summary>
internal static class RenderCache
{
    private const int MaxBrushes = 4096;
    private const int MaxTypefaces = 32;

    private static readonly Dictionary<Color, SolidColorBrush> _brushes = [];
    private static readonly Dictionary<(string Family, bool Bold, bool Italic), Typeface> _typefaces = [];

    public static SolidColorBrush GetBrush(Color color)
    {
        if (_brushes.TryGetValue(color, out var brush))
            return brush;

        if (_brushes.Count >= MaxBrushes)
            _brushes.Clear();

        brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushes[color] = brush;
        return brush;
    }

    public static Typeface GetTypeface(string fontFamily, bool bold, bool italic)
    {
        var key = (fontFamily, bold, italic);
        if (_typefaces.TryGetValue(key, out var typeface))
            return typeface;

        if (_typefaces.Count >= MaxTypefaces)
            _typefaces.Clear();

        typeface = new Typeface(
            new FontFamily(fontFamily),
            italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        _typefaces[key] = typeface;
        return typeface;
    }
}
