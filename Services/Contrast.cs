using System.Globalization;

namespace SqlStudio.Services;

/// <summary>WCAG relative-luminance contrast, so colour choices can be checked by a test instead of by eye.</summary>
public static class Contrast
{
    public static double Ratio(string foregroundHex, string backgroundHex)
    {
        var a = Luminance(foregroundHex);
        var b = Luminance(backgroundHex);
        if (a < b)
            (a, b) = (b, a);
        return (a + 0.05) / (b + 0.05);
    }

    static double Luminance(string hex)
    {
        var h = hex.TrimStart('#');
        double Channel(int start)
        {
            var c = int.Parse(h.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }
}
