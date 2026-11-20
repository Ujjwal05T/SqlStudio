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
/// Dark Sapphire, Monokai Pro, Ocean Breeze, Solarized Dark, Nordic Noir and Tokyo Night Cinematic follow the theme sheet:
/// its five swatches give the window, panel, surface, accent and text colours, and the rest (hover, divider, muted text,
/// status colours) is derived from them and held to the same contrast targets by ThemeTests.
/// The other light themes (Arctic Blue, Soft Lavender, Mint Paper, Warm Sand, Rose Quartz, Claude Clay &amp; Paper, Slate Mist,
/// Graphite Light) were designed for this app: a tinted window, a near-white panel, and one deepened accent that carries white labels.
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
        new("sapphire", "Dark Sapphire", true, "#0E111A", "#161C2A", "#20293A", "#343C4C", "#343C4C", "#E3E8EF", "#8D929C",
            "#62BAF3", "#62BAF3", "#838CF1", "#0E111A", "#FF7B8A", "#5FD9A0", "#F0B95A"),
        new("monokai", "Monokai Pro", true, "#17151E", "#201D2A", "#2F283C", "#423B4E", "#423B4E", "#EDE9F5", "#9793A0",
            "#B787F5", "#B787F5", "#E479B3", "#17151E", "#FF6188", "#A9DC76", "#F9E796"),
        new("ocean", "Ocean Breeze", true, "#0E171B", "#152328", "#20333A", "#33464C", "#33464C", "#E0EEF1", "#8F9DA1",
            "#68D1BF", "#68D1BF", "#86E7D4", "#0E171B", "#F28B82", "#6FE3B0", "#F5C26B"),
        new("solarized", "Solarized Dark", true, "#171714", "#20201A", "#333127", "#464339", "#464339", "#EDE8D7", "#9F9C8F",
            "#E2B53E", "#E2B53E", "#68D1BF", "#171714", "#F07178", "#9CC653", "#F0C674"),
        new("noir", "Nordic Noir", true, "#151922", "#1E2532", "#2B3546", "#3E4857", "#3E4857", "#EDEFF3", "#9EA2AA",
            "#A8B4F6", "#A8B4F6", "#94BECE", "#151922", "#F0899A", "#8FD9A8", "#E8C27A"),
        new("cinematic", "Tokyo Night Cinematic", true, "#141322", "#1A1930", "#25243E", "#353550", "#353550", "#C2CAF1", "#898EAF",
            "#B69BF1", "#B69BF1", "#82A1F1", "#141322", "#F7768E", "#9ECE6A", "#E0AF68"),
        // The dark counterpart of Cherry Red: the same warm ink and paper, with the cherry lifted so it reads on dark. Buttons keep the
        // deeper cherry with white labels (darker still on hover); the lighter shade is for icons, links and the text accent.
        new("cherry-dark", "Cherry Red Dark", true, "#121110", "#1B1A18", "#262421", "#3A3835", "#3A3835", "#F3EFE8", "#908D88",
            "#F0707F", "#C8364F", "#B02C44", "#FFFFFF", "#FF9166", "#6FCF97", "#E8B04A"),
        new("daylight", "Daylight", false, "#F6F7F9", "#FFFFFF", "#EEF0F4", "#E3E7EE", "#DDE1E8", "#1B1F27", "#59636F",
            "#2F6FED", "#2F6FED", "#1F5BD6", "#FFFFFF", "#C62F2F", "#16794B", "#9A6700"),
        new("latte", "Catppuccin Latte", false, "#DCE0E8", "#EFF1F5", "#E6E9EF", "#CCD0DA", "#BCC0CC", "#4C4F69", "#5C5F77",
            "#1E66F5", "#1E66F5", "#1A5AD8", "#FFFFFF", "#CE0F38", "#2F7620", "#915C13"),
        new("arctic", "Arctic Blue", false, "#EBF1F8", "#FAFCFE", "#E6EEF7", "#D8E4F1", "#CFDCEB", "#122033", "#616B78",
            "#2972D2", "#2972D2", "#2362B5", "#FFFFFF", "#C62F2F", "#16794B", "#9A6700"),
        new("lavender", "Soft Lavender", false, "#EFECF8", "#FBFAFE", "#E8E4F4", "#DCD6EE", "#D4CDE8", "#221E35", "#676475",
            "#6C4FD9", "#6C4FD9", "#5D44BB", "#FFFFFF", "#B5263F", "#16794B", "#946200"),
        new("mint", "Mint Paper", false, "#EAF4EE", "#FAFDFB", "#E1EEE6", "#D2E5DA", "#C8DDD0", "#13281D", "#5D6C64",
            "#1C8256", "#1C8256", "#18704A", "#FFFFFF", "#C0392B", "#16794B", "#96640A"),
        new("sand", "Warm Sand", false, "#F1EBDF", "#FCFAF5", "#ECE4D4", "#E0D6C2", "#D6CBB5", "#2A2217", "#69635A",
            "#A5641C", "#A5641C", "#8E5618", "#FFFFFF", "#B0362A", "#2F7A3E", "#8F5B00"),
        new("rose", "Rose Quartz", false, "#F6ECEF", "#FFFBFC", "#F0E2E6", "#E6D2D8", "#DDC8CF", "#2D1E24", "#6C6065",
            "#C93E69", "#C93E69", "#AD355A", "#FFFFFF", "#B7283A", "#1E7A52", "#94620A"),
        new("clay", "Claude Clay & Paper", false, "#EFEDE4", "#FAF9F5", "#ECE9DE", "#E1DDCE", "#D8D3C3", "#1F1E1B", "#676663",
            "#B45A39", "#B45A39", "#9B4D31", "#FFFFFF", "#B5342B", "#2F7A47", "#8F5E0A"),
        new("slate", "Slate Mist", false, "#EDF0F4", "#FBFCFD", "#E4E9EF", "#D6DDE6", "#CDD5DF", "#18202B", "#61666E",
            "#4B5FD0", "#4B5FD0", "#4052B3", "#FFFFFF", "#C22F3A", "#17784E", "#95640A"),
        new("graphite", "Graphite Light", false, "#EFEFEF", "#FFFFFF", "#E8E8E8", "#DADADA", "#D0D0D0", "#1A1A1A", "#666666",
            "#3D3D3D", "#3D3D3D", "#343434", "#FFFFFF", "#C22F2F", "#16794B", "#946000"),
        // Taken from the owner's site: warm paper, near-black ink, grey secondary text and a cherry that carries the buttons and the active link.
        new("cherry", "Cherry Red", false, "#F0EDE6", "#F9F7F2", "#F1EEE7", "#E6E2D8", "#D9D6CE", "#171614", "#6B6A66",
            "#AE2A3F", "#AE2A3F", "#962236", "#FFFFFF", "#B33A18", "#2F7A47", "#8F5E0A"),
    ];

    public static ThemePalette Default => All[0];

    public static ThemePalette ById(string? id) => All.FirstOrDefault(t => t.Id == id) ?? Default;
}
