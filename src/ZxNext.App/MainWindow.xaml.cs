using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using ZxNext.App.Rendering;
using ZxNext.App.ViewModels;
using ZxNext.App.Views;
using ZxNext.Core.Conversion;
using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;
using ZxNext.Core.Settings;

namespace ZxNext.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        ProjectTreeViewControl.AssetDropRequested += OnAssetDropRequested;
        ProjectTreeViewControl.RenameAssetRequested += OnRenameAssetRequested;
        ProjectTreeViewControl.ReQuantizeContextRequested += _viewModel.RequestReQuantizeById;
        ProjectTreeViewControl.DeleteFolderRequested += _viewModel.DeleteFolder;
        ProjectTreeViewControl.ReQuantizeFolderRequested += OnReQuantizeFolderRequested;
        PixelEditorViewControl.PaintStrokeStarted += _viewModel.BeginPaintStroke;
        PixelEditorViewControl.PixelPainted += _viewModel.PaintPixel;
        PixelEditorViewControl.PaintStrokeEnded += _viewModel.EndPaintStroke;
        PixelEditorViewControl.PixelPicked += _viewModel.PickColorAt;
        ImageViewerViewControl.PixelPicked += _viewModel.PickColorAt;
        _viewModel.PaletteStrip.SwatchDoubleClicked += OnPaletteSwatchDoubleClicked;
        _viewModel.ReQuantizeRequested += OnReQuantizeRequested;

        RestoreWindowGeometry();
    }

    /// <summary>Applies the saved window size/position, falling back to the XAML defaults if nothing was saved yet or the saved bounds no longer fit any screen.</summary>
    private void RestoreWindowGeometry()
    {
        var settings = AppSettingsStore.Load();
        if (settings.WindowWidth is { } w && settings.WindowHeight is { } h)
        {
            Width = w;
            Height = h;
        }

        if (settings.WindowLeft is { } left && settings.WindowTop is { } top)
        {
            var visible = SystemParameters.VirtualScreenWidth > 0 &&
                          left + Width > SystemParameters.VirtualScreenLeft &&
                          left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                          top + 50 > SystemParameters.VirtualScreenTop &&
                          top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (visible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnPaletteSwatchDoubleClicked(int index)
    {
        var palette = _viewModel.PaletteStrip.CurrentPalette;
        if (palette is null) return;
        if (index == palette.TransparentIndex)
        {
            _viewModel.PixelEditor.StatusText = "That slot is the transparent index — it has no colour to edit.";
            return;
        }

        var pickerVm = new NextColorPickerViewModel(palette.Slots[index]);
        var dialog = new NextColorPickerWindow { DataContext = pickerVm, Owner = this };
        if (dialog.ShowDialog() == true && pickerVm.SelectedColor is { } chosen)
        {
            _viewModel.SetPaletteColor(index, chosen);
        }
    }

    private async void OnAssetDropRequested(SourceImageViewModel source, AssetCategory category, string folderPath)
    {
        var decoded = _viewModel.DecodeSource(source);
        var (cellWidth, cellHeight) = category.CellSize();

        if (decoded.Width == cellWidth && decoded.Height == cellHeight)
        {
            var outcome = _viewModel.ImportIntoCategory(source, category, folderPath);
            if (!outcome.Success && IsPaletteFullReason(outcome.Reason))
            {
                await HandlePaletteOverflowAsync(outcome.Error ?? "Palette overflow.", category,
                    maxColors => _viewModel.ImportIntoCategory(source, category, folderPath, maxColors));
            }
            return;
        }

        if (category.IsLayer2())
        {
            var layer2Preview = RawBitmapRenderer.FromRgba32(decoded.Width, decoded.Height, decoded.Rgba32);
            var placementVm = new Layer2PlacementViewModel(layer2Preview, decoded.Width, decoded.Height, cellWidth, cellHeight);
            var placementDialog = new Layer2PlacementWindow { DataContext = placementVm, Owner = this };
            if (placementDialog.ShowDialog() == true)
            {
                var placedRgba = placementVm.BuildPlacedRgba(decoded.Rgba32);
                var outcome = _viewModel.ImportLayer2Placement(source, category, folderPath, placedRgba, placementVm.ResultWidth, placementVm.ResultHeight,
                    placementVm.OffsetLeft, placementVm.OffsetTop, placementVm.CopyWidth, placementVm.CopyHeight);
                if (!outcome.Success && IsPaletteFullReason(outcome.Reason))
                {
                    // Layer2_640x256x4 only has 15 usable colours — real art almost always needs
                    // reducing to fit, unlike the 256-colour 8bpp modes where this is rare.
                    await HandlePaletteOverflowAsync(outcome.Error ?? "Palette full.", category,
                        maxColors => _viewModel.ImportLayer2Placement(source, category, folderPath, placedRgba, placementVm.ResultWidth, placementVm.ResultHeight,
                            placementVm.OffsetLeft, placementVm.OffsetTop, placementVm.CopyWidth, placementVm.CopyHeight, maxColors));
                }
            }
            return;
        }

        var preview = RawBitmapRenderer.FromRgba32(decoded.Width, decoded.Height, decoded.Rgba32);
        var slicerVm = new AtlasSlicerViewModel(preview, decoded.Width, decoded.Height, cellWidth, cellHeight);
        var dialog = new AtlasSlicerWindow { DataContext = slicerVm, Owner = this };

        if (dialog.ShowDialog() == true)
        {
            await _viewModel.ImportSlicedAsync(source, category, folderPath, slicerVm.BuildParameters(), slicerVm.SkipDuplicateCells);
        }
    }

    private void OnRenameAssetRequested(Guid assetId)
    {
        var node = _viewModel.Tree.FindLeafNode(assetId);
        if (node is null) return;

        var dialog = new RenameAssetWindow(node.Name, candidate => _viewModel.IsAssetNameTaken(assetId, candidate)) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.RenameAsset(assetId, dialog.NewName);
        }
    }

    private async void OnReQuantizeRequested(Guid assetId, DitherMode currentMode)
    {
        var vm = new ReQuantizeViewModel(currentMode);
        var dialog = new ReQuantizeWindow { DataContext = vm, Owner = this };
        if (dialog.ShowDialog() != true) return;

        var outcome = _viewModel.ReQuantizeAsset(assetId, vm.SelectedDitherMode, null);
        if (!outcome.Success && IsPaletteFullReason(outcome.Reason))
        {
            await HandlePaletteOverflowAsync(outcome.Error ?? "Palette overflow.", outcome.Category,
                maxColors => _viewModel.ReQuantizeAsset(assetId, vm.SelectedDitherMode, maxColors));
        }
    }

    /// <summary>
    /// Bulk-re-quantizes every tile/sprite in ONE folder with a freshly chosen dithering mode —
    /// scoped to that folder alone, unlike the old per-source-image dithering combo (removed) which
    /// used to cascade to every folder the same image happened to be dropped into.
    /// </summary>
    private async void OnReQuantizeFolderRequested(TreeNodeViewModel node)
    {
        if (node.Category is not { } category || node.FolderPath is not { } folderPath) return;

        var vm = new ReQuantizeViewModel(DitherMode.None,
            $"Re-converts every tile/sprite currently in \"{node.Name}\" from its original source region — other folders (even ones sharing the same source image) are left untouched.");
        var dialog = new ReQuantizeWindow { DataContext = vm, Owner = this, Title = $"Re-quantize folder: {node.Name}" };
        if (dialog.ShowDialog() != true) return;

        await _viewModel.ReQuantizeFolderAsync(category, folderPath, vm.SelectedDitherMode);
    }

    /// <summary>True for either "the 4bpp bank slot is full" or "the flat folder palette is full" — both get the same reduce-and-retry remediation dialog.</summary>
    private static bool IsPaletteFullReason(ImportFailureReason reason) =>
        reason is ImportFailureReason.PaletteOverflow or ImportFailureReason.FlatPaletteFull;

    /// <summary>Shown whenever an import/re-quantize can't fit into its palette — lets the user reduce just this tile's colours, or (4bpp bank categories only) rebuild the whole category's palette bank from scratch at a lower colour cap.</summary>
    private async Task HandlePaletteOverflowAsync(string message, AssetCategory category, Func<int, ImportOutcome> retryWithMaxColors)
    {
        var maxUsableColors = category.UsesPaletteBank() ? PaletteBank.SlotUsableColors : category.FlatPaletteCapacity() - 1;
        var vm = new PaletteOverflowViewModel(message, maxUsableColors, showReQuantizeCategoryOption: category.UsesPaletteBank());
        var dialog = new PaletteOverflowWindow { DataContext = vm, Owner = this };
        if (dialog.ShowDialog() != true) return;

        switch (dialog.Choice)
        {
            case OverflowChoice.ReduceThisTile:
                var outcome = retryWithMaxColors(vm.MaxColors);
                if (!outcome.Success)
                {
                    MessageBox.Show($"Still couldn't place it: {outcome.Error}", "Palette Overflow", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                break;
            case OverflowChoice.ReQuantizeCategory:
                await _viewModel.ReQuantizeCategoryAsync(category, vm.MaxColors);
                break;
        }
    }

    private void NewProject_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.ConfirmDiscardUnsavedChanges()) return;

        var vm = new NewProjectViewModel();
        var dialog = new NewProjectWindow { DataContext = vm, Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.CreateNewProject(vm.FullPath);
            SaveLastProjectDirectory(vm.ParentFolder);
        }
    }

    private async void OpenProject_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.ConfirmDiscardUnsavedChanges()) return;

        var settings = AppSettingsStore.Load();
        var dialog = new OpenFileDialog
        {
            Title = "Open project",
            Filter = $"ZX Next Project (*{ProjectService.FileExtension})|*{ProjectService.FileExtension}",
            InitialDirectory = settings.LastProjectDirectory ?? ""
        };
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.LoadProjectFromAsync(dialog.FileName);
            SaveLastProjectDirectory(System.IO.Path.GetDirectoryName(dialog.FileName));
        }
    }

    private async void SaveProjectAs_OnClick(object sender, RoutedEventArgs e)
    {
        var vm = new NewProjectViewModel(createSubfolder: false);
        var dialog = new NewProjectWindow { DataContext = vm, Owner = this, Title = "Save Project As" };
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.SaveToAsync(vm.FullPath);
            SaveLastProjectDirectory(vm.ParentFolder);
        }
    }

    private static void SaveLastProjectDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        var settings = AppSettingsStore.Load();
        settings.LastProjectDirectory = directory;
        AppSettingsStore.Save(settings);
    }

    private async void Export_OnClick(object sender, RoutedEventArgs e)
    {
        var results = _viewModel.PreviewExport();
        if (results.Count == 0)
        {
            MessageBox.Show(
                "Nothing to export yet — import and convert some tiles/sprites first.",
                "Export for Next", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var vm = new ExportViewModel(results);
        var dialog = new ExportWindow { DataContext = vm, Owner = this };
        if (dialog.ShowDialog() != true) return;

        var existing = ExportService.ListOutputFileNames(results)
            .Where(name => File.Exists(Path.Combine(vm.OutputDirectory, name)))
            .ToList();
        if (existing.Count > 0)
        {
            var preview = string.Join("\n", existing.Take(10));
            if (existing.Count > 10) preview += $"\n...and {existing.Count - 10} more.";
            var overwrite = MessageBox.Show(
                $"{existing.Count} file(s) already exist in this folder and will be overwritten:\n\n{preview}\n\nContinue?",
                "Overwrite existing files?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (overwrite != MessageBoxResult.Yes) return;
        }

        try
        {
            await _viewModel.WriteExportAsync(results, vm.OutputDirectory);
            var settings = AppSettingsStore.Load();
            settings.LastExportDirectory = vm.OutputDirectory;
            AppSettingsStore.Save(settings);
            MessageBox.Show(
                $"Exported {results.Count} folder(s) to:\n{vm.OutputDirectory}",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Export for Next", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MainWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_viewModel.ConfirmDiscardUnsavedChanges())
        {
            e.Cancel = true;
            return;
        }

        SaveWindowGeometry();
    }

    /// <summary>Saves size/position for next launch. When maximized, <see cref="Window.RestoreBounds"/> holds the pre-maximize rectangle — saving the live Width/Height/Left/Top would instead save the full-screen size.</summary>
    private void SaveWindowGeometry()
    {
        var settings = AppSettingsStore.Load();
        var maximized = WindowState == WindowState.Maximized;
        var bounds = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);

        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowMaximized = maximized;

        AppSettingsStore.Save(settings);
    }
}
