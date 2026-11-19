using System.Globalization;
using Avalonia.Media;

namespace SqlStudio.Views;

/// <summary>
/// The app's icon set: simple line icons on a 24 x 24 grid, drawn with one stroke weight and round ends so every
/// icon looks like it belongs to the same family. Add new ones here and use them by name (Icon Kind="database").
/// </summary>
public static class Icons
{
    static readonly Dictionary<string, Geometry> Cache = new(StringComparer.OrdinalIgnoreCase);

    static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        // data objects
        ["server"] = RoundRect(3, 4, 18, 7, 2) + RoundRect(3, 13, 18, 7, 2) + "M7 7.5h.01 M7 16.5h.01",
        ["database"] = "M4 6a8 3 0 0 0 16 0 a8 3 0 0 0 -16 0 z M4 6v12a8 3 0 0 0 16 0V6 M4 12a8 3 0 0 0 16 0",
        ["table"] = RoundRect(3, 4, 18, 16, 2) + "M3 10h18 M9.5 10v10",
        ["view"] = "M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z",
        ["procedure"] = "M8 4c-2 0-3 1-3 3v2c0 1-.8 2-2 3 1.2 1 2 2 2 3v2c0 2 1 3 3 3 M16 4c2 0 3 1 3 3v2c0 1 .8 2 2 3-1.2 1-2 2-2 3v2c0 2-1 3-3 3",
        ["function"] = "M15 4.5c-2.5-1-4 .2-4 2.5v10c0 2.3-1.5 3.5-4 2.5 M7.5 11h7",
        ["column"] = RoundRect(8, 4, 8, 16, 2),
        ["folder"] = "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z",
        ["layers"] = "M12 3 3 8l9 5 9-5z M3 12.5l9 5 9-5 M3 17l9 5 9-5",
        ["plan"] = "M3 9h6V4H3z M15 8h6V3h-6z M15 21h6v-5h-6z M9 6.5h3 M12 5.5h3 M12 5.5v13 M12 18.5h3",
        ["maximize"] = "M4 9V5a1 1 0 0 1 1-1h4 M15 4h4a1 1 0 0 1 1 1v4 M20 15v4a1 1 0 0 1-1 1h-4 M9 20H5a1 1 0 0 1-1-1v-4",
        ["drive"] = "M3 14h18 M5 14 7.5 5h9L19 14 M5 14v4a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-4 M16 17h.01 M12 17h.01",
        ["arrow-up"] = "M12 19V5 M5 12l7-7 7 7",
        ["list"] = "M8 6h12 M8 12h12 M8 18h12 M4 6h.01 M4 12h.01 M4 18h.01",

        // actions
        ["play"] = "M7 4.5v15l12.5-7.5z",
        ["stop"] = RoundRect(6, 6, 12, 12, 2),
        ["plus"] = "M12 5v14 M5 12h14",
        ["minus"] = "M5 12h14",
        ["notequal"] = "M5 9h14 M5 15h14 M8 20 16 4",
        ["swap"] = "M7 4 3 8l4 4 M3 8h16 M17 20l4-4-4-4 M21 16H5",
        ["check"] = "M5 12.5 9.5 17 19 7.5",
        ["copy"] = RoundRect(9, 9, 11, 11, 2) + "M5 15V6a2 2 0 0 1 2-2h8",
        ["chevron-down"] = "M6 9l6 6 6-6",
        ["download"] = "M12 4v11 M7.5 10.5 12 15l4.5-4.5 M5 20h14",
        ["restore"] = "M3 12a9 9 0 1 0 3-6.7L3 8 M3 3v5h5",
        ["file"] = "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z M14 3v5h5 M9 13h6 M9 17h4",
        ["sparkles"] = "M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z M19 16l.7 1.8 1.8.7-1.8.7L19 21l-.7-1.8-1.8-.7 1.8-.7z",
        ["search"] = "M3 11a8 8 0 0 0 16 0 a8 8 0 0 0 -16 0 z M21 21l-4.3-4.3",
        ["pencil"] = "M17 3a2.83 2.83 0 0 1 4 4L7.5 20.5 2 22l1.5-5.5z",
        ["trash"] = "M3 6h18 M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2 M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6 M10 11v6 M14 11v6",
        ["undo"] = "M9 14 4 9l5-5 M4 9h10.5a5.5 5.5 0 0 1 0 11H11",
        ["save"] = "M5 3h11l3 3v13a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M7 3v5h8V3 M7 21v-7h10v7",
        ["chevron-up"] = "M6 15l6-6 6 6",
        ["note"] = "M5 3h14a2 2 0 0 1 2 2v10l-6 6H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M15 21v-4a2 2 0 0 1 2-2h4 M8 9h8 M8 13h4",
        ["close"] = "M6 6l12 12 M18 6 6 18",
        ["sun"] = "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z M12 2v2 M12 20v2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M2 12h2 M20 12h2 M4.9 19.1l1.4-1.4 M17.7 6.3l1.4-1.4",
        ["refresh"] = "M21 12a9 9 0 1 1-3-6.7L21 8 M21 3v5h-5",
        ["diagram"] = "M3 4h7v6H3z M14 4h7v6h-7z M3 14h7v6H3z M14 14h7v6h-7z",
        ["theme"] = "M12 3a9 9 0 0 1 0 18 a9 9 0 0 1 0 -18 z M12 3v18",
    };

    /// <summary>The icon's outline, or null for an unknown or empty name (the icon then draws nothing).</summary>
    public static Geometry? Get(string? kind)
    {
        if (string.IsNullOrEmpty(kind) || !Paths.TryGetValue(kind, out var data))
            return null;
        if (!Cache.TryGetValue(kind, out var geometry))
            Cache[kind] = geometry = Geometry.Parse(data);
        return geometry;
    }

    public static IEnumerable<string> Names => Paths.Keys;

    /// <summary>A rounded rectangle as path data. Written out here because hand-typing arcs is where icons go wrong.</summary>
    static string RoundRect(double x, double y, double w, double h, double r)
    {
        string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        return $"M{N(x + r)} {N(y)} h{N(w - 2 * r)} a{N(r)} {N(r)} 0 0 1 {N(r)} {N(r)} v{N(h - 2 * r)} a{N(r)} {N(r)} 0 0 1 {N(-r)} {N(r)} " +
               $"h{N(-(w - 2 * r))} a{N(r)} {N(r)} 0 0 1 {N(-r)} {N(-r)} v{N(-(h - 2 * r))} a{N(r)} {N(r)} 0 0 1 {N(r)} {N(-r)} z ";
    }
}
