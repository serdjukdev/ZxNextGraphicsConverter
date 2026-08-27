using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>Green = included, red (+ a translucent wash so the dimming is visible even at this preview's small scale) = excluded — see <see cref="AtlasSlicerViewModel.IncludedUnits"/>, always meaningful now (every category defaults every unit to included, so an all-green grid is the common case, visually close to the plain outline this used to always draw).</summary>
    private void DrawGrid()
    {
        GridOverlay.Children.Clear();
        var rects = Vm.ComputeCellRects();
        var included = Vm.IncludedUnits;

        for (var i = 0; i < rects.Count; i++)
        {
            var rect = rects[i];
            var isIncluded = i >= included.Count || included[i]; // defensive: mid-recompute, default to "included" look rather than a false "excluded" flash
            var r = new Rectangle
            {
                Width = rect.Width,
                Height = rect.Height,
                Stroke = isIncluded ? Brushes.LimeGreen : Brushes.Red,
                StrokeThickness = 1 / 4.0, // hairline once scaled up by the LayoutTransform
                Fill = isIncluded ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(90, 200, 0, 0))
            };
            Canvas.SetLeft(r, rect.X);
            Canvas.SetTop(r, rect.Y);
            GridOverlay.Children.Add(r);
        }
    }

    /// <summary>Always active, for every category — maps the click position to a cell/block index (linear scan; unit counts are small enough that this is fine for a single click) and toggles it.</summary>
    private void PreviewHost_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(PreviewHost);
        var rects = Vm.ComputeCellRects();
        for (var i = 0; i < rects.Count; i++)
        {
            var rect = rects[i];
            if (pos.X >= rect.X && pos.X < rect.X + rect.Width && pos.Y >= rect.Y && pos.Y < rect.Y + rect.Height)
            {
                Vm.ToggleUnit(i);
                return;
            }
        }
    }

    private void SelectAllThatFit_OnClick(object sender, RoutedEventArgs e) => Vm.SelectAllThatFit();

    private void ClearSelection_OnClick(object sender, RoutedEventArgs e) => Vm.ClearSelection();

    /// <summary>
    /// <see cref="AtlasSlicerViewModel.ResolvedGridSize"/> is set from <see cref="ZxNext.Core.Project.ProjectState.MetatileGridSize"/>
    /// in the constructor now that it's locked up front (New Project dialog, or MainWindow's
    /// once-per-legacy-project prompt right after load) — so this is normally a no-op. It can still be
    /// null only for a legacy project whose load-time prompt got cancelled, in which case metatile-block
    /// slicing stays unavailable (the checkbox reverts) until the project is reopened.
    /// </summary>
    private void MetatileBlocksCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (Vm.ResolvedGridSize is not null) return;

        MetatileBlocksCheckBox.IsChecked = false;
        MessageBox.Show("This project has no metatile size set. Close and reopen the project to be asked again.",
            "Slice into metatile blocks", MessageBoxButton.OK, MessageBoxImage.Warning);
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
