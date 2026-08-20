using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.Core.Model;

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
            Roots.Add(folder);
        }
    }

    public TreeNodeViewModel GetCategoryFolder(AssetCategory category) => _categoryFolders[category];

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

    /// <summary>Creates a new sub-folder under a flat-palette category root (each sub-folder gets its own independent palette). No-op (returns null) for anything else, including the two palette-bank categories.</summary>
    public TreeNodeViewModel? CreateSubfolder(TreeNodeViewModel parentFolder, string name)
    {
        if (parentFolder.Category is not { } category || category.UsesPaletteBank())
        {
            return null;
        }

        var path = $"{parentFolder.FolderPath}/{name}";
        if (parentFolder.Children.Any(c => c.IsFolder && c.FolderPath == path)) return null;

        var node = new TreeNodeViewModel(name, isFolder: true) { Category = category, FolderPath = path };
        parentFolder.Children.Add(node);
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
        owner.Children.Add(new TreeNodeViewModel(name, isFolder: true) { Category = owner.Category, FolderPath = path });
    }

    public void AddAssetNode(GraphicsAsset asset)
    {
        var folder = FindFolderNode(asset.FolderPath) ?? _categoryFolders[asset.Category];
        folder.Children.Add(new TreeNodeViewModel(asset.Name, isFolder: false)
        {
            AssetId = asset.Id,
            FolderPath = asset.FolderPath,
            Category = asset.Category
        });
    }

    /// <summary>Removes a leaf node by asset id from whichever category folder (or its sub-folder) holds it (no-op if not found).</summary>
    public void RemoveAssetNode(Guid assetId)
    {
        foreach (var folder in AllFolders())
        {
            var node = folder.Children.FirstOrDefault(c => c.AssetId == assetId);
            if (node is null) continue;

            folder.Children.Remove(node);
            if (SelectedNode == node) SelectedNode = null;
            return;
        }
    }

    public TreeNodeViewModel? FindLeafNode(Guid assetId) =>
        AllFolders().SelectMany(f => f.Children).FirstOrDefault(c => c.AssetId == assetId);

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
    public void ReplaceAssetNode(Guid oldAssetId, GraphicsAsset newAsset)
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
                IsMultiSelected = wasMultiSelected
            };
            folder.Children.Insert(index, node);
            if (wasSelected) SelectedNode = node;
            return;
        }

        AddAssetNode(newAsset); // old node wasn't found for some reason — don't silently drop the new asset
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
