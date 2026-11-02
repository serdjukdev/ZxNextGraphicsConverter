using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ZxNext.App.Rendering;
using ZxNext.Core.Conversion;
using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.Views;

/// <summary>
/// Sets (or clears) what a Tile4Bpp sub-folder's animation frames overwrite at runtime — the whole of a
/// tilemap tile animation's configuration, see <see cref="TileAnimation"/>. Deliberately a small dialog
/// on the folder rather than an animation editor: the folder's tiles in their own order already ARE the
/// frames, so the only thing the app cannot derive is what they replace.
///
/// <para>
/// Two kinds of target, since both are ordinary: a whole metatile (one frame = all its tiles) or a single
/// tile (one frame = 32 bytes). Both are shown as PICTURES, with the tile numbers underneath, because
/// that is how a person recognises their own art; the numbers matter too, since the game's copy
/// destination is computed from them.
/// </para>
/// </summary>
public partial class TileAnimationTargetWindow : Window
{
    /// <summary>One row of either picker. <paramref name="PreviewSize"/> keeps an 8x8 tile from being blown up to a metatile's size.</summary>
    private record Choice(WriteableBitmap Preview, string Name, string Detail, int PreviewSize, Metatile? Metatile, GraphicsAsset? Tile);

    private readonly ProjectState _project;
    private readonly string _folderPath;

    /// <summary>The metatile the user picked, or null when they picked a single tile or chose to clear. Only meaningful after an OK result.</summary>
    public Metatile? ChosenMetatile { get; private set; }

    /// <summary>The tile the user picked, or null when they picked a metatile or chose to clear.</summary>
    public GraphicsAsset? ChosenTile { get; private set; }

    /// <summary>True when the user pressed "Clear target" rather than OK.</summary>
    public bool Cleared { get; private set; }

    public TileAnimationTargetWindow(ProjectState project, string folderPath)
    {
        InitializeComponent();
        _project = project;
        _folderPath = folderPath;
        FolderText.Text = folderPath;

        var gridSize = project.MetatileGridSize;
        MetatileList.ItemsSource = project.Metatiles
            .Where(m => m.Kind == MetatileKind.FourBpp && !m.IsReservedBlank && (gridSize is null || m.GridSize == gridSize))
            .OrderBy(m => m.SortIndex)
            .Select(m => new Choice(
                TileGridBitmapRenderer.RenderMetatile(m, project),
                m.Name,
                $"{m.Cells.Count} tiles: {string.Join(", ", m.Cells.Select(c => TileNumber(project, c.TileAssetId)))}",
                PreviewSize: 48, m, null))
            .ToList();

        // Root tiles only: a sub-folder tile is itself frame data, never a tile the hardware draws from
        // the tilemap, so it can never be an animation's target. The reserved blank is excluded too --
        // animating "empty" would animate every unpainted cell of every map.
        var rootPath = AssetCategory.Tile4Bpp.ToFolderPath();
        TileList.ItemsSource = project.Assets
            .Where(a => a.Category == AssetCategory.Tile4Bpp && a.FolderPath == rootPath && !a.IsReservedBlank)
            .OrderBy(a => a.SortIndex)
            .Select(a => new Choice(
                NextBitmapRenderer.Render(a, project),
                a.Name,
                $"tile {AssetExportIndexer.PlacementIndexOf(a, project.Assets)}, palette slot {a.PaletteSlotIndex}",
                PreviewSize: 32, null, a))
            .ToList();

        var existing = TileAnimationService.For(project, folderPath);
        ClearButton.IsEnabled = existing is not null;

        if (existing?.SourceMetatileId is { } sourceId)
        {
            MetatileList.SelectedItem = ((List<Choice>)MetatileList.ItemsSource).FirstOrDefault(c => c.Metatile!.Id == sourceId);
        }
        else if (existing is { TargetTileIds.Count: 1 })
        {
            // A one-tile target with no source metatile: that is single-tile mode.
            TileModeRadio.IsChecked = true;
            TileList.SelectedItem = ((List<Choice>)TileList.ItemsSource).FirstOrDefault(c => c.Tile!.Id == existing.TargetTileIds[0]);
        }

        UpdateTexts();
    }

    private static string TileNumber(ProjectState project, Guid tileAssetId)
    {
        var tile = project.Assets.FirstOrDefault(a => a.Id == tileAssetId);
        return tile is null || AssetExportIndexer.IsSubfolderAsset(tile)
            ? "?"
            : AssetExportIndexer.PlacementIndexOf(tile, project.Assets).ToString();
    }

    private bool MetatileMode => MetatileModeRadio.IsChecked == true;

    private void Mode_OnChecked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        MetatileList.Visibility = MetatileMode ? Visibility.Visible : Visibility.Collapsed;
        TileList.Visibility = MetatileMode ? Visibility.Collapsed : Visibility.Visible;
        UpdateTexts();
    }

    private void Choice_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTexts();

    private Choice? Selected => (MetatileMode ? MetatileList.SelectedItem : TileList.SelectedItem) as Choice;

    private void UpdateTexts()
    {
        var selected = Selected;
        OkButton.IsEnabled = selected is not null;

        // Preview against the PICKED target without touching the project: build a throwaway animation with
        // the same targets and ask the service, so the dialog shows exactly what OK would produce.
        var preview = selected is null
            ? TileAnimationService.For(_project, _folderPath)
            : new TileAnimation
            {
                FolderPath = _folderPath,
                SourceMetatileId = selected.Metatile?.Id,
                TargetTileIds = selected.Metatile is { } m
                    ? m.Cells.Select(c => c.TileAssetId).ToList()
                    : [selected.Tile!.Id]
            };

        if (preview is null)
        {
            TargetDetailText.Text = "No target set yet: this folder is treated as a plain tile bank.";
            StateText.Text = "";
            ProblemText.Visibility = Visibility.Collapsed;
            SharedWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        var slots = TileAnimationService.TargetPaletteSlots(_project, preview);
        TargetDetailText.Text = $"Palette slot per frame position: {string.Join(", ", slots.Select(s => s?.ToString() ?? "?"))}. " +
                                "Every frame's tile at a position must use that position's slot, since substituting pixels never changes a map cell's palette.";

        var shared = TileAnimationService.OtherMetatilesSharingTargets(_project, preview);
        SharedWarningText.Visibility = shared.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SharedWarningText.Text = shared.Count == 0
            ? ""
            : $"These tiles are also used by metatile(s) {string.Join(", ", shared.Select(m => m.Name))}. Substituting a tile's pattern is global, so those animate too (which may well be what you want: the same tiles mirrored into other orientations).";

        var info = TileAnimationService.Describe(_project, preview);
        StateText.Text = info.FrameSize == 0
            ? ""
            : $"{info.TileCount} tile(s) in this folder = {info.FrameCount} frame(s) of {info.FrameSize}, {info.FrameSize * 32} bytes per frame.";

        ProblemText.Visibility = info.Problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProblemText.Text = string.Join("\n", info.Problems);
    }

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected) return;
        ChosenMetatile = selected.Metatile;
        ChosenTile = selected.Tile;
        DialogResult = true;
    }

    private void Clear_OnClick(object sender, RoutedEventArgs e)
    {
        Cleared = true;
        ChosenMetatile = null;
        ChosenTile = null;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
