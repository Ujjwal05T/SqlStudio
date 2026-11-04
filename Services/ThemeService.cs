using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using SqlStudio.Models;

namespace SqlStudio.Services;

/// <summary>
/// Applies a palette at runtime. Each palette becomes its own theme variant that inherits Dark or Light, so anything
/// not set here (scrollbars, focus rings) falls back to Fluent's own colours, and switching updates every open window live.
/// </summary>
public static class ThemeService
{
    static readonly Dictionary<string, ThemeVariant> Variants = [];

    public static ThemePalette Current { get; private set; } = AppThemes.Default;

    public static void Apply(ThemePalette palette)
    {
        var app = Application.Current ?? throw new InvalidOperationException("No running application.");
        if (!Variants.TryGetValue(palette.Id, out var variant))
            Variants[palette.Id] = variant = new ThemeVariant(palette.Id, palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light);

        app.Resources.ThemeDictionaries[variant] = Build(palette);
        app.RequestedThemeVariant = variant;
        Current = palette;
    }

    static ResourceDictionary Build(ThemePalette p)
    {
        var d = new ResourceDictionary();

        // Tokens the app's own XAML uses.
        Brush(d, "AppBg", p.App);
        Brush(d, "PanelBg", p.Panel);
        Brush(d, "SurfaceBg", p.Surface);
        Brush(d, "SurfaceHoverBg", p.Hover);
        Brush(d, "LineBrush", p.Line);
        Brush(d, "TextBrush", p.Text);
        Brush(d, "MutedText", p.Muted);
        Brush(d, "AccentBrush", p.Accent);
        Brush(d, "AccentFill", p.Fill);
        Brush(d, "AccentFillHover", p.FillHover);
        Brush(d, "OnAccentBrush", p.OnAccent);
        Brush(d, "DangerBrush", p.Danger);
        Brush(d, "SuccessBrush", p.Success);
        Brush(d, "WarnBrush", p.Warn);

        // The same keys Fluent's own ColorPaletteResources sets, so built-in controls (inputs, menus, combo boxes,
        // scrollbars) pick up the scheme as well.
        var accent = Color.Parse(p.Fill);
        d["SystemAccentColor"] = accent;
        d["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.12);
        d["SystemAccentColorLight2"] = Mix(accent, Colors.White, 0.24);
        d["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.36);
        d["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.12);
        d["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.24);
        d["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.36);
        d["TextOnAccentFillColorPrimary"] = Color.Parse(p.OnAccent);

        var panel = Color.Parse(p.Panel);
        var text = Color.Parse(p.Text);
        var muted = Color.Parse(p.Muted);
        d["SystemRegionColor"] = panel;
        d["SystemBaseHighColor"] = text;
        d["SystemBaseMediumHighColor"] = Mix(text, panel, 0.15);
        d["SystemBaseMediumColor"] = muted;
        d["SystemBaseMediumLowColor"] = Mix(muted, panel, 0.4);
        d["SystemBaseLowColor"] = Color.Parse(p.Line);
        d["SystemAltHighColor"] = panel;
        d["SystemAltMediumHighColor"] = panel;
        d["SystemAltMediumColor"] = panel;
        d["SystemAltMediumLowColor"] = panel;
        d["SystemAltLowColor"] = panel;
        d["SystemChromeLowColor"] = Color.Parse(p.Surface);
        d["SystemChromeMediumLowColor"] = Color.Parse(p.Surface);
        d["SystemChromeMediumColor"] = Color.Parse(p.Hover);
        d["SystemChromeHighColor"] = Color.Parse(p.Line);
        d["SystemChromeAltLowColor"] = muted;
        d["SystemChromeDisabledLowColor"] = Mix(muted, panel, 0.5);
        d["SystemChromeDisabledHighColor"] = Color.Parse(p.Line);
        d["SystemListLowColor"] = Color.Parse(p.Hover);
        d["SystemListMediumColor"] = Color.Parse(p.Line);
        d["SystemErrorTextColor"] = Color.Parse(p.Danger);

        foreach (var (key, hex) in ControlColours(p))
            Brush(d, key, hex);
        d["ComboBoxDropdownBorderPadding"] = new Thickness(4); // breathing room so highlighted rows never touch the popup edge
        return d;
    }

    /// <summary>Fluent's per-control brushes, set from the palette. ThemeTests checks every key really exists in Fluent.</summary>
    public static IReadOnlyDictionary<string, string> ControlColours(ThemePalette p)
    {
        const string clear = "#00000000";
        return new Dictionary<string, string>
        {
            // Drop-down boxes and their lists
            ["ComboBoxBackground"] = p.Surface,
            ["ComboBoxBackgroundUnfocused"] = p.Surface,
            ["ComboBoxBackgroundDisabled"] = p.Surface,
            ["ComboBoxBackgroundPointerOver"] = p.Hover,
            ["ComboBoxBackgroundPressed"] = p.Hover,
            ["ComboBoxBorderBrush"] = p.Line,
            ["ComboBoxBackgroundBorderBrushUnfocused"] = p.Line,
            ["ComboBoxBorderBrushPointerOver"] = p.Muted,
            ["ComboBoxBorderBrushPressed"] = p.Accent,
            ["ComboBoxBackgroundBorderBrushFocused"] = p.Accent,
            ["ComboBoxForeground"] = p.Text,
            ["ComboBoxForegroundFocused"] = p.Text,
            ["ComboBoxForegroundFocusedPressed"] = p.Text,
            ["ComboBoxPlaceHolderForeground"] = p.Muted,
            ["ComboBoxDropDownGlyphForeground"] = p.Muted,
            ["ComboBoxDropDownBackground"] = p.Surface,
            ["ComboBoxDropDownBorderBrush"] = p.Line,
            ["ComboBoxItemBackground"] = clear,
            ["ComboBoxItemBackgroundPointerOver"] = p.Hover,
            ["ComboBoxItemBackgroundPressed"] = p.Hover,
            ["ComboBoxItemBackgroundSelected"] = p.Hover,
            ["ComboBoxItemBackgroundSelectedPointerOver"] = p.Hover,
            ["ComboBoxItemBackgroundSelectedPressed"] = p.Hover,
            ["ComboBoxItemForeground"] = p.Text,
            ["ComboBoxItemForegroundPointerOver"] = p.Text,
            ["ComboBoxItemForegroundPressed"] = p.Text,
            ["ComboBoxItemForegroundSelected"] = p.Text,
            ["ComboBoxItemForegroundSelectedPointerOver"] = p.Text,
            ["ComboBoxItemForegroundSelectedPressed"] = p.Text,

            // Text boxes
            ["TextControlBackground"] = p.Surface,
            ["TextControlBackgroundPointerOver"] = p.Hover,
            ["TextControlBackgroundFocused"] = p.Surface,
            ["TextControlBorderBrush"] = p.Line,
            ["TextControlBorderBrushPointerOver"] = p.Muted,
            ["TextControlBorderBrushFocused"] = p.Accent,
            ["TextControlForeground"] = p.Text,
            ["TextControlForegroundPointerOver"] = p.Text,
            ["TextControlForegroundFocused"] = p.Text,

            // Buttons
            ["ButtonBackground"] = p.Surface,
            ["ButtonBackgroundPointerOver"] = p.Hover,
            ["ButtonBackgroundPressed"] = p.Line,
            ["ButtonBackgroundDisabled"] = p.Surface,
            ["ButtonBorderBrush"] = p.Line,
            ["ButtonBorderBrushPointerOver"] = p.Muted,
            ["ButtonBorderBrushPressed"] = p.Line,
            ["ButtonForeground"] = p.Text,
            ["ButtonForegroundPointerOver"] = p.Text,
            ["ButtonForegroundPressed"] = p.Text,
            ["ButtonForegroundDisabled"] = p.Muted,

            // Tabs: a soft pill for the selected one, no coloured bar
            ["TabItemHeaderBackgroundSelected"] = p.Hover,
            ["TabItemHeaderBackgroundSelectedPointerOver"] = p.Hover,
            ["TabItemHeaderBackgroundSelectedPressed"] = p.Hover,
            ["TabItemHeaderBackgroundUnselected"] = clear,
            ["TabItemHeaderBackgroundUnselectedPointerOver"] = p.Surface,
            ["TabItemHeaderBackgroundUnselectedPressed"] = p.Surface,
            ["TabItemHeaderForegroundSelected"] = p.Text,
            ["TabItemHeaderForegroundSelectedPointerOver"] = p.Text,
            ["TabItemHeaderForegroundSelectedPressed"] = p.Text,
            ["TabItemHeaderForegroundUnselected"] = p.Muted,
            ["TabItemHeaderForegroundUnselectedPointerOver"] = p.Text,
            ["TabItemHeaderForegroundUnselectedPressed"] = p.Text,
            ["TabItemHeaderSelectedPipeFill"] = clear, // the pill marks the selected tab; the accent bar would just add a second marker

            // Menus and flyouts
            ["MenuFlyoutPresenterBackground"] = p.Surface,
            ["MenuFlyoutPresenterBorderBrush"] = p.Line,
            ["MenuFlyoutItemBackgroundPointerOver"] = p.Hover,
            ["MenuFlyoutItemBackgroundPressed"] = p.Hover,
        };
    }

    static void Brush(ResourceDictionary d, string key, string hex) => d[key] = new SolidColorBrush(Color.Parse(hex));

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R * (1 - t) + b.R * t),
        (byte)Math.Round(a.G * (1 - t) + b.G * t),
        (byte)Math.Round(a.B * (1 - t) + b.B * t));
}
