using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using ZxNext.App.ViewModels;
using ZxNext.Core.Model;

namespace ZxNext.App.Views;

public partial class ProjectTreeView : UserControl
{
    /// <summary>Drag format for reordering a tile/sprite row within the tree — carries the dragged TreeNodeViewModel itself (distinct from <see cref="SourceImagesPanelView.DragFormat"/>, which carries a SourceImageViewModel and always means "import this").</summary>
    public const string DragFormat = "ZxNext.TreeAssetReorder";

    /// <summary>Raised when a source image is dropped onto a category folder or one of its sub-folders — carries the exact target folder path (not just the category), so 8bpp sub-folders route to their own palette.</summary>
    public event Action<SourceImageViewModel, AssetCategory, string>? AssetDropRequested;

    /// <summary>Raised when a tile/sprite row is dropped onto another one to reorder — see <see cref="MainViewModel.ReorderAsset"/> for what actually happens with it.</summary>
    public event Action<Guid, Guid, bool>? AssetReorderRequested;

    /// <summary>Raised from the right-click "Rename..." menu item.</summary>
    public event Action<Guid>? RenameAssetRequested;

    /// <summary>Raised from the right-click "Re-quantize..." menu item (single asset — no active multi-selection).</summary>
    public event Action<Guid>? ReQuantizeContextRequested;

    /// <summary>Raised from the right-click "Re-quantize N selected..." menu item — every id in the current multi-selection, all sharing one category (mixed-category selections disable this item).</summary>
    public event Action<List<Guid>>? ReQuantizeSelectedRequested;

    /// <summary>Raised from the right-click "Delete"/"Delete N selected..." menu item — the actual target set (single node or the whole multi-selection) is resolved by the same logic as the Delete key, in <see cref="MainViewModel.DeleteSelectedCommand"/>.</summary>
    public event Action? DeleteRequested;

    /// <summary>Raised from a user-created folder's right-click "Delete folder..." menu item.</summary>
    public event Action<TreeNodeViewModel>? DeleteFolderRequested;

    /// <summary>Raised from a folder's (root or sub-folder) right-click "Re-quantize folder..." menu item.</summary>
    public event Action<TreeNodeViewModel>? ReQuantizeFolderRequested;

    /// <summary>
    /// The last plain- or Ctrl-clicked leaf — the anchor a following Shift-click ranges from.
    /// Cleared whenever selection collapses to a single node (plain click, or a folder click).
    /// </summary>
    private TreeNodeViewModel? _multiSelectAnchor;

    /// <summary>
    /// True whenever WE are the ones calling `.Focus()` on a TreeViewItem (from a mouse click, or from
    /// <see cref="OnTreeViewModelPropertyChanged"/>'s re-focus) — set because focusing a TreeViewItem
    /// can itself flip that item's NATIVE IsSelected to true (a documented WPF TreeView quirk), which
    /// bubbles into TreeView.SelectedItemChanged firing SYNCHRONOUSLY, re-entering
    /// <see cref="Tree_OnSelectedItemChanged"/> mid-click. Without this guard, that echo called
    /// SelectSingle right on top of whatever Ctrl/Shift-click had just built, collapsing the whole
    /// multi-selection down to one node on every single click.
    /// </summary>
    private bool _suppressNativeSelectionSync;

    /// <summary>Coalesces <see cref="OnTreeViewModelPropertyChanged"/>'s deferred re-focus so a burst of SelectedNode changes (e.g. holding an arrow key) schedules only ONE pending action instead of one per change.</summary>
    private bool _focusFollowScheduled;

    /// <summary>Armed in <see cref="Tree_OnPreviewMouseLeftButtonDown"/> when the click landed on a draggable leaf (not a folder, not the reserved blank) — same "record start position, only actually start DoDragDrop once the mouse clears the OS drag threshold" pattern as <see cref="SourceImagesPanelView"/>'s own drag source.</summary>
    private Point _dragStart;
    private bool _dragArmed;
    private TreeNodeViewModel? _dragCandidate;

    /// <summary>The node currently showing a drop-indicator line (see <see cref="TreeNodeViewModel.ShowDropIndicatorAbove"/>/<see cref="TreeNodeViewModel.ShowDropIndicatorBelow"/>) — at most one at a time, cleared via <see cref="ClearDropIndicator"/> whenever the hovered target changes, the drag leaves the tree, or it completes.</summary>
    private TreeNodeViewModel? _dropIndicatorNode;

    /// <summary>Runs only while a reorder drag from THIS control is in progress (started/stopped around the blocking <see cref="DragDrop.DoDragDrop"/> call in <see cref="Tree_OnPreviewMouseMove"/> — WPF still pumps the dispatcher during that call, so a timer keeps ticking) — scrolls the tree while the mouse hovers near its top/bottom edge, since DragOver alone only fires on mouse movement and a long list needs scrolling even while the mouse sits still near an edge.</summary>
    private DispatcherTimer? _autoScrollTimer;
    private Point _lastDragPosition;

    public ProjectTreeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ProjectTreeViewModel oldVm) oldVm.PropertyChanged -= OnTreeViewModelPropertyChanged;
        if (e.NewValue is ProjectTreeViewModel newVm) newVm.PropertyChanged += OnTreeViewModelPropertyChanged;
    }

    /// <summary>
    /// Keeps keyboard focus following whichever node is actually selected — needed because
    /// replacing a node in place (re-quantize) destroys the old TreeViewItem container via
    /// Remove+Insert, and WPF falls back focus to the nearest surviving ancestor (the containing
    /// folder) when the focused element is torn down. Runs at Background priority so the
    /// ObservableCollection change that triggered this has already flowed through to a freshly
    /// generated container by the time <see cref="FindContainer"/> looks for it.
    /// Deliberately does NOT capture "which node" up front, and coalesces via
    /// <see cref="_focusFollowScheduled"/> to at most one pending action — a burst of SelectedNode
    /// changes (holding an arrow key through a folder with hundreds/thousands of items) used to
    /// schedule one of these PER change, each capturing whatever was selected at that instant; by the
    /// time they all finally ran (Background priority runs after the whole keystroke burst), they
    /// replayed the ENTIRE selection history one Focus() call at a time — visually indistinguishable
    /// from the tree scrolling back through every item the user had just scrolled past. Re-reading
    /// SelectedNode fresh inside the deferred action instead makes every one of them converge on
    /// whatever is ACTUALLY selected by the time it runs, so at most one real re-focus ever happens
    /// per burst, however many change notifications fired during it.
    /// </summary>
    private void OnTreeViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectTreeViewModel.SelectedNode)) return;
        if (_focusFollowScheduled) return;
        _focusFollowScheduled = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _focusFollowScheduled = false;
            if (DataContext is not ProjectTreeViewModel { SelectedNode: { } current }) return;

            _suppressNativeSelectionSync = true;
            try
            {
                FindContainer(current)?.Focus();
            }
            finally
            {
                _suppressNativeSelectionSync = false;
            }
        }));
    }

    /// <summary>Locates the realized TreeViewItem container for a node up to two levels deep (root category folder, or one of its sub-folders' leaves) — matches this tree's actual max nesting.</summary>
    private TreeViewItem? FindContainer(TreeNodeViewModel node)
    {
        foreach (var rootItem in Tree.Items)
        {
            if (Tree.ItemContainerGenerator.ContainerFromItem(rootItem) is not TreeViewItem rootTvi) continue;
            if (ReferenceEquals(rootItem, node)) return rootTvi;

            rootTvi.UpdateLayout();
            if (rootTvi.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem directChildTvi) return directChildTvi;

            foreach (var child in ((TreeNodeViewModel)rootItem).Children)
            {
                if (rootTvi.ItemContainerGenerator.ContainerFromItem(child) is not TreeViewItem childFolderTvi) continue;
                childFolderTvi.UpdateLayout();
                if (childFolderTvi.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem grandchildTvi) return grandchildTvi;
            }
        }
        return null;
    }

    /// <summary>
    /// Fires only from keyboard arrow-key navigation now — all mouse clicks are fully handled (and
    /// marked Handled) in <see cref="Tree_OnPreviewMouseLeftButtonDown"/>, which never lets WPF's own
    /// native TreeViewItem.IsSelected/TreeView.SelectedItem machinery run. Routes through
    /// <see cref="SelectSingle"/> so keyboard navigation stays consistent with mouse clicks — before
    /// this, a native IsSelected-based highlight Trigger existed ALONGSIDE the IsMultiSelected one,
    /// and native IsSelected — once set (e.g. by an earlier click before this rewrite, or just by
    /// residual TreeView behavior) — stuck on a node forever with nothing to ever clear it, so
    /// Ctrl-clicking that same already-"selected" node to toggle IsMultiSelected off did nothing
    /// visible: the IsSelected trigger kept it highlighted regardless. Now IsMultiSelected is the
    /// SOLE source of truth for the row highlight, for both mouse and keyboard.
    /// </summary>
    private void Tree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressNativeSelectionSync) return; // just an echo of our own Focus() call below — we already handled this click ourselves
        if (DataContext is not ProjectTreeViewModel vm) return;
        if (e.NewValue is TreeNodeViewModel node) SelectSingle(vm, node);
        else vm.SelectedNode = null;
    }

    /// <summary>
    /// WPF's TreeView has no built-in multi-select, so mouse selection is entirely hand-rolled here
    /// — including double-click. Deliberately NOT using Control's built-in MouseDoubleClick: that
    /// event is raised from Control.OnMouseLeftButtonDown, a CLASS handler that runs once per
    /// ANCESTOR TreeViewItem the click bubbles through (a leaf's own no-op instance does not stop it
    /// — only Handled=true set on THIS PreviewMouseLeftButtonDown, before the bubble phase even
    /// starts, can), so relying on it meant double-clicking a leaf ALSO toggled its parent folder's
    /// expand state. Handling everything here — and marking every click Handled — avoids that
    /// entirely. Also deliberately does NOT special-case e.ClickCount &gt;= 2 for leaves: a rapid
    /// Ctrl-click sequence (toggling several files in quick succession) can easily land within the
    /// OS's double-click time/distance window and get ClickCount 2+ despite the user never intending
    /// a double-click, so a leaf always goes through the same modifier-aware selection logic
    /// regardless of click count — only a folder with NO modifiers held treats ClickCount &gt;= 2 as
    /// expand/collapse.
    /// </summary>
    private void Tree_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ProjectTreeViewModel vm) return;
        var originalSource = e.OriginalSource as DependencyObject;
        if (FindAncestorDataContext<TreeNodeViewModel>(originalSource) is not { } node) return;
        if (FindAncestor<ToggleButton>(originalSource) is not null) return; // let the expand arrow toggle itself normally

        _dragStart = e.GetPosition(null);
        _dragCandidate = node;
        _dragArmed = !node.IsFolder && !node.IsReservedBlank; // the reserved blank must always stay first — never a valid drag source

        if (node.IsFolder && e.ClickCount >= 2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            node.IsExpanded = !node.IsExpanded;
        }
        else if (!node.IsFolder)
        {
            HandleLeafClick(vm, node, Keyboard.Modifiers);
        }
        else
        {
            SelectSingle(vm, node);
        }

        e.Handled = true;
        _suppressNativeSelectionSync = true;
        try
        {
            FindAncestor<TreeViewItem>(originalSource)?.Focus();
        }
        finally
        {
            _suppressNativeSelectionSync = false;
        }
    }

    /// <summary>Fires continuously while a mouse button is held over the tree — only actually starts a drag once armed (see <see cref="Tree_OnPreviewMouseLeftButtonDown"/>) and past the OS's drag distance threshold, same pattern as <see cref="SourceImagesPanelView"/>'s drag source.</summary>
    private void Tree_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not { } dragged) return;

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragArmed = false;
        StartAutoScroll();
        try
        {
            DragDrop.DoDragDrop(Tree, new DataObject(DragFormat, dragged), DragDropEffects.Move);
        }
        finally
        {
            StopAutoScroll();
            ClearDropIndicator();
        }
    }

    private void StartAutoScroll()
    {
        _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        _autoScrollTimer.Tick += AutoScrollTimer_OnTick;
        _autoScrollTimer.Start();
    }

    private void StopAutoScroll()
    {
        if (_autoScrollTimer is null) return;
        _autoScrollTimer.Stop();
        _autoScrollTimer.Tick -= AutoScrollTimer_OnTick;
        _autoScrollTimer = null;
    }

    /// <summary>Scrolls the tree a little whenever the last-known drag position (updated in <see cref="Tree_OnDragOver"/>) sits within a small band of the tree's own top/bottom edge — ticks continuously (see <see cref="_autoScrollTimer"/>'s own doc comment) so hovering near an edge without moving still scrolls.</summary>
    private void AutoScrollTimer_OnTick(object? sender, EventArgs e)
    {
        if (FindVisualChild<ScrollViewer>(Tree) is not { } scrollViewer) return;
        const double edge = 28;
        const double step = 14;
        if (_lastDragPosition.Y < edge) scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - step));
        else if (_lastDragPosition.Y > Tree.ActualHeight - edge) scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + step);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void ClearDropIndicator()
    {
        if (_dropIndicatorNode is null) return;
        _dropIndicatorNode.ShowDropIndicatorAbove = false;
        _dropIndicatorNode.ShowDropIndicatorBelow = false;
        _dropIndicatorNode = null;
    }

    /// <summary>Which half of the hovered row's container the pointer is over — shared by DragOver (live indicator/cursor feedback) and Drop (the actual decision), so they can never disagree. Defaults to "after" when the container can't be resolved (e.g. a stale/torn-down visual mid-drag) rather than silently doing nothing.</summary>
    private static bool IsInsertAfter(DependencyObject? originalSource, DragEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(originalSource) is { ActualHeight: > 0 } container)
        {
            return e.GetPosition(container).Y > container.ActualHeight / 2;
        }
        return true;
    }

    private void HandleLeafClick(ProjectTreeViewModel vm, TreeNodeViewModel node, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Shift) && _multiSelectAnchor is { IsFolder: false } anchor)
        {
            var ordered = vm.GetVisibleLeavesInOrder();
            var from = ordered.IndexOf(anchor);
            var to = ordered.IndexOf(node);
            if (from >= 0 && to >= 0)
            {
                foreach (var leaf in ordered) leaf.IsMultiSelected = false;
                var (lo, hi) = from <= to ? (from, to) : (to, from);
                for (var i = lo; i <= hi; i++) ordered[i].IsMultiSelected = true;
            }
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            node.IsMultiSelected = !node.IsMultiSelected;
            _multiSelectAnchor = node;
        }
        else
        {
            SelectSingle(vm, node);
            return;
        }

        vm.SelectedNode = node; // drives the right-hand edit panels — always the row just clicked, even mid multi-select
    }

    /// <summary>Collapses selection down to exactly one node (or none, for a folder — folders never join the leaf multi-selection).</summary>
    private void SelectSingle(ProjectTreeViewModel vm, TreeNodeViewModel node)
    {
        foreach (var leaf in vm.GetMultiSelectedLeaves()) leaf.IsMultiSelected = false;
        if (!node.IsFolder) node.IsMultiSelected = true;
        _multiSelectAnchor = node.IsFolder ? null : node;
        vm.SelectedNode = node;
    }

    /// <summary>
    /// Right-clicking a leaf that's already part of a multi-selection keeps the whole selection
    /// intact (so the context menu's bulk actions apply to all of it, Explorer-style); right-clicking
    /// anything else collapses selection down to just that node before the menu opens.
    /// </summary>
    private void Tree_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ProjectTreeViewModel vm) return;
        if (FindAncestorDataContext<TreeNodeViewModel>(e.OriginalSource as DependencyObject) is not { } node) return;

        var alreadyBulkTarget = !node.IsFolder && node.IsMultiSelected && vm.GetMultiSelectedLeaves().Count > 1;
        if (!alreadyBulkTarget) SelectSingle(vm, node);
    }

    /// <summary>
    /// OriginalSource can land on a <see cref="System.Windows.Documents.Run"/> (or other Inline) when a click
    /// hits rendered text directly — those are logical/content-tree elements, not Visuals, and
    /// VisualTreeHelper.GetParent throws on them ("'Run' is not a Visual or Visual3D") — so non-Visual nodes
    /// are walked via LogicalTreeHelper instead until the walk reaches a real Visual again.
    /// </summary>
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void Tree_OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(SourceImagesPanelView.DragFormat))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DragFormat) && e.Data.GetData(DragFormat) is TreeNodeViewModel dragged)
        {
            _lastDragPosition = e.GetPosition(Tree);
            var originalSource = e.OriginalSource as DependencyObject;
            var target = FindAncestorDataContext<TreeNodeViewModel>(originalSource);

            var insertAfter = IsInsertAfter(originalSource, e);
            var valid = CanReorder(dragged, target) && !(target!.IsReservedBlank && !insertAfter);
            if (valid)
            {
                if (!ReferenceEquals(_dropIndicatorNode, target))
                {
                    ClearDropIndicator();
                    _dropIndicatorNode = target;
                }
                target!.ShowDropIndicatorAbove = !insertAfter;
                target.ShowDropIndicatorBelow = insertAfter;
            }
            else
            {
                ClearDropIndicator();
            }

            e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Tree_OnDragLeave(object sender, DragEventArgs e) => ClearDropIndicator();

    /// <summary>Cheap pre-check shared by DragOver (live cursor feedback) and Drop (before actually raising the request) — same category, and for an 8bpp category (per-folder palette, unlike 4bpp's single shared bank) the same folder too, since crossing folders there would mean re-quantizing, not just reordering. Does NOT check the reserved-blank "never insert before it" rule — that needs the resolved before/after side, only computed at Drop.</summary>
    private static bool CanReorder(TreeNodeViewModel dragged, TreeNodeViewModel? target) =>
        target is { IsFolder: false } && target != dragged &&
        target.Category == dragged.Category &&
        (dragged.Category!.Value.UsesPaletteBank() || target.FolderPath == dragged.FolderPath);

    private void Tree_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(SourceImagesPanelView.DragFormat) && e.Data.GetData(SourceImagesPanelView.DragFormat) is SourceImageViewModel sourceVm)
        {
            var target = FindAncestorDataContext<TreeNodeViewModel>(e.OriginalSource as DependencyObject);
            if (target is { Category: { } category, FolderPath: { } folderPath })
            {
                AssetDropRequested?.Invoke(sourceVm, category, folderPath);
            }
            return;
        }

        if (e.Data.GetDataPresent(DragFormat) && e.Data.GetData(DragFormat) is TreeNodeViewModel dragged)
        {
            var originalSource = e.OriginalSource as DependencyObject;
            var target = FindAncestorDataContext<TreeNodeViewModel>(originalSource);
            ClearDropIndicator();
            if (!CanReorder(dragged, target)) return;
            if (dragged.AssetId is not { } draggedId || target!.AssetId is not { } targetId) return;

            var insertAfter = IsInsertAfter(originalSource, e);
            if (target.IsReservedBlank && !insertAfter) return; // never allowed to bump it out of the first slot

            AssetReorderRequested?.Invoke(draggedId, targetId, insertAfter);
        }
    }

    private void AddSubfolderMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: TreeNodeViewModel { IsFolder: true } parentFolder } } }) return;
        if (DataContext is not ProjectTreeViewModel vm) return;

        var dialog = new NewSubfolderWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SubfolderName))
        {
            vm.CreateSubfolder(parentFolder, dialog.SubfolderName.Trim());
        }
    }

    /// <summary>
    /// Shows only the menu items that make sense for the right-clicked node and the current
    /// multi-selection. Item order: 0 Rename, 1 Re-quantize, 2 Delete, 3 Add sub-folder,
    /// 4 Re-quantize folder, 5 Delete folder. Rename is hidden for a bulk (>1) selection — renaming
    /// several assets at once doesn't make sense. Re-quantize/Delete swap their header text to show
    /// the selection count, and bulk Re-quantize is disabled outright when the selection spans more
    /// than one category (re-quantizing needs one shared FolderPath/Category to replace assets into).
    /// </summary>
    private void NodeText_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TreeNodeViewModel node, ContextMenu: { } menu }) return;
        if (DataContext is not ProjectTreeViewModel vm) return;

        var isLeaf = !node.IsFolder;
        var multiSelected = vm.GetMultiSelectedLeaves();
        var isBulkTarget = isLeaf && node.IsMultiSelected && multiSelected.Count > 1;

        if (menu.Items[0] is MenuItem rename)
        {
            rename.Visibility = isLeaf && !isBulkTarget ? Visibility.Visible : Visibility.Collapsed;
        }

        if (menu.Items[1] is MenuItem reQuantize)
        {
            reQuantize.Visibility = isLeaf ? Visibility.Visible : Visibility.Collapsed;
            if (isBulkTarget)
            {
                var singleCategory = multiSelected.Select(n => n.Category).Distinct().Count() == 1;
                reQuantize.Header = $"Re-quantize {multiSelected.Count} selected...";
                reQuantize.IsEnabled = singleCategory;
                reQuantize.ToolTip = singleCategory ? null : "Can't bulk re-quantize a selection spanning more than one category/folder — select tiles/sprites from a single folder at a time.";
            }
            else
            {
                reQuantize.Header = "Re-quantize... (Ctrl+R)";
                reQuantize.IsEnabled = true;
                reQuantize.ToolTip = null;
            }
        }

        if (menu.Items[2] is MenuItem delete)
        {
            delete.Visibility = isLeaf ? Visibility.Visible : Visibility.Collapsed;
            delete.Header = isBulkTarget ? $"Delete {multiSelected.Count} selected..." : "Delete";
        }

        if (menu.Items[3] is MenuItem addSubfolder) addSubfolder.Visibility = node.IsFolder && node.CanHaveSubfolders ? Visibility.Visible : Visibility.Collapsed;

        if (menu.Items[4] is MenuItem reQuantizeFolder)
        {
            reQuantizeFolder.Visibility = node.IsFolder ? Visibility.Visible : Visibility.Collapsed;
            if (node.IsFolder)
            {
                var hasAssets = node.Children.Any(c => !c.IsFolder);
                reQuantizeFolder.IsEnabled = hasAssets;
                reQuantizeFolder.ToolTip = hasAssets
                    ? "Re-quantize every tile/sprite in THIS folder only, with a chosen dithering mode"
                    : "This folder has no tiles/sprites of its own to re-quantize yet.";
            }
        }

        if (menu.Items[5] is MenuItem deleteFolder) deleteFolder.Visibility = node.IsFolder && !node.IsCategoryRoot ? Visibility.Visible : Visibility.Collapsed;
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
        if (!TryGetContextNode(sender, out var node)) return;
        if (DataContext is not ProjectTreeViewModel vm) return;

        var multiSelected = vm.GetMultiSelectedLeaves();
        if (node.IsMultiSelected && multiSelected.Count > 1)
        {
            var ids = multiSelected.Where(n => n.AssetId.HasValue).Select(n => n.AssetId!.Value).ToList();
            ReQuantizeSelectedRequested?.Invoke(ids);
        }
        else if (node.AssetId is { } assetId)
        {
            ReQuantizeContextRequested?.Invoke(assetId);
        }
    }

    /// <summary>Delegates to the same target-resolution logic as the Delete key (<see cref="MainViewModel.DeleteSelectedCommand"/>) — whatever's currently multi-selected, or else this single node.</summary>
    private void DeleteMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out _)) return;
        DeleteRequested?.Invoke();
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

    /// <summary>See <see cref="FindAncestor{T}"/>'s doc comment — same non-Visual OriginalSource hazard.</summary>
    private static T? FindAncestorDataContext<T>(DependencyObject? source) where T : class
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: T match }) return match;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
}
