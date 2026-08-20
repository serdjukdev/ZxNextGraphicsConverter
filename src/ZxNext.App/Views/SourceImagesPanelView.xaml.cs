using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
