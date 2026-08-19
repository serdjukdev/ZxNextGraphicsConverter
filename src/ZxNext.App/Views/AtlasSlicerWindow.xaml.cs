using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class AtlasSlicerWindow : Window
{
    private AtlasSlicerViewModel Vm => (AtlasSlicerViewModel)DataContext;

    public AtlasSlicerWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Setup();
    }

    private void Setup()
    {
        PreviewHost.Width = Vm.SourceWidth;
        PreviewHost.Height = Vm.SourceHeight;

        var scale = Math.Max(1, Math.Min(8, 400 / Math.Max(Vm.SourceWidth, Vm.SourceHeight)));
        PreviewHost.LayoutTransform = new ScaleTransform(scale, scale);

        Vm.PropertyChanged += Vm_OnPropertyChanged;
        DrawGrid();
    }

    private void Vm_OnPropertyChanged(object? sender, PropertyChangedEventArgs e) => DrawGrid();

    private void DrawGrid()
    {
        GridOverlay.Children.Clear();
        foreach (var rect in Vm.ComputeCellRects())
        {
            var r = new Rectangle
            {
                Width = rect.Width,
                Height = rect.Height,
                Stroke = Brushes.Red,
                StrokeThickness = 1 / 4.0, // hairline once scaled up by the LayoutTransform
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(r, rect.X);
            Canvas.SetTop(r, rect.Y);
            GridOverlay.Children.Add(r);
        }
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
