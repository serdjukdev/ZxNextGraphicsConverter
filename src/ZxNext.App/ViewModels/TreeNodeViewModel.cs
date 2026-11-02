using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.Model;

namespace ZxNext.App.ViewModels;

/// <summary>
/// One node in the project's virtual tree: either one of the four fixed category folders,
/// or a leaf node representing a converted <see cref="ZxNext.Core.Model.GraphicsAsset"/>.
/// </summary>
public partial class TreeNodeViewModel : ObservableObject
{
    /// <summary>Settable (not just constructor-init) so renaming an asset can update the tree display live.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string name;

    /// <summary>What the tree row shows. Same as <see cref="Name"/> for sub-folders and assets; a fixed-set
    /// category root instead shows a short friendly label ("Sprite 4bpp", "Layer2 320x256") rather than its
    /// raw folder path. <see cref="Name"/> / <see cref="FolderPath"/> are unchanged (rename, palette status,
    /// re-quantize dialogs still use the real values).</summary>
    public string DisplayName => IsCategoryRoot
        ? Category switch
        {
            AssetCategory.Sprite4Bpp => "Sprite 4bpp",
            AssetCategory.Sprite8Bpp => "Sprite 8bpp",
            AssetCategory.Tile4Bpp => "Tile 4bpp",
            AssetCategory.Tile8Bpp => "Tile 8bpp",
            AssetCategory.Layer2_256x192 => "Layer2 256x192",
            AssetCategory.Layer2_320x256 => "Layer2 320x256",
            AssetCategory.Layer2_640x256x4 => "Layer2 640x256x4",
            _ => Name
        }
        : Name;

    public bool IsFolder { get; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    /// <summary>Set on every root category folder and on any user-created sub-folder under them.</summary>
    public AssetCategory? Category { get; init; }

    /// <summary>Settable so an asset leaf can be reparented into another folder in place (see <c>ProjectTreeViewModel.MoveAssetToFolder</c>) without tearing its tree container down.</summary>
    public string? FolderPath { get; set; }

    /// <summary>True only for the four fixed root category folders (not for user-created sub-folders) — used to decide where the "add sub-folder" button appears.</summary>
    public bool IsCategoryRoot { get; init; }

    /// <summary>Whether this category root offers "Add sub-folder...": every category does (see
    /// <see cref="AssetCategoryExtensions.SupportsSubfolders"/>). 8bpp sprite/tile, Sprite4Bpp and Tile4Bpp
    /// sub-folders are "banks" of one shared palette; Layer2 sub-folders each get their own.</summary>
    public bool CanHaveSubfolders => IsCategoryRoot && Category is { } c && c.SupportsSubfolders();

    /// <summary>Set only on leaf (asset) nodes.</summary>
    public Guid? AssetId { get; init; }

    /// <summary>Mirrors <see cref="ZxNext.Core.Model.GraphicsAsset.IsReservedBlank"/> — false for folder nodes. Drives whether this row can be a drag SOURCE for reordering (never — it must stay first) and, as a drop TARGET, whether "insert before" is refused (would bump it out of the first slot).</summary>
    public bool IsReservedBlank { get; init; }

    /// <summary>Mirrors <see cref="ZxNext.Core.Model.GraphicsAsset.IsLocked"/> — false for folder nodes and the reserved blank. Drives the row's padlock icon (open/closed) and the "Lock"/"Unlock" context-menu label.</summary>
    [ObservableProperty]
    private bool isLocked;

    /// <summary>Mirrors <see cref="ZxNext.Core.Model.GraphicsAsset.IsPriority"/> — only ever true for a Tile8Bpp asset. Drives the row's green priority arrow and the "Priority"/"No priority" context-menu label.</summary>
    [ObservableProperty]
    private bool isPriority;

    /// <summary>Leaf Tile8Bpp asset node — the only kind that shows the Layer2 priority arrow / "Priority" menu item.</summary>
    public bool IsTile8Bpp => !IsFolder && Category == AssetCategory.Tile8Bpp;

    /// <summary>
    /// Whether this row even offers the "show in Map Editor" checkbox at all: a non-reserved-blank leaf,
    /// Sprite4Bpp or Sprite8Bpp, that lives in a user sub-folder (not the category root, a root sprite is
    /// already individually placeable without this flag). See
    /// <see cref="ZxNext.Core.Model.GraphicsAsset.IsMapRepresentative"/>.
    /// </summary>
    public bool CanBeMapRepresentative =>
        !IsFolder && !IsReservedBlank && Category is { } c && c.IsSprite() && FolderPath != c.ToFolderPath();

    /// <summary>Mirrors <see cref="ZxNext.Core.Model.GraphicsAsset.IsMapRepresentative"/> directly, same pattern as <see cref="IsLocked"/>/<see cref="IsPriority"/>.</summary>
    [ObservableProperty]
    private bool isMapRepresentative;

    /// <summary>Drag-and-drop reorder feedback (ProjectTreeView's code-behind) — a thin line above/below this row showing exactly where the dragged item will land. At most one node in the whole tree has either set true at any time; cleared on drag-leave/drop.</summary>
    [ObservableProperty]
    private bool showDropIndicatorAbove;

    [ObservableProperty]
    private bool showDropIndicatorBelow;

    /// <summary>True on the FOLDER node currently hovered as a "drop the dragged asset here to move it into this folder" target (see ProjectTreeView's CanMoveToFolder). Drives a highlight on the folder chip.</summary>
    [ObservableProperty]
    private bool isMoveDropTarget;

    /// <summary>Small native-size render of the asset's own pixels (see <c>ZxNext.App.Rendering.NextBitmapRenderer</c>) — null for folder nodes. Re-set whenever the underlying asset's pixels/palette could have changed (add/replace, or a live pixel-edit stroke on the currently selected asset); NOT eagerly re-rendered for every OTHER asset sharing a palette slot that just changed colour — same "catches up next time it's shown/touched" tolerance the Pixel Editor/Image Viewer already have (see MainViewModel.RefreshSelectedAssetRender's own doc comment).</summary>
    [ObservableProperty]
    private WriteableBitmap? thumbnail;

    /// <summary>"[N]" where N is this asset's real export index within its own folder — for a root-folder asset the number a metatile/sprite placement stores (and its <c>t8_/s8_</c> label); for a sub-folder asset that sub-folder's own local export index. See <c>ProjectTreeViewModel.RefreshExportIndices</c>. Null for folder nodes. Pre-formatted as a display string (not just the raw int) so nothing in XAML needs a converter/StringFormat to hide it for folders.</summary>
    [ObservableProperty]
    private string? indexLabel;

    /// <summary>
    /// True while this leaf is part of the mouse-driven (Ctrl/Shift-click) multi-selection, handled
    /// entirely in ProjectTreeView's code-behind since WPF's TreeView has no built-in multi-select.
    /// Drives both the row's highlight (via a DataTrigger on the TreeViewItem template) and bulk
    /// delete/re-quantize — independent of <see cref="ProjectTreeViewModel.SelectedNode"/>, which
    /// always tracks the single most-recently-clicked node (the one shown in the edit panels).
    /// </summary>
    [ObservableProperty]
    private bool isMultiSelected;

    /// <summary>Bound two-way to the TreeViewItem's own IsExpanded, so "Expand all"/"Collapse all" can drive it programmatically instead of only reacting to the user's own clicks.</summary>
    [ObservableProperty]
    private bool isExpanded = true;

    /// <summary>Zebra striping for leaf rows: set by <see cref="ProjectTreeViewModel.RestripeFolder"/> to this leaf's parity within its parent folder's children. Ignored for folder rows (they get the chip chrome instead).</summary>
    [ObservableProperty]
    private bool isOddRow;

    // ---- Visual styling (project tree) --------------------------------------------------------
    // A folder row is drawn as a rounded "chip": a tinted fill plus a left accent bar. The tint is
    // by category TYPE, not by individual category: 4bpp (green), 8bpp (blue), Layer2 (violet);
    // a user sub-folder gets a lighter shade of its parent's tint. Leaf rows are flat (zebra only).

    private static readonly SolidColorBrush SubfolderAccent8Bpp = UiPalette.Freeze("#8FB0DC");
    private static readonly SolidColorBrush SubfolderAccentLayer2 = UiPalette.Freeze("#B79ED2");

    private enum TintKind { FourBpp, EightBpp, Layer2 }

    private TintKind? CategoryTint => Category switch
    {
        { } c when c.IsLayer2() => TintKind.Layer2,
        AssetCategory.Sprite8Bpp or AssetCategory.Tile8Bpp => TintKind.EightBpp,
        AssetCategory.Sprite4Bpp or AssetCategory.Tile4Bpp => TintKind.FourBpp,
        _ => null
    };

    /// <summary>True for a folder row that should be drawn as a chip (every category root and sub-folder).</summary>
    public bool IsChipRow => IsFolder && CategoryTint is not null;

    /// <summary>Chip fill for a folder row; transparent (but still hit-testable, so the whole row stays
    /// clickable) for a leaf, which instead gets its zebra / selection background from template triggers.</summary>
    public Brush RowFill => !IsFolder ? Brushes.Transparent : CategoryTint switch
    {
        TintKind.Layer2 => IsCategoryRoot ? UiPalette.Layer2Fill : UiPalette.Layer2FillLight,
        TintKind.EightBpp => IsCategoryRoot ? UiPalette.EightBppFill : UiPalette.EightBppFillLight,
        TintKind.FourBpp => UiPalette.FourBppFill, // 4bpp categories never have sub-folders
        _ => Brushes.Transparent
    };

    /// <summary>Left accent-bar / border colour for a folder row; transparent for a leaf.</summary>
    public Brush RowAccent => !IsFolder ? Brushes.Transparent : CategoryTint switch
    {
        TintKind.Layer2 => IsCategoryRoot ? UiPalette.Layer2Accent : SubfolderAccentLayer2,
        TintKind.EightBpp => IsCategoryRoot ? UiPalette.EightBppAccent : SubfolderAccent8Bpp,
        TintKind.FourBpp => UiPalette.FourBppAccent,
        _ => Brushes.Transparent
    };

    /// <summary>Soft fill for a selected / multi-selected leaf row, tinted to its own category.</summary>
    public Brush SelectionFill => CategoryTint switch
    {
        TintKind.Layer2 => UiPalette.Layer2Selection,
        TintKind.EightBpp => UiPalette.EightBppSelection,
        _ => UiPalette.FourBppSelection
    };

    public TreeNodeViewModel(string name, bool isFolder)
    {
        this.name = name;
        IsFolder = isFolder;
    }
}
