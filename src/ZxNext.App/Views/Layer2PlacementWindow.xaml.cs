using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class Layer2PlacementWindow : Window
{
    private Layer2PlacementViewModel Vm => (Layer2PlacementViewModel)DataContext;

    public Layer2PlacementWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Setup();
    }

    private void Setup()
    {
        PreviewHost.Width = Vm.SourceWidth;
        PreviewHost.Height = Vm.SourceHeight;

        var scale = Math.Max(1, Math.Min(8, 400 / (double)Math.Max(Vm.SourceWidth, Vm.SourceHeight)));
        PreviewHost.LayoutTransform = new ScaleTransform(scale, scale);

        Vm.PropertyChanged += Vm_OnPropertyChanged;
        DrawRect();
    }

    private void Vm_OnPropertyChanged(object? sender, PropertyChangedEventArgs e) => DrawRect();

    private void DrawRect()
    {
        RectOverlay.Children.Clear();

        var width = Math.Min(Vm.SourceWidth, Vm.TargetWidth);
        var height = Math.Min(Vm.SourceHeight, Vm.TargetHeight);

        var r = new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = Brushes.Red,
            StrokeThickness = 1 / 4.0,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(r, Vm.OffsetLeft);
        Canvas.SetTop(r, Vm.OffsetTop);
        RectOverlay.Children.Add(r);
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
