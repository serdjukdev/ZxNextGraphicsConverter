using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>Raised from a folder's (root or sub-folder) right-click "Re-quantize folder..." menu item.</summary>
    public event Action<TreeNodeViewModel>? ReQuantizeFolderRequested;

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

    /// <summary>Double-clicking anywhere on a folder row's full-width background toggles expand/collapse (leaf rows: no-op). Always marks the event handled so a double-click inside a deeply nested folder doesn't also bubble up and toggle its ancestors.</summary>
    private void TreeViewItem_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: TreeNodeViewModel { IsFolder: true } node })
        {
            node.IsExpanded = !node.IsExpanded;
        }
        e.Handled = true;
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
    /// for a leaf; Re-quantize folder for any folder (root or sub-folder — a category root can
    /// hold assets directly when no sub-folder was ever created); Delete folder only for a
    /// user-created sub-folder (the four fixed category roots can't be deleted).
    /// </summary>
    private void NodeText_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TreeNodeViewModel node, ContextMenu: { } menu })
        {
            return;
        }

        var isLeaf = !node.IsFolder;
        if (menu.Items[0] is MenuItem rename) rename.Visibility = isLeaf ? Visibility.Visible : Visibility.Collapsed;
        if (menu.Items[1] is MenuItem reQuantize) reQuantize.Visibility = isLeaf ? Visibility.Visible : Visibility.Collapsed;
        if (menu.Items[2] is MenuItem reQuantizeFolder) reQuantizeFolder.Visibility = node.IsFolder ? Visibility.Visible : Visibility.Collapsed;
        if (menu.Items[3] is MenuItem deleteFolder) deleteFolder.Visibility = node.IsFolder && !node.IsCategoryRoot ? Visibility.Visible : Visibility.Collapsed;
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

    private void ReQuantizeFolderMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: TreeNodeViewModel { IsFolder: true } node } } }) return;
        ReQuantizeFolderRequested?.Invoke(node);
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
