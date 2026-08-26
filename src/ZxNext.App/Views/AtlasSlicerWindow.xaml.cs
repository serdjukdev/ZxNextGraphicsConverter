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
    /// Lazily resolves the project's <see cref="ZxNext.Core.Project.ProjectState.MetatileGridSize"/> the
    /// first time it's actually needed — same pattern/dialog as MainWindow.MetatileEditor_OnClick. If the
    /// project already has it locked, <see cref="AtlasSlicerViewModel.ResolvedGridSize"/> was already set
    /// in the constructor and this is a no-op check (EnsureChosen returns true immediately). If the user
    /// cancels the size-picker, the checkbox reverts so the dialog is left in a consistent 8x8 state.
    /// </summary>
    private void MetatileBlocksCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (Vm.ResolvedGridSize is not null) return;

        if (MetatileGridSizeWindow.EnsureChosen(Vm.Project, this) && Vm.Project.MetatileGridSize is { } gridSize)
        {
            Vm.SetResolvedGridSize(gridSize);
        }
        else
        {
            MetatileBlocksCheckBox.IsChecked = false;
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
