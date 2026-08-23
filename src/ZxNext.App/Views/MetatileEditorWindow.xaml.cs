using System.Windows;

namespace ZxNext.App.Views;

public partial class MetatileEditorWindow : Window
{
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
}
