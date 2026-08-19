using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZxNext.App.ViewModels;
using ZxNext.Core.Model;

namespace ZxNext.App.Views;

public partial class ProjectTreeView : UserControl
{
    /// <summary>Raised when a source image is dropped onto a category folder or one of its sub-folders — carries the exact target folder path (not just the category), so 8bpp sub-folders route to their own palette.</summary>
    public event Action<SourceImageViewModel, AssetCategory, string>? AssetDropRequested;

    /// <summary>Raised from the right-click "Rename..." menu item.</summary>
    public event Action<Guid>? RenameAssetRequested;

    /// <summary>Raised from the right-click "Re-quantize..." menu item.</summary>
    public event Action<Guid>? ReQuantizeContextRequested;

    /// <summary>Raised from a user-created folder's right-click "Delete folder..." menu item.</summary>
    public event Action<TreeNodeViewModel>? DeleteFolderRequested;

    public ProjectTreeView()
    {
        InitializeComponent();
    }

    private void Tree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ProjectTreeViewModel vm)
        {
            vm.SelectedNode = e.NewValue as TreeNodeViewModel;
        }
    }

    private void Tree_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(SourceImagesPanelView.DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tree_OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SourceImagesPanelView.DragFormat)) return;
        if (e.Data.GetData(SourceImagesPanelView.DragFormat) is not SourceImageViewModel sourceVm) return;

        var target = FindAncestorDataContext<TreeNodeViewModel>(e.OriginalSource as DependencyObject);
        if (target is { Category: { } category, FolderPath: { } folderPath })
        {
            AssetDropRequested?.Invoke(sourceVm, category, folderPath);
        }
    }

    private void AddSubfolder_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TreeNodeViewModel parentFolder }) return;
        if (DataContext is not ProjectTreeViewModel vm) return;

        var dialog = new NewSubfolderWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SubfolderName))
        {
            vm.CreateSubfolder(parentFolder, dialog.SubfolderName.Trim());
        }
    }

    /// <summary>
    /// Shows only the menu items that make sense for the right-clicked node: Rename/Re-quantize
    /// for a leaf, Delete folder for a user-created sub-folder, nothing at all (menu suppressed)
    /// for one of the four fixed category roots.
    /// </summary>
    private void NodeText_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TreeNodeViewModel node, ContextMenu: { } menu })
        {
            return;
        }

        if (node.IsFolder && node.IsCategoryRoot)
        {
            e.Handled = true;
            return;
        }

        var isLeaf = !node.IsFolder;
        if (menu.Items[0] is MenuItem rename) rename.Visibility = isLeaf ? Visibility.Visible : Visibility.Collapsed;
        if (menu.Items[1] is MenuItem reQuantize) reQuantize.Visibility = isLeaf ? Visibility.Visible : Visibility.Collapsed;
        if (menu.Items[2] is MenuItem deleteFolder) deleteFolder.Visibility = isLeaf ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenameMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (TryGetContextNode(sender, out var node) && node.AssetId is { } assetId)
        {
            RenameAssetRequested?.Invoke(assetId);
        }
    }

    private void ReQuantizeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (TryGetContextNode(sender, out var node) && node.AssetId is { } assetId)
        {
            ReQuantizeContextRequested?.Invoke(assetId);
        }
    }

    private void DeleteFolderMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: TreeNodeViewModel { IsFolder: true, IsCategoryRoot: false } node } } }) return;
        DeleteFolderRequested?.Invoke(node);
    }

    private static bool TryGetContextNode(object sender, out TreeNodeViewModel node)
    {
        node = null!;
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: TreeNodeViewModel found } } }) return false;
        if (found.IsFolder) return false;
        node = found;
        return true;
    }

    private static T? FindAncestorDataContext<T>(DependencyObject? source) where T : class
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: T match }) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }
}
