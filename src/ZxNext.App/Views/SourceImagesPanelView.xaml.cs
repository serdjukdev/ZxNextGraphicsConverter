using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class SourceImagesPanelView : UserControl
{
    public const string DragFormat = "ZxNext.SourceImage";

    private Point _dragStart;
    private bool _dragArmed;

    public SourceImagesPanelView()
    {
        InitializeComponent();
    }

    private void Item_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragArmed = FindAncestorDataContext<SourceImageViewModel>(e.OriginalSource as DependencyObject) is not null;
    }

    private void Item_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (FindAncestorDataContext<SourceImageViewModel>(e.OriginalSource as DependencyObject) is not { } vm) return;

        _dragArmed = false;
        var data = new DataObject(DragFormat, vm);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private void Panel_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && DataContext is SourceImagesPanelViewModel vm)
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            foreach (var path in paths)
            {
                vm.AddFile(path);
            }
        }
    }

    private void DeleteSourceImage_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SourceImageViewModel svm }) return;
        if (DataContext is SourceImagesPanelViewModel vm) vm.RequestDeleteImage(svm.Model.Id);
    }

    /// <summary>
    /// Walks up from a mouse event's OriginalSource looking for the item's DataContext. OriginalSource can land
    /// on a <see cref="System.Windows.Documents.Run"/> (or other Inline) when the click hits rendered text
    /// directly — those are logical/content-tree elements, not Visuals, and VisualTreeHelper.GetParent throws
    /// on them ("'Run' is not a Visual or Visual3D") — so non-Visual nodes are walked via LogicalTreeHelper
    /// instead until the walk reaches a real Visual again (e.g. the TextBlock hosting the Run).
    /// </summary>
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
