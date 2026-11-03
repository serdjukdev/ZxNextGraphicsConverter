using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

/// <summary>
/// "Replace image...": pick a source image from the project and click the part of it that becomes this
/// tile/sprite/Layer2 asset's new picture. Aiming at one 8x8 cell of a big sheet is the whole job here, so
/// the three ways of aiming are kept strictly separate and never fight each other: a LEFT CLICK snaps to a
/// whole cell, the ARROW KEYS move one pixel at a time, and the two offset boxes take a typed number
/// (Enter commits it and hands focus back to the picture instead of pressing Replace). The view is
/// independent of all three: a resizable window that starts by fitting the whole image, the mouse wheel
/// to zoom, and a middle-button drag to pan.
/// </summary>
public partial class ReplaceImageWindow : Window
{
    /// <summary>Whole-number zoom steps only (plus the fractions a big sheet needs), so pixel art never lands on a blurry 1.37x.</summary>
    private static readonly double[] ZoomSteps = [0.125, 0.25, 0.5, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32];

    private ReplaceImageViewModel Vm => (ReplaceImageViewModel)DataContext;

    /// <summary>Null means "fit the whole image into the preview area", which is how the dialog opens and what it goes back to on a new source image; it then follows the window size until the wheel sets an explicit zoom factor.</summary>
    private double? _zoomChoice;

    /// <summary>The zoom currently applied, needed both for the next zoom step and to keep the selection box's outline one screen pixel wide whatever the zoom is (the box is drawn in unscaled source pixels and then scaled with the image).</summary>
    private double _scale = 1;

    private bool _panning;
    private Point _panStart;
    private double _panStartHorizontal;
    private double _panStartVertical;

    public ReplaceImageWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Vm.PropertyChanged += Vm_OnPropertyChanged;
            Setup();
        };
        // The picture holds the keyboard focus from the start, so the arrow keys move the selection
        // straight away instead of needing a click on the image first.
        Loaded += (_, _) => PreviewScroll.Focus();
    }

    private void Vm_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReplaceImageViewModel.SelectedSource))
        {
            Setup();
            return;
        }
        DrawSelection();
    }

    /// <summary>Re-sized from scratch on every source change: a different source image is a different size, so the host and the fit zoom have to be recomputed, not just redrawn.</summary>
    private void Setup()
    {
        if (DataContext is not ReplaceImageViewModel vm || vm.SelectedSource is null)
        {
            PreviewHost.Width = 0;
            PreviewHost.Height = 0;
            RectOverlay.Children.Clear();
            return;
        }

        PreviewHost.Width = vm.SourceWidth;
        PreviewHost.Height = vm.SourceHeight;
        _zoomChoice = null; // a different image is a different size: start by showing all of it again
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (DataContext is not ReplaceImageViewModel vm || vm.SelectedSource is null) return;

        double scale;
        if (_zoomChoice is { } chosen)
        {
            scale = chosen;
        }
        else
        {
            // Fit: the biggest zoom at which the whole image still fits the preview area. Deliberately
            // allowed above 1x, so a 16x16 sprite sheet fills the window instead of sitting in a corner,
            // and below 1x, so a 1024px sheet is visible whole rather than only through the scroll bars.
            var availableWidth = Math.Max(1, PreviewScroll.ActualWidth - 6);
            var availableHeight = Math.Max(1, PreviewScroll.ActualHeight - 6);
            scale = Math.Min(availableWidth / vm.SourceWidth, availableHeight / vm.SourceHeight);
            scale = Math.Clamp(scale, 0.05, 32);
        }

        _scale = scale;
        PreviewHost.LayoutTransform = new ScaleTransform(scale, scale);
        ZoomLabel.Text = $"{scale * 100:0}%";
        DrawSelection();
    }

    /// <summary>
    /// One step along <see cref="ZoomSteps"/> from whatever zoom is showing right now — the initial
    /// fit-to-window scale included, so the first step out of a 43% fit goes to 25%, not to an end of the
    /// ladder. At either end nothing happens at all: running out of steps must never wrap round to the
    /// opposite extreme.
    /// </summary>
    private void StepZoom(int direction)
    {
        double next;
        if (direction > 0)
        {
            if (_scale >= ZoomSteps[^1] * 0.999) return;
            next = ZoomSteps.First(z => z > _scale * 1.001);
        }
        else
        {
            if (_scale <= ZoomSteps[0] * 1.001) return;
            next = ZoomSteps.Last(z => z < _scale * 0.999);
        }

        _zoomChoice = next;
        ApplyZoom();
    }

    /// <summary>
    /// The selection box, drawn to be visible over any art: a translucent yellow wash, a dark outer
    /// outline and a bright yellow inner one, each kept at roughly one screen pixel by dividing by the
    /// current zoom. It shows what will actually be READ, which for a Layer2 source smaller than the
    /// canvas is less than the asset's size (the rest stays transparent) — drawing the full canvas size
    /// there would promise pixels the source doesn't have.
    /// </summary>
    private void DrawSelection()
    {
        RectOverlay.Children.Clear();
        if (DataContext is not ReplaceImageViewModel vm || vm.SelectedSource is null || vm.IsSourceTooSmall) return;

        var width = Math.Max(1, vm.CopyWidth);
        var height = Math.Max(1, vm.CopyHeight);

        var wash = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(Color.FromArgb(60, 255, 240, 0))
        };
        var outer = new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = Brushes.Black,
            StrokeThickness = 3 / _scale,
            Fill = Brushes.Transparent
        };
        var inner = new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = new SolidColorBrush(Color.FromRgb(255, 240, 0)),
            StrokeThickness = 1.5 / _scale,
            Fill = Brushes.Transparent
        };

        foreach (var shape in new[] { wash, outer, inner })
        {
            Canvas.SetLeft(shape, vm.OffsetLeft);
            Canvas.SetTop(shape, vm.OffsetTop);
            RectOverlay.Children.Add(shape);
        }
    }

    /// <summary>The click position is in the host's own unscaled pixel space (the zoom is a LayoutTransform on the host), so it is already a source pixel coordinate — the view model rounds it down to a whole cell and clamps it. Also takes the keyboard focus back from the offset boxes, so the arrow keys keep working right after a click.</summary>
    private void PreviewHost_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm.SelectedSource is null) return;

        var pos = e.GetPosition(PreviewHost);
        Vm.PickAt((int)Math.Floor(pos.X), (int)Math.Floor(pos.Y));
        PreviewScroll.Focus();
    }

    /// <summary>
    /// Arrow keys move the selection one pixel at a time — clicking is the cell-aligned way to aim, so the
    /// keyboard is the pixel-exact one, for a sheet whose cells don't start on a multiple of the tile size
    /// or one with a stray pixel of rubbish along an edge. This is the TUNNELLING PreviewKeyDown on
    /// purpose: the preview's ScrollViewer (and the source list) handle arrow keys themselves and mark
    /// them handled, so a plain KeyDown on the window never ran and the arrows just scrolled the picture.
    /// They work whatever has the keyboard focus, an offset box included (where they would otherwise only
    /// move a caret, and the selection would sit still until the picture was clicked again) — the price is
    /// that a typed number is edited with Home/End and Backspace rather than with the arrows.
    /// </summary>
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ReplaceImageViewModel vm || vm.SelectedSource is null) return;

        switch (e.Key)
        {
            case Key.Left: vm.Nudge(-1, 0); break;
            case Key.Right: vm.Nudge(1, 0); break;
            case Key.Up: vm.Nudge(0, -1); break;
            case Key.Down: vm.Nudge(0, 1); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Enter in an offset box commits the number and puts the focus back on the picture. Without this it would reach the window's default button and close the dialog with Replace, which is the one thing it must not do while a value is still being typed.</summary>
    private void OffsetBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;

        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        PreviewScroll.Focus();
        e.Handled = true;
    }

    private void PreviewScroll_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoomChoice is null) ApplyZoom(); // "Fit" follows the window: resizing it is the zoom control
    }

    /// <summary>
    /// The wheel is the zoom, with no modifier: panning is the middle-button drag, so the wheel has
    /// nothing else to do here. It zooms AROUND THE CURSOR: the source pixel under the mouse pointer is
    /// read before the zoom and the view is scrolled afterwards so that same pixel sits back under the
    /// pointer, which is what makes zooming into a particular cell of a big sheet possible at all. While
    /// the whole image still fits the viewport there is nothing to scroll and it stays centred instead.
    /// </summary>
    private void PreviewScroll_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // GetPosition on the scaled host returns unscaled source pixels, so this survives the zoom change.
        var anchorInImage = e.GetPosition(PreviewHost);
        var anchorInViewport = e.GetPosition(PreviewScroll);

        StepZoom(e.Delta > 0 ? 1 : -1);

        // The new extent only exists after a layout pass; without this the scroll offsets would be
        // clamped against the OLD (smaller) extent and the anchor would drift on every zoom in.
        PreviewScroll.UpdateLayout();
        PreviewScroll.ScrollToHorizontalOffset(anchorInImage.X * _scale - anchorInViewport.X);
        PreviewScroll.ScrollToVerticalOffset(anchorInImage.Y * _scale - anchorInViewport.Y);

        e.Handled = true;
    }

    /// <summary>Middle-button drag pans the view. Scrolling by the raw mouse delta is already zoom-correct (both are screen pixels) and the ScrollViewer clamps at the edges by itself, so a drag can never push the picture out of sight.</summary>
    private void PreviewScroll_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        _panning = true;
        _panStart = e.GetPosition(PreviewScroll);
        _panStartHorizontal = PreviewScroll.HorizontalOffset;
        _panStartVertical = PreviewScroll.VerticalOffset;
        PreviewScroll.CaptureMouse();
        Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void PreviewScroll_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;

        var pos = e.GetPosition(PreviewScroll);
        PreviewScroll.ScrollToHorizontalOffset(_panStartHorizontal - (pos.X - _panStart.X));
        PreviewScroll.ScrollToVerticalOffset(_panStartVertical - (pos.Y - _panStart.Y));
    }

    private void PreviewScroll_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_panning) return;

        _panning = false;
        PreviewScroll.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
