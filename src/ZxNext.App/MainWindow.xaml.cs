using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
        ProjectTreeViewControl.AssetReorderRequested += _viewModel.ReorderAsset;
        ProjectTreeViewControl.RenameAssetRequested += OnRenameAssetRequested;
        ProjectTreeViewControl.ReQuantizeContextRequested += _viewModel.RequestReQuantizeById;
        ProjectTreeViewControl.ReQuantizeSelectedRequested += OnReQuantizeSelectedRequested;
        ProjectTreeViewControl.DeleteRequested += () => _viewModel.DeleteSelectedCommand.Execute(null);
        ProjectTreeViewControl.DeleteFolderRequested += _viewModel.DeleteFolder;
        ProjectTreeViewControl.ReQuantizeFolderRequested += OnReQuantizeFolderRequested;
        PixelEditorViewControl.PaintStrokeStarted += _viewModel.BeginPaintStroke;
        PixelEditorViewControl.PixelPainted += _viewModel.PaintPixel;
        PixelEditorViewControl.PaintStrokeEnded += _viewModel.EndPaintStroke;
        PixelEditorViewControl.PixelPicked += _viewModel.PickColorAt;
        ImageViewerViewControl.PixelPicked += _viewModel.PickColorAt;
        _viewModel.PaletteStrip.SwatchDoubleClicked += OnPaletteSwatchDoubleClicked;
        _viewModel.ReQuantizeRequested += OnReQuantizeRequested;
        _viewModel.HelpRequested += OnHelpRequested;
        _viewModel.MetatileGridSizeNeeded += OnMetatileGridSizeNeeded;

        // F1 is intercepted at the raw Win32 message level (WM_KEYDOWN), not through any WPF routed-event
        // or command mechanism. Two prior attempts (a plain <KeyBinding Key="F1">, then a CommandBinding
        // for the built-in ApplicationCommands.Help) both still depend on SOME element having actual WPF
        // keyboard focus for the event/command to route through at all — several of this window's own
        // child controls (Pixel Editor's paint canvas, Image Viewer, Palette Strip) never end up holding
        // real keyboard focus after being clicked, so nothing was there to route through. An HwndSource
        // hook fires on any keystroke reaching this window's native handle, independent of WPF focus state
        // entirely — the one mechanism that can't have this problem.
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource hwndSource) hwndSource.AddHook(WndProc);
        };

        RestoreWindowGeometry();
    }

    private const int WM_KEYDOWN = 0x0100;
    private const int VK_F1 = 0x70;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_KEYDOWN && wParam.ToInt32() == VK_F1)
        {
            if (_viewModel.ShowHelpCommand.CanExecute(null)) _viewModel.ShowHelpCommand.Execute(null);
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Non-modal by deliberate exception to this app's otherwise-universal "every window is modal" rule
    /// (see the Map/Metatile Editor design decisions) — Help is pure reference content with no result to
    /// hand back and no owner-window state to keep in sync, so there's no Closing-semantics/enable-disable
    /// machinery to invent, and the whole point is to be able to leave it open while working elsewhere.
    /// Re-invoking (F1 again, or the menu item again) brings the existing window forward instead of
    /// opening a second copy.
    /// </summary>
    private HelpWindow? _helpWindow;

    private void OnHelpRequested()
    {
        if (_helpWindow is not null)
        {
            _helpWindow.Activate();
            return;
        }

        _helpWindow = new HelpWindow { Owner = this };
        _helpWindow.Closed += (_, _) => _helpWindow = null;
        _helpWindow.Show();
    }

    /// <summary>
    /// Fires once, right after a project finishes loading, only for a legacy .zxngc saved before
    /// <see cref="ProjectState.MetatileGridSize"/> was asked up front in the New Project dialog. Resolves
    /// it immediately instead of lazily deferring to whichever feature (Metatile Editor, New Map, Atlas
    /// Slicer) the user happens to touch first.
    /// </summary>
    private void OnMetatileGridSizeNeeded()
    {
        if (MetatileGridSizeWindow.EnsureChosen(_viewModel.Project, this)) _viewModel.HasUnsavedChanges = true;
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
        var slicerVm = new AtlasSlicerViewModel(preview, decoded.Width, decoded.Height, cellWidth, cellHeight,
            _viewModel.Project, category, decoded.Rgba32);
        var dialog = new AtlasSlicerWindow { DataContext = slicerVm, Owner = this };

        if (dialog.ShowDialog() == true)
        {
            if (slicerVm.SliceIntoMetatileBlocks && slicerVm.ResolvedGridSize is { } gridSize)
            {
                await _viewModel.ImportSlicedAsMetatilesAsync(source, category, folderPath, slicerVm.BuildParameters(), gridSize, slicerVm.SkipDuplicateCells, slicerVm.IncludedUnits);
            }
            else
            {
                var placeTransparentFirst = slicerVm.CanOfferTransparentTileFirst && slicerVm.PlaceTransparentTileFirst;
                await _viewModel.ImportSlicedAsync(source, category, folderPath, slicerVm.BuildParameters(), slicerVm.SkipDuplicateCells, placeTransparentFirst, slicerVm.IncludedUnits);
            }
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

    /// <summary>
    /// Bulk-re-quantizes an explicit set of tiles/sprites — the tree's "Re-quantize N selected..."
    /// context menu item, which only enables when every selected leaf shares one category (enforced
    /// in ProjectTreeView's ContextMenuOpening handler, so <see cref="MainViewModel.ReQuantizeSelectedAsync"/>
    /// can safely assume a single category here).
    /// </summary>
    private async void OnReQuantizeSelectedRequested(List<Guid> assetIds)
    {
        var vm = new ReQuantizeViewModel(DitherMode.None,
            $"Re-converts {assetIds.Count} selected tile(s)/sprite(s) from their original source regions.");
        var dialog = new ReQuantizeWindow { DataContext = vm, Owner = this, Title = $"Re-quantize {assetIds.Count} selected" };
        if (dialog.ShowDialog() != true) return;

        await _viewModel.ReQuantizeSelectedAsync(assetIds, vm.SelectedDitherMode);
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
            _viewModel.CreateNewProject(vm.FullPath, vm.MetatileGridSize);
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
        var initialResults = _viewModel.PreviewExport(_ => ExportChunkSize.EightKb);
        if (initialResults.Count == 0)
        {
            MessageBox.Show(
                "Nothing to export yet — import and convert some tiles/sprites first.",
                "Export for Next", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var vm = new ExportViewModel(initialResults, _viewModel.RecomputeExportRow);
        var dialog = new ExportWindow { DataContext = vm, Owner = this };
        if (dialog.ShowDialog() != true) return;

        // Regenerate the FINAL plan using whatever chunk size the user ended up choosing per row —
        // the dialog only ever showed a live preview per-row, never mutated initialResults itself —
        // then drop whichever rows the user unchecked (CanExport already guarantees at least one stays).
        var includedRowKeys = new HashSet<string>(vm.IncludedRowKeys);
        var results = _viewModel.PreviewExport(vm.ChunkSizeForRow, vm.PixelOrderForRow)
            .Where(r => includedRowKeys.Contains(r.RowKey))
            .ToList();

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

    /// <summary>
    /// Every project has <see cref="ProjectState.MetatileGridSize"/> locked by now — chosen up front in
    /// the New Project dialog, or (for a legacy .zxngc that predates that) resolved once right after
    /// loading, by <see cref="OnMetatileGridSizeNeeded"/>. If the user cancelled that legacy prompt it can
    /// still be null here; nothing left to do about that at this point, the editor just opens with an
    /// empty (and, since nothing can be created without a size, permanently empty) palette.
    /// </summary>
    private void Settings_OnClick(object sender, RoutedEventArgs e)
    {
        var vm = new SettingsViewModel();
        var dialog = new SettingsWindow { DataContext = vm, Owner = this };
        dialog.ShowDialog();
    }

    private void MetatileEditor_OnClick(object sender, RoutedEventArgs e)
    {
        var vm = new MetatileEditorViewModel(_viewModel.Project);
        var dialog = new MetatileEditorWindow { DataContext = vm, Owner = this };
        dialog.ShowDialog();
        if (vm.HasChanges) _viewModel.HasUnsavedChanges = true;
        // Creating the very first metatile of some Kind+GridSize can silently auto-create that Kind's
        // reserved blank TILE too (if this project never had one) — the Metatile Editor's own list
        // refreshes itself from the project directly, but the main tree does not, on its own.
        _viewModel.SyncReservedBlankAssetNodes();
        // A GridSize=1 project's "delete metatile" can delete the underlying TILE instead (see
        // MetatileEditorViewModel.DeleteUnderlyingTile) — this window has no tree of its own to drop the
        // node from, so it's removed here once the dialog closes.
        _viewModel.SyncDeletedAssetNodes(vm.DeletedTileAssetIds);
    }

    private void MapEditor_OnClick(object sender, RoutedEventArgs e)
    {
        var vm = new MapEditorViewModel(_viewModel.Project);
        var dialog = new MapEditorWindow { DataContext = vm, Owner = this };
        dialog.ShowDialog();
        if (vm.HasChanges) _viewModel.HasUnsavedChanges = true;
        // Creating a brand-new map silently ensures both Kinds' reserved blank metatile for its
        // GridSize, which can in turn auto-create a reserved blank TILE the main tree doesn't know about yet.
        _viewModel.SyncReservedBlankAssetNodes();
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
