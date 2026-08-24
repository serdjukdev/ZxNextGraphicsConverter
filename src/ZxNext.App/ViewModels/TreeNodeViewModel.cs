using System.Collections.ObjectModel;
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
    private string name;

    public bool IsFolder { get; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    /// <summary>Set on every root category folder and on any user-created sub-folder under them.</summary>
    public AssetCategory? Category { get; init; }

    public string? FolderPath { get; init; }

    /// <summary>True only for the four fixed root category folders (not for user-created sub-folders) — used to decide where the "add sub-folder" button appears.</summary>
    public bool IsCategoryRoot { get; init; }

    /// <summary>Every category root with its own flat (not palette-bank) palette supports user-created sub-folders — 8bpp sprite/tile and all three Layer2 categories — since each sub-folder gets its own independent palette.</summary>
    public bool CanHaveSubfolders => IsCategoryRoot && Category is not null && !Category.Value.UsesPaletteBank();

    /// <summary>Set only on leaf (asset) nodes.</summary>
    public Guid? AssetId { get; init; }

    /// <summary>Mirrors <see cref="ZxNext.Core.Model.GraphicsAsset.IsReservedBlank"/> — false for folder nodes. Drives whether this row can be a drag SOURCE for reordering (never — it must stay first) and, as a drop TARGET, whether "insert before" is refused (would bump it out of the first slot).</summary>
    public bool IsReservedBlank { get; init; }

    /// <summary>Drag-and-drop reorder feedback (ProjectTreeView's code-behind) — a thin line above/below this row showing exactly where the dragged item will land. At most one node in the whole tree has either set true at any time; cleared on drag-leave/drop.</summary>
    [ObservableProperty]
    private bool showDropIndicatorAbove;

    [ObservableProperty]
    private bool showDropIndicatorBelow;

    /// <summary>Small native-size render of the asset's own pixels (see <c>ZxNext.App.Rendering.NextBitmapRenderer</c>) — null for folder nodes. Re-set whenever the underlying asset's pixels/palette could have changed (add/replace, or a live pixel-edit stroke on the currently selected asset); NOT eagerly re-rendered for every OTHER asset sharing a palette slot that just changed colour — same "catches up next time it's shown/touched" tolerance the Pixel Editor/Image Viewer already have (see MainViewModel.RefreshSelectedAssetRender's own doc comment).</summary>
    [ObservableProperty]
    private WriteableBitmap? thumbnail;

    /// <summary>"[N]" where N is this asset's real export index — the exact same 0-based rank <c>ZxNext.Core.Export.AssetExportIndexer.IndexOf</c> computes at export time (see <c>ProjectTreeViewModel.RefreshExportIndices</c>). Null for folder nodes. Pre-formatted as a display string (not just the raw int) so nothing in XAML needs a converter/StringFormat to hide it for folders.</summary>
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

    public TreeNodeViewModel(string name, bool isFolder)
    {
        this.name = name;
        IsFolder = isFolder;
    }
}
