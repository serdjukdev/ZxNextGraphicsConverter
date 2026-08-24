using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class MetatileEditorWindow : Window
{
    /// <summary>Drag format for reordering a metatile within the "Existing metatiles" gallery — carries the dragged MetatileListItemViewModel itself.</summary>
    private const string DragFormat = "ZxNext.MetatileReorder";

    /// <summary>Armed in <see cref="MetatileList_OnPreviewMouseLeftButtonDown"/> when the click landed on a draggable item (not the reserved blank) — same "record start position, only actually start DoDragDrop once the mouse clears the OS drag threshold" pattern as SourceImagesPanelView/ProjectTreeView's own drag sources.</summary>
    private Point _dragStart;
    private bool _dragArmed;
    private MetatileListItemViewModel? _dragCandidate;

    /// <summary>The item currently showing a drop-indicator line — at most one at a time, cleared whenever the hovered target changes, the drag leaves the list, or it completes.</summary>
    private MetatileListItemViewModel? _dropIndicatorItem;

    /// <summary>Runs only while a reorder drag from this list is in progress — same "ticks during the blocking DoDragDrop call" approach as ProjectTreeView's own auto-scroll.</summary>
    private DispatcherTimer? _autoScrollTimer;
    private Point _lastDragPosition;

    public MetatileEditorWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// GridSplitter's own built-in resize doesn't reliably re-check the star-width RightPaneColumn's
    /// MinWidth against the Grid's CURRENT ActualWidth on every drag tick — neither column has a
    /// MaxWidth, so a fast drag can grow LeftPaneColumn past where RightPaneColumn bottoms out at its
    /// own MinWidth, committing a width that only becomes valid once the window is resized larger again.
    /// Handling DragDelta ourselves and marking it Handled replaces GridSplitter's own resize logic
    /// entirely: every drag tick grows/shrinks LeftPaneColumn by how much slack RightPaneColumn
    /// currently has above its own MinWidth (computed fresh from ActualWidth each tick), so it can never
    /// push either column out of range even transiently.
    /// </summary>
    private void MainSplitter_OnDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var newWidth = LeftPaneColumn.ActualWidth + e.HorizontalChange;
        var maxWidth = LeftPaneColumn.ActualWidth + (RightPaneColumn.ActualWidth - RightPaneColumn.MinWidth);
        LeftPaneColumn.Width = new GridLength(Math.Clamp(newWidth, LeftPaneColumn.MinWidth, Math.Max(LeftPaneColumn.MinWidth, maxWidth)));
        e.Handled = true;
    }

    private void MetatileList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var originalSource = e.OriginalSource as DependencyObject;
        if (FindAncestorDataContext<MetatileListItemViewModel>(originalSource) is not { } item) return;

        _dragStart = e.GetPosition(null);
        _dragCandidate = item;
        _dragArmed = !item.IsReservedBlank; // the reserved blank must always stay first — never a valid drag source
    }

    /// <summary>Fires continuously while a mouse button is held over the list — only actually starts a drag once armed and past the OS's drag distance threshold, same pattern as ProjectTreeView's own drag source.</summary>
    private void MetatileList_OnPreviewMouseMove(object sender, MouseEventArgs e)
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
            DragDrop.DoDragDrop(MetatileList, new DataObject(DragFormat, dragged), DragDropEffects.Move);
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

    /// <summary>Scrolls the gallery a little whenever the last-known drag position (updated in <see cref="MetatileList_OnDragOver"/>) sits within a small band of the list's own top/bottom edge — the gallery still scrolls vertically (rows wrap horizontally, but the whole thing scrolls up/down), so this is the same axis ProjectTreeView's auto-scroll uses.</summary>
    private void AutoScrollTimer_OnTick(object? sender, EventArgs e)
    {
        if (FindVisualChild<ScrollViewer>(MetatileList) is not { } scrollViewer) return;
        const double edge = 28;
        const double step = 14;
        if (_lastDragPosition.Y < edge) scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - step));
        else if (_lastDragPosition.Y > MetatileList.ActualHeight - edge) scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + step);
    }

    private void ClearDropIndicator()
    {
        if (_dropIndicatorItem is null) return;
        _dropIndicatorItem.ShowDropIndicatorLeft = false;
        _dropIndicatorItem.ShowDropIndicatorRight = false;
        _dropIndicatorItem = null;
    }

    /// <summary>Which half of the hovered item's container the pointer is over — the gallery flows left-to-right (WrapPanel), so this is horizontal, unlike the main tree's vertical top/bottom split. Shared by DragOver (live indicator/cursor feedback) and Drop (the actual decision), so they can never disagree.</summary>
    private static bool IsInsertAfter(DependencyObject? originalSource, DragEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(originalSource) is { ActualWidth: > 0 } container)
        {
            return e.GetPosition(container).X > container.ActualWidth / 2;
        }
        return true;
    }

    private static bool CanReorder(MetatileListItemViewModel dragged, MetatileListItemViewModel? target) =>
        target is not null && target != dragged && target.Metatile.GridSize == dragged.Metatile.GridSize;

    private void MetatileList_OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat) || e.Data.GetData(DragFormat) is not MetatileListItemViewModel dragged)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _lastDragPosition = e.GetPosition(MetatileList);
        var originalSource = e.OriginalSource as DependencyObject;
        var target = FindAncestorDataContext<MetatileListItemViewModel>(originalSource);

        var insertAfter = IsInsertAfter(originalSource, e);
        var valid = CanReorder(dragged, target) && !(target!.IsReservedBlank && !insertAfter);
        if (valid)
        {
            if (!ReferenceEquals(_dropIndicatorItem, target))
            {
                ClearDropIndicator();
                _dropIndicatorItem = target;
            }
            target!.ShowDropIndicatorLeft = !insertAfter;
            target.ShowDropIndicatorRight = insertAfter;
        }
        else
        {
            ClearDropIndicator();
        }

        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void MetatileList_OnDragLeave(object sender, DragEventArgs e) => ClearDropIndicator();

    private void MetatileList_OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat) || e.Data.GetData(DragFormat) is not MetatileListItemViewModel dragged) return;
        if (DataContext is not MetatileEditorViewModel vm) return;

        var originalSource = e.OriginalSource as DependencyObject;
        var target = FindAncestorDataContext<MetatileListItemViewModel>(originalSource);
        ClearDropIndicator();
        if (!CanReorder(dragged, target)) return;

        var insertAfter = IsInsertAfter(originalSource, e);
        if (target!.IsReservedBlank && !insertAfter) return; // never allowed to bump it out of the first slot

        vm.ReorderMetatile(dragged.Metatile.Id, target.Metatile.Id, insertAfter);
    }

    /// <summary>See ProjectTreeView's own copy of this same helper for why non-Visual OriginalSource nodes (e.g. a Run inside rendered text) need the LogicalTreeHelper fallback.</summary>
    private static T? FindAncestorDataContext<T>(DependencyObject? source) where T : class
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: T match }) return match;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
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
}
