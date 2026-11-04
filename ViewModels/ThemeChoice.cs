using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlStudio.Models;

namespace SqlStudio.ViewModels;

/// <summary>One row in the theme picker: a name and a small preview of its colours.</summary>
public sealed partial class ThemeChoice : ObservableObject
{
    readonly Action<ThemePalette> _select;

    public ThemeChoice(ThemePalette palette, Action<ThemePalette> select)
    {
        Palette = palette;
        _select = select;
        AppBrush = new SolidColorBrush(Color.Parse(palette.App));
        PanelBrush = new SolidColorBrush(Color.Parse(palette.Panel));
        SurfaceBrush = new SolidColorBrush(Color.Parse(palette.Surface));
        AccentSwatch = new SolidColorBrush(Color.Parse(palette.Fill));
        TextSwatch = new SolidColorBrush(Color.Parse(palette.Text));
    }

    public ThemePalette Palette { get; }
    public string Name => Palette.Name;
    public string Kind => Palette.IsDark ? "Dark" : "Light";
    public IBrush AppBrush { get; }
    public IBrush PanelBrush { get; }
    public IBrush SurfaceBrush { get; }
    public IBrush AccentSwatch { get; }
    public IBrush TextSwatch { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    void Select() => _select(Palette);
}
