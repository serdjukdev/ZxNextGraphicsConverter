using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.App.Rendering;
using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>
/// Owns the left-pane project tree: the four fixed category folders, populated with real
/// converted-asset leaf nodes as the user drops source images onto them. The two 8bpp
/// categories also support one level of user-created sub-folders (each one its own flat
/// palette, per the project's data model).
/// </summary>
public partial class ProjectTreeViewModel : ObservableObject
{
    private readonly Dictionary<AssetCategory, TreeNodeViewModel> _categoryFolders = [];

    public ObservableCollection<TreeNodeViewModel> Roots { get; } = [];

    [ObservableProperty]
    private TreeNodeViewModel? selectedNode;

    /// <summary>
    /// Raised whenever a node's tree item is torn down and rebuilt in a Remove+Insert (re-quantize's
    /// <see cref="ReplaceAssetNode"/>, or a cross-folder move in <see cref="MoveAssetToFolder"/>) — the
    /// view uses it to re-focus that node's new container, which also scrolls it back into view instead of
    /// letting the ScrollViewer snap to the top. Deliberately not tied to <see cref="SelectedNode"/>
    /// changes in general (see <c>ProjectTreeView.OnNodeReplacedInPlace</c> for why re-focusing on every
    /// selection change was a bug).
    /// </summary>
    public event Action<TreeNodeViewModel>? NodeReplacedInPlace;

    public ProjectTreeViewModel()
    {
        foreach (var category in Enum.GetValues<AssetCategory>())
        {
            var folderPath = category.ToFolderPath();
            var folder = new TreeNodeViewModel(folderPath, isFolder: true)
            {
                Category = category,
                FolderPath = folderPath,
                IsCategoryRoot = true
            };
            _categoryFolders[category] = folder;
            WireFolder(folder);
            Roots.Add(folder);
        }
    }

    public TreeNodeViewModel GetCategoryFolder(AssetCategory category) => _categoryFolders[category];

    /// <summary>Keeps a folder's leaf-row zebra striping correct through every add / remove / reorder / replace,
    /// from one subscription rather than a call at each mutation site.</summary>
    private static void WireFolder(TreeNodeViewModel folder) =>
        folder.Children.CollectionChanged += (_, _) => RestripeFolder(folder);

    private static void RestripeFolder(TreeNodeViewModel folder)
    {
        var leafIndex = 0;
        foreach (var child in folder.Children)
        {
            if (child.IsFolder) continue;
            child.IsOddRow = (leafIndex++ & 1) == 1;
        }
    }

    [RelayCommand]
    private void ExpandAll() => SetAllExpanded(true);

    [RelayCommand]
    private void CollapseAll() => SetAllExpanded(false);

    private void SetAllExpanded(bool expanded)
    {
        foreach (var root in Roots)
        {
            root.IsExpanded = expanded;
            foreach (var child in root.Children) child.IsExpanded = expanded;
        }
    }

    /// <summary>Clears every category folder's children — sub-folders and all — (used when opening a different project or starting a new one).</summary>
    public void Reset()
    {
        foreach (var folder in Roots) folder.Children.Clear();
        SelectedNode = null;
    }

    /// <summary>Creates a new sub-folder under a category root that supports them (every category except Tile4Bpp — see <see cref="AssetCategoryExtensions.SupportsSubfolders"/>). No-op (returns null) otherwise. The palette (a shared flat palette for 8bpp, the one 16-slot bank for Sprite4Bpp, an independent flat palette for Layer2) is created lazily on the first import into the sub-folder.</summary>
    public TreeNodeViewModel? CreateSubfolder(TreeNodeViewModel parentFolder, string name)
    {
        if (parentFolder.Category is not { } category || !category.SupportsSubfolders())
        {
            return null;
        }

        var path = $"{parentFolder.FolderPath}/{name}";
        if (parentFolder.Children.Any(c => c.IsFolder && c.FolderPath == path)) return null;

        var node = new TreeNodeViewModel(name, isFolder: true) { Category = category, FolderPath = path };
        WireFolder(node);
        int insertIdx = 0;
        for (int i = 0; i < parentFolder.Children.Count; i++)
        {
            if (!parentFolder.Children[i].IsFolder) break;
            if (string.Compare(parentFolder.Children[i].FolderPath, path, StringComparison.Ordinal) < 0)
                insertIdx = i + 1;
            else
                break;
        }
        parentFolder.Children.Insert(insertIdx, node);
        return node;
    }

    /// <summary>Ensures a folder path exists in the tree (creating a sub-folder node if needed) — used to rebuild sub-folder structure from a loaded project, which only implicitly records folder paths via asset/palette data.</summary>
    public void EnsureFolderPath(string path)
    {
        var root = Roots.FirstOrDefault(r => r.FolderPath == path);
        if (root is not null) return; // it's a category root itself, nothing to create

        var owner = Roots.FirstOrDefault(r => path.StartsWith(r.FolderPath + "/", StringComparison.Ordinal));
        if (owner is null) return;
        if (owner.Children.Any(c => c.IsFolder && c.FolderPath == path)) return;

        var name = path[(owner.FolderPath!.Length + 1)..];
        var node = new TreeNodeViewModel(name, isFolder: true) { Category = owner.Category, FolderPath = path };
        WireFolder(node);
        owner.Children.Add(node);
    }

    /// <summary>Tree display order is plain insertion order, NOT SortIndex — a caller that just gave this asset a lower SortIndex than its siblings (e.g. moving a transparent tile to export index 0) needs <paramref name="insertAtFront"/> to actually show that, otherwise it silently lands at the end like any other new node.</summary>
    public void AddAssetNode(GraphicsAsset asset, ProjectState project, bool insertAtFront = false)
    {
        var folder = FindFolderNode(asset.FolderPath) ?? _categoryFolders[asset.Category];
        var node = new TreeNodeViewModel(asset.Name, isFolder: false)
        {
            AssetId = asset.Id,
            FolderPath = asset.FolderPath,
            Category = asset.Category,
            Thumbnail = NextBitmapRenderer.Render(asset, project),
            IsReservedBlank = asset.IsReservedBlank,
            IsLocked = asset.IsLocked,
            IsPriority = asset.IsPriority,
            IsMapRepresentative = asset.IsMapRepresentative
        };
        if (insertAtFront) folder.Children.Insert(0, node);
        else folder.Children.Add(node);
        RefreshExportIndices(asset.Category, project);
    }

    /// <summary>Removes a leaf node by asset id from whichever category folder (or its sub-folder) holds it (no-op if not found).</summary>
    public void RemoveAssetNode(Guid assetId, ProjectState project)
    {
        foreach (var folder in AllFolders())
        {
            var node = folder.Children.FirstOrDefault(c => c.AssetId == assetId);
            if (node is null) continue;

            var category = node.Category;
            folder.Children.Remove(node);
            if (SelectedNode == node) SelectedNode = null;
            if (category is { } removedCategory) RefreshExportIndices(removedCategory, project);
            return;
        }
    }

    /// <summary>
    /// Re-derives every leaf node's <see cref="TreeNodeViewModel.IndexLabel"/> ("[N]") for one category —
    /// the SAME 0-based number that asset gets in the exported .asm, so what's shown here can never drift
    /// from what actually gets exported. Numbering is now PER FOLDER: a root-folder asset's "[N]" is its
    /// metatile/sprite placement index (and its <c>t8_/s8_</c> label); a sub-folder asset's "[N]" is that
    /// sub-folder's own local export index (its <c>t8_&lt;subfolder&gt;_&lt;name&gt;</c> label). Touches
    /// every leaf, not just the one that changed, because adding/removing/reordering ONE asset shifts the
    /// number of every OTHER asset in that folder that sorts after it.
    /// </summary>
    public void RefreshExportIndices(AssetCategory category, ProjectState project)
    {
        foreach (var folder in AllFolders().Where(f => f.Category == category))
        {
            var folderAssets = project.Assets
                .Where(a => a.Category == category && a.FolderPath == folder.FolderPath)
                .OrderBy(a => a.SortIndex)
                .ToList();

            foreach (var node in folder.Children)
            {
                if (node.AssetId is not { } id) continue;
                var pos = folderAssets.FindIndex(a => a.Id == id);
                node.IndexLabel = pos < 0 ? null : $"[{pos}]";
            }
        }
    }

    public TreeNodeViewModel? FindLeafNode(Guid assetId) =>
        AllFolders().SelectMany(f => f.Children).FirstOrDefault(c => c.AssetId == assetId);

    /// <summary>
    /// Drag-and-drop reorder: moves <paramref name="dragged"/> to sit immediately before/after
    /// <paramref name="target"/> within whichever folder currently holds BOTH of them. The caller (see
    /// <see cref="MainViewModel.ReorderAsset"/>) is responsible for validating category/folder
    /// compatibility and the reserved-blank invariant FIRST — this method only knows how to move a node
    /// that's already confirmed to belong next to the other one; it returns null (no-op) if they don't
    /// share a parent folder or either wasn't found, rather than guessing. Returns the shared parent so
    /// the caller can read back the resulting order to recompute SortIndex (see
    /// <see cref="ZxNext.Core.Conversion.AssetReorderService"/>).
    ///
    /// Uses <see cref="ObservableCollection{T}.Move"/>, deliberately NOT Remove+Insert — WPF's TreeView
    /// reacts to a Move collection-changed action by repositioning the EXISTING TreeViewItem container,
    /// but to a Remove+Add pair by tearing the old container down and generating a brand-new one (same
    /// "loses its container" hazard <see cref="ReplaceAssetNode"/>'s own doc comment describes for
    /// re-quantize). For a drag reorder specifically, that teardown was observed to reset the WHOLE
    /// TreeView's scroll position back to the top on every single drop (presumably because the dragged
    /// item's container — likely still focused/selected from the mouse-down that started the drag — gets
    /// destroyed without SelectedNode itself ever changing VALUE, so nothing re-triggers the tree's
    /// existing focus-follow logic in ProjectTreeView.OnTreeViewModelPropertyChanged), which is exactly
    /// the "scroll all the way back down after every move" complaint this fixes.
    /// </summary>
    public TreeNodeViewModel? MoveLeafNode(TreeNodeViewModel dragged, TreeNodeViewModel target, bool insertAfter)
    {
        if (dragged == target) return null;
        var parent = AllFolders().FirstOrDefault(f => f.Children.Contains(dragged) && f.Children.Contains(target));
        if (parent is null) return null;

        var oldIndex = parent.Children.IndexOf(dragged);
        var targetIndex = parent.Children.IndexOf(target);
        // ObservableCollection.Move's newIndex is interpreted AFTER oldIndex's item is conceptually
        // removed — if dragged currently sits before target, target's own position shifts down by one
        // once dragged is out of the way.
        var targetIndexAfterRemoval = oldIndex < targetIndex ? targetIndex - 1 : targetIndex;
        var newIndex = insertAfter ? targetIndexAfterRemoval + 1 : targetIndexAfterRemoval;
        parent.Children.Move(oldIndex, newIndex);
        return parent;
    }

    /// <summary>Re-derives every asset's <see cref="GraphicsAsset.SortIndex"/> for one category from the
    /// CURRENT tree layout: folders in ordinal path order (so the category root, being a path prefix, comes
    /// first), each folder's leaves in their tree order, numbered 0..n. Safe because a GraphicsAsset's
    /// SortIndex is a pure sort key (never an exported literal — see AssetExportIndexer), so a full
    /// renumber only re-derives what export/placement/[N] already compute from order. Used after a
    /// cross-folder move, where the moved asset needs a slot between two others that may be numerically
    /// adjacent.</summary>
    private void RenumberCategory(AssetCategory category, ProjectState project)
    {
        var ordered = AllFolders()
            .Where(f => f.Category == category)
            .OrderBy(f => f.FolderPath, StringComparer.Ordinal)
            .SelectMany(f => f.Children)
            .Select(n => n.AssetId is { } id ? project.Assets.FirstOrDefault(a => a.Id == id) : null)
            .Where(a => a is not null)
            .ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i]!.SortIndex = i;
    }

    /// <summary>Moves a leaf asset node into a DIFFERENT folder of the same category (only offered for the
    /// shared-palette sprite/8bpp-tile categories — the palette is shared so no re-quantize is needed).
    /// <paramref name="targetNeighbourId"/> null appends to the end of the target folder; otherwise the
    /// node lands immediately before/after that neighbour. Reparents the node, updates both FolderPath
    /// properties, renumbers the whole category's SortIndex from the new layout, and refreshes the
    /// per-folder "[N]" labels. The caller checks reference integrity first. Returns false on any invalid
    /// combination.</summary>
    public bool MoveAssetToFolder(Guid assetId, string targetFolderPath, ProjectState project, Guid? targetNeighbourId = null, bool insertAfter = false)
    {
        var node = FindLeafNode(assetId);
        var source = node is null ? null : AllFolders().FirstOrDefault(f => f.Children.Contains(node));
        var target = AllFolders().FirstOrDefault(f => f.FolderPath == targetFolderPath);
        var asset = project.Assets.FirstOrDefault(a => a.Id == assetId);
        if (node is null || source is null || target is null || asset is null || ReferenceEquals(source, target)) return false;

        source.Children.Remove(node);

        var insertAt = target.Children.Count;
        if (targetNeighbourId is { } neighbourId)
        {
            var neighbourIndex = -1;
            for (var i = 0; i < target.Children.Count; i++)
            {
                if (target.Children[i].AssetId == neighbourId) { neighbourIndex = i; break; }
            }
            if (neighbourIndex >= 0) insertAt = insertAfter ? neighbourIndex + 1 : neighbourIndex;
        }
        target.Children.Insert(Math.Clamp(insertAt, 0, target.Children.Count), node);

        node.FolderPath = targetFolderPath;
        asset.FolderPath = targetFolderPath;

        RenumberCategory(asset.Category, project);
        RefreshExportIndices(asset.Category, project);
        SelectedNode = node;
        // The reparent above is a Remove+Insert, which makes WPF's TreeView tear the node's container
        // down and rebuild it (and the ScrollViewer snap to the top). Same recovery as re-quantize:
        // the view re-focuses the new container, which scrolls it back into view where it landed.
        NodeReplacedInPlace?.Invoke(node);
        return true;
    }

    /// <summary>
    /// Swaps one leaf node for another representing a re-quantized replacement asset, keeping its
    /// position in the list (re-quantize would otherwise always append to the end via
    /// Remove+Add, which is confusing — the tile visibly "jumps" to the bottom for no reason a
    /// user can see). Re-selects it if it was selected, and carries over its multi-selection
    /// highlight if it was part of a bulk re-quantize selection — the replacement is a brand-new
    /// TreeNodeViewModel instance, so without this its row would silently lose its blue highlight
    /// (and, for the single-selected case, keyboard focus would fall back to the nearest surviving
    /// ancestor — the containing folder — since the old, focused container gets torn down by the
    /// Remove/Insert; ProjectTreeView's SelectedNode-changed handler re-focuses the new container).
    /// </summary>
    public void ReplaceAssetNode(Guid oldAssetId, GraphicsAsset newAsset, ProjectState project)
    {
        foreach (var folder in AllFolders())
        {
            var index = -1;
            for (var i = 0; i < folder.Children.Count; i++)
            {
                if (folder.Children[i].AssetId != oldAssetId) continue;
                index = i;
                break;
            }
            if (index < 0) continue;

            var oldNode = folder.Children[index];
            var wasSelected = SelectedNode == oldNode;
            var wasMultiSelected = oldNode.IsMultiSelected;
            folder.Children.RemoveAt(index);
            var node = new TreeNodeViewModel(newAsset.Name, isFolder: false)
            {
                AssetId = newAsset.Id,
                FolderPath = newAsset.FolderPath,
                Category = newAsset.Category,
                IsMultiSelected = wasMultiSelected,
                Thumbnail = NextBitmapRenderer.Render(newAsset, project),
                IsReservedBlank = newAsset.IsReservedBlank,
                IsLocked = newAsset.IsLocked,
                IsPriority = newAsset.IsPriority,
                IsMapRepresentative = newAsset.IsMapRepresentative
            };
            folder.Children.Insert(index, node);
            if (wasSelected)
            {
                SelectedNode = node;
                NodeReplacedInPlace?.Invoke(node); // let the view recover focus the Remove+Insert above just dropped
            }
            RefreshExportIndices(newAsset.Category, project); // a fresh node instance starts with no IndexLabel of its own, even though SortIndex (so rank) is unchanged
            return;
        }

        AddAssetNode(newAsset, project); // old node wasn't found for some reason — don't silently drop the new asset
    }

    /// <summary>Removes a user-created sub-folder node (and, being an ObservableCollection subtree, every leaf node under it) from its owning category root. No-op for anything that isn't a non-root folder.</summary>
    public void RemoveFolderNode(TreeNodeViewModel folder)
    {
        if (!folder.IsFolder || folder.IsCategoryRoot) return;

        foreach (var root in Roots)
        {
            if (!root.Children.Remove(folder)) continue;
            if (SelectedNode == folder || (SelectedNode is not null && folder.Children.Contains(SelectedNode))) SelectedNode = null;
            return;
        }
    }

    /// <summary>Every leaf node currently in the mouse-driven (Ctrl/Shift-click) multi-selection — used for bulk delete/re-quantize and row highlighting.</summary>
    public List<TreeNodeViewModel> GetMultiSelectedLeaves() =>
        AllFolders().SelectMany(f => f.Children).Where(c => c.IsMultiSelected && c.AssetId.HasValue).ToList();

    /// <summary>Asset ids for <see cref="GetMultiSelectedLeaves"/> — used for a group operation (delete).</summary>
    public List<Guid> GetMultiSelectedAssetIds() =>
        GetMultiSelectedLeaves().Select(c => c.AssetId!.Value).ToList();

    /// <summary>Clears every leaf node's multi-selection highlight (after a delete, or when switching projects).</summary>
    public void ClearMultiSelection()
    {
        foreach (var child in AllFolders().SelectMany(f => f.Children)) child.IsMultiSelected = false;
    }

    /// <summary>
    /// Every leaf node in the same top-to-bottom order the tree currently displays them, skipping
    /// anything inside a collapsed folder (invisible items can't be part of a Shift-click range).
    /// Used to compute a Shift-click range between two leaves.
    /// </summary>
    public List<TreeNodeViewModel> GetVisibleLeavesInOrder()
    {
        var result = new List<TreeNodeViewModel>();
        foreach (var root in Roots)
        {
            if (!root.IsExpanded) continue;
            foreach (var child in root.Children)
            {
                if (!child.IsFolder)
                {
                    result.Add(child);
                    continue;
                }

                if (!child.IsExpanded) continue;
                foreach (var grandchild in child.Children)
                {
                    if (!grandchild.IsFolder) result.Add(grandchild);
                }
            }
        }
        return result;
    }

    private TreeNodeViewModel? FindFolderNode(string folderPath) =>
        AllFolders().FirstOrDefault(f => f.FolderPath == folderPath);

    /// <summary>Every folder node: the four roots plus their direct (8bpp-only, one level deep) sub-folders.</summary>
    private IEnumerable<TreeNodeViewModel> AllFolders() =>
        Roots.SelectMany(root => new[] { root }.Concat(root.Children.Where(c => c.IsFolder)));
}
