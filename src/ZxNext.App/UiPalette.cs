using System.Windows.Media;

namespace ZxNext.App;

/// <summary>
/// One shared accent palette so every panel that colour-codes by asset-category type (the project tree,
/// the export dialog, ...) stays visually consistent. Tints: 4bpp green, 8bpp blue, Layer2 violet, plus a
/// neutral for rows that belong to no single category (a map's grid/metatile/object rows).
/// </summary>
public static class UiPalette
{
    public static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static readonly SolidColorBrush FourBppAccent = Freeze("#77B15E");
    public static readonly SolidColorBrush EightBppAccent = Freeze("#5B84C0");
    public static readonly SolidColorBrush Layer2Accent = Freeze("#8E6DB4");
    public static readonly SolidColorBrush NeutralAccent = Freeze("#A6A2AE");

    public static readonly SolidColorBrush FourBppFill = Freeze("#E7F0E3");
    public static readonly SolidColorBrush EightBppFill = Freeze("#E3EAF4");
    public static readonly SolidColorBrush Layer2Fill = Freeze("#ECE5F1");
    public static readonly SolidColorBrush EightBppFillLight = Freeze("#F1F5FB");
    public static readonly SolidColorBrush Layer2FillLight = Freeze("#F5F1F8");

    public static readonly SolidColorBrush FourBppSelection = Freeze("#D9EAD0");
    public static readonly SolidColorBrush EightBppSelection = Freeze("#D3E0F2");
    public static readonly SolidColorBrush Layer2Selection = Freeze("#E0D4EC");
    public static readonly SolidColorBrush NeutralSelection = Freeze("#E4E2E8");
}
