using System.Windows;
using ZxNext.Core.Model;

namespace ZxNext.App.Views;

/// <summary>
/// Edits one object's (or a whole selection's) palette number, width and height: the extended per-object
/// bytes 8-10 of an exported object record. Each of the three can either follow the object's TYPE (the
/// "from type" tick, which stores null, so changing the type later moves this object with it) or be pinned
/// on the object itself. Opened from the Map Editor's right-click popup, and applied via
/// <see cref="ViewModels.MapEditorViewModel.ApplyObjectData"/> /
/// <see cref="ViewModels.MapEditorViewModel.ApplyObjectDataToSelection"/>.
/// </summary>
public partial class ObjectDataWindow : Window
{
    /// <summary>Null means "follow the type". Read by the caller only after <see cref="Window.ShowDialog"/> returns true.</summary>
    public int? PaletteIndexResult { get; private set; }
    public int? WidthResult { get; private set; }
    public int? HeightResult { get; private set; }

    private readonly ObjectType? _type;

    /// <param name="type">The object's type, or null when it has none: an unset field then falls back to palette 0 and the sprite's own pixel size at export time, which the header text spells out.</param>
    /// <param name="paletteIndex">Current override, or null for "from type".</param>
    /// <param name="batchCount">How many objects this will apply to; 1 for the single-object flow.</param>
    public ObjectDataWindow(ObjectType? type, int? paletteIndex, int? width, int? height, int batchCount = 1)
    {
        InitializeComponent();
        _type = type;

        HeaderText.Text = batchCount > 1
            ? $"Palette, width and height for {batchCount} selected object(s). Whatever is set here is applied to every one of them."
            : "Palette, width and height for this object.";
        HeaderText.Text += type is not null
            ? $" Its type \"{type.Name}\" says palette {type.PaletteIndex}, {type.Width}x{type.Height} px."
            : " It has no type, so an unset field exports palette 0 and the sprite's own size.";

        PaletteFromType.IsChecked = paletteIndex is null;
        WidthFromType.IsChecked = width is null;
        HeightFromType.IsChecked = height is null;

        PaletteBox.Text = (paletteIndex ?? type?.PaletteIndex ?? 0).ToString();
        WidthBox.Text = (width ?? type?.Width ?? ObjectType.DefaultSize).ToString();
        HeightBox.Text = (height ?? type?.Height ?? ObjectType.DefaultSize).ToString();

        UpdateEnabledState();
    }

    /// <summary>A field following its type is read-only and shows the type's value, so the number on screen is always the one that will actually be exported.</summary>
    private void UpdateEnabledState()
    {
        PaletteBox.IsEnabled = PaletteFromType.IsChecked != true;
        WidthBox.IsEnabled = WidthFromType.IsChecked != true;
        HeightBox.IsEnabled = HeightFromType.IsChecked != true;

        if (PaletteFromType.IsChecked == true) PaletteBox.Text = (_type?.PaletteIndex ?? 0).ToString();
        if (WidthFromType.IsChecked == true) WidthBox.Text = (_type?.Width ?? ObjectType.DefaultSize).ToString();
        if (HeightFromType.IsChecked == true) HeightBox.Text = (_type?.Height ?? ObjectType.DefaultSize).ToString();
    }

    private void FromType_OnChanged(object sender, RoutedEventArgs e) => UpdateEnabledState();

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        PaletteIndexResult = PaletteFromType.IsChecked == true ? null : Parse(PaletteBox.Text, 15, _type?.PaletteIndex ?? 0);
        WidthResult = WidthFromType.IsChecked == true ? null : Parse(WidthBox.Text, 255, _type?.Width ?? ObjectType.DefaultSize);
        HeightResult = HeightFromType.IsChecked == true ? null : Parse(HeightBox.Text, 255, _type?.Height ?? ObjectType.DefaultSize);
        DialogResult = true;
    }

    /// <summary>Anything unparseable falls back to what the field was showing before, rather than silently becoming 0.</summary>
    private static int Parse(string text, int max, int fallback) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, 0, max) : Math.Clamp(fallback, 0, max);

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
