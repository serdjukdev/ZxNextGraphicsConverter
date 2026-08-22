using System.Windows;
using System.Windows.Input;
using ZxNext.App.ViewModels;
using ZxNext.Core.Model;

namespace ZxNext.App.Views;

public partial class MapEditorWindow : Window
{
    private bool _isDrawing;

    public MapEditorWindow()
    {
        InitializeComponent();
    }

    private void NewMap_OnClick(object sender, RoutedEventArgs e)
    {
        var newMapVm = new NewMapViewModel();
        var dialog = new NewMapWindow { DataContext = newMapVm, Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (DataContext is MapEditorViewModel vm)
        {
            vm.CreateMap(newMapVm.Name, newMapVm.Width, newMapVm.Height, newMapVm.MetatileGridSize);
        }
    }

    private void MapCanvasHost_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MapEditorViewModel vm || vm.SelectedMap is null) return;

        _isDrawing = true;
        MapCanvasHost.CaptureMouse();
        vm.BeginStroke();

        var position = e.GetPosition(MapImage);
        vm.PaintOrEraseAt((int)position.X, (int)position.Y);
    }

    private void MapCanvasHost_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDrawing || e.LeftButton != MouseButtonState.Pressed) return;
        if (DataContext is not MapEditorViewModel vm) return;

        // Sprite placement is click-only by design (no drag-stamping) — every other tool/layer
        // combination supports continuous drag painting/erasing.
        if (vm.ActiveLayer == MapLayerKind.Sprites && vm.ActiveTool == MapEditTool.Paint) return;

        var position = e.GetPosition(MapImage);
        vm.PaintOrEraseAt((int)position.X, (int)position.Y);
    }

    private void MapCanvasHost_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrawing();

    private void MapCanvasHost_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDrawing) EndDrawing();
    }

    private void EndDrawing()
    {
        _isDrawing = false;
        MapCanvasHost.ReleaseMouseCapture();
        if (DataContext is MapEditorViewModel vm) vm.EndStroke();
    }
}
