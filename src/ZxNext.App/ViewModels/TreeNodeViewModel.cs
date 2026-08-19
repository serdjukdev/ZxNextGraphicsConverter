using System.Collections.ObjectModel;
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

    /// <summary>Checkbox-driven multi-select for group operations (delete) — independent of the TreeView's own single-item "currently being edited" selection.</summary>
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
