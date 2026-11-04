using System.Windows;
using ZxNext.Core.Model;

namespace ZxNext.App.Views;

/// <summary>
/// Edits one object's (or a whole selection's) width and height: the extended per-object bytes 8-9 of an
/// exported object record. Either can be INHERITED (the "from object/type" tick, which stores null, so
/// changing the object or its type later moves this placement with it) or be pinned on the placement
/// itself. What an inherited field shows is resolved by <see cref="ObjectPlacementSize"/>, the same rule
/// the exporter uses, because the two used to disagree: a 48x8 object exported 48x8 while this dialog
/// still showed its type's 16x16. A palette
/// number used to sit here as well, and no longer does: a sprite's palette travels with the sprite in the
/// object's animation table. Opened from the Map Editor's right-click popup, and applied via
/// <see cref="ViewModels.MapEditorViewModel.ApplyObjectData"/> /
/// <see cref="ViewModels.MapEditorViewModel.ApplyObjectDataToSelection"/>.
/// </summary>
public partial class ObjectDataWindow : Window
{
    /// <summary>Null means "follow the type". Read by the caller only after <see cref="Window.ShowDialog"/> returns true.</summary>
    public int? WidthResult { get; private set; }
    public int? HeightResult { get; private set; }

    private readonly int _inheritedWidth;
    private readonly int _inheritedHeight;

    /// <param name="inheritedSize">The width/height this placement exports while it pins nothing of its own, and a sentence naming where that came from (object, type, or the sprite itself). From MapEditorViewModel.InheritedSizeOf.</param>
    /// <param name="width">Current override, or null for "from object/type".</param>
    /// <param name="batchCount">How many objects this will apply to; 1 for the single-object flow.</param>
    public ObjectDataWindow((int Width, int Height, string SourceText) inheritedSize, int? width, int? height, int batchCount = 1)
    {
        InitializeComponent();
        _inheritedWidth = inheritedSize.Width;
        _inheritedHeight = inheritedSize.Height;

        HeaderText.Text = batchCount > 1
            ? $"Width and height for {batchCount} selected object(s). Whatever is set here is applied to every one of them. The inherited values shown are the first selected object's."
            : "Width and height for this object.";
        HeaderText.Text += inheritedSize.SourceText;

        WidthFromType.IsChecked = width is null;
        HeightFromType.IsChecked = height is null;

        WidthBox.Text = (width ?? _inheritedWidth).ToString();
        HeightBox.Text = (height ?? _inheritedHeight).ToString();

        UpdateEnabledState();
    }

    /// <summary>An inherited field is read-only and shows the inherited value, so the number on screen is always the one that will actually be exported.</summary>
    private void UpdateEnabledState()
    {
        WidthBox.IsEnabled = WidthFromType.IsChecked != true;
        HeightBox.IsEnabled = HeightFromType.IsChecked != true;

        if (WidthFromType.IsChecked == true) WidthBox.Text = _inheritedWidth.ToString();
        if (HeightFromType.IsChecked == true) HeightBox.Text = _inheritedHeight.ToString();
    }

    private void FromType_OnChanged(object sender, RoutedEventArgs e) => UpdateEnabledState();

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        WidthResult = WidthFromType.IsChecked == true ? null : Parse(WidthBox.Text, 255, _inheritedWidth);
        HeightResult = HeightFromType.IsChecked == true ? null : Parse(HeightBox.Text, 255, _inheritedHeight);
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
