namespace SqlStudio.Models;

/// <summary>
/// One colour scheme. Surfaces step from App (furthest back) to Panel to Surface (cards, editor) to Hover.
/// Every text/background pair used by the UI is checked in ThemeTests, so a palette that fails contrast fails the build.
/// </summary>
public sealed record ThemePalette(
    string Id,
    string Name,
    bool IsDark,
    string App,
    string Panel,
    string Surface,
    string Hover,
    string Line,
    string Text,
    string Muted,
    string Accent,
    string Fill,
    string FillHover,
    string OnAccent,
    string Danger,
    string Success,
    string Warn);

/// <summary>
/// Midnight and Daylight are this app's own. The rest follow well-known community palettes (Catppuccin, Tokyo Night,
/// Nord, Rosé Pine, Gruvbox), nudged only where a colour missed the contrast targets.
/// </summary>
public static class AppThemes
{
    public static readonly IReadOnlyList<ThemePalette> All =
    [
        new("midnight", "Midnight", true, "#0E1116", "#151921", "#1C222D", "#252C39", "#2B3341", "#E6EAF2", "#8F99AB",
            "#6AA0FF", "#2F6FED", "#1F5BD6", "#FFFFFF", "#FF7B7B", "#3DD68C", "#E5A93D"),
        new("mocha", "Catppuccin Mocha", true, "#11111B", "#1E1E2E", "#313244", "#45475A", "#45475A", "#CDD6F4", "#A6ADC8",
            "#89B4FA", "#89B4FA", "#B4BEFE", "#11111B", "#F38BA8", "#A6E3A1", "#F9E2AF"),
        new("tokyo", "Tokyo Night", true, "#16161E", "#1A1B26", "#24283B", "#292E42", "#3B4261", "#C0CAF5", "#9AA5CE",
            "#7AA2F7", "#7AA2F7", "#9AB8FF", "#16161E", "#F7768E", "#9ECE6A", "#E0AF68"),
        new("nord", "Nord", true, "#242933", "#2E3440", "#3B4252", "#434C5E", "#4C566A", "#E5E9F0", "#AEB8CB",
            "#88C0D0", "#88C0D0", "#8FBCBB", "#2E3440", "#E09CA2", "#A3BE8C", "#EBCB8B"),
        new("rosepine", "Rosé Pine", true, "#191724", "#1F1D2E", "#26233A", "#312E47", "#403D52", "#E0DEF4", "#908CAA",
            "#C4A7E7", "#C4A7E7", "#D4BAF0", "#191724", "#EB6F92", "#9CCFD8", "#F6C177"),
        new("gruvbox", "Gruvbox Dark", true, "#1D2021", "#282828", "#3C3836", "#504945", "#504945", "#EBDBB2", "#AEA08D",
            "#83A598", "#83A598", "#A3BDB0", "#1D2021", "#FC7C6D", "#B8BB26", "#FABD2F"),
        new("daylight", "Daylight", false, "#F6F7F9", "#FFFFFF", "#EEF0F4", "#E3E7EE", "#DDE1E8", "#1B1F27", "#59636F",
            "#2F6FED", "#2F6FED", "#1F5BD6", "#FFFFFF", "#C62F2F", "#16794B", "#9A6700"),
        new("latte", "Catppuccin Latte", false, "#DCE0E8", "#EFF1F5", "#E6E9EF", "#CCD0DA", "#BCC0CC", "#4C4F69", "#5C5F77",
            "#1E66F5", "#1E66F5", "#1A5AD8", "#FFFFFF", "#CE0F38", "#2F7620", "#915C13"),
    ];

    public static ThemePalette Default => All[0];

    public static ThemePalette ById(string? id) => All.FirstOrDefault(t => t.Id == id) ?? Default;
}
