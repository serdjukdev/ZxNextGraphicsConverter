using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.App.Rendering;
using ZxNext.App.Views;
using ZxNext.Core.AtlasSlicer;
using ZxNext.Core.Conversion;
using ZxNext.Core.Editing;
using ZxNext.Core.Export;
using ZxNext.Core.Imaging;
using ZxNext.Core.Model;
using ZxNext.Core.PaletteAllocation;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;

namespace ZxNext.App.ViewModels;

public record ImportOutcome(bool Success, ImportFailureReason Reason, AssetCategory Category, string? Error);

/// <summary>
/// Composition root view model: owns the project state, the tree, the source-images library,
/// and the three right-pane panels; orchestrates drag-drop conversion, selection, and project
/// save/load/recent-list management.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private ProjectState _project = new();
    private readonly IImageDecoder _decoder;
    private GraphicsAsset? _selectedAsset;
    private readonly Stack<Action> _undoStack = new();
    private readonly Dictionary<(int X, int Y), int> _strokeOriginalIndices = new();
    private GraphicsAsset? _strokeAsset;

    public ProjectTreeViewModel Tree { get; }
    public SourceImagesPanelViewModel SourceImages { get; }
    public ImageViewerViewModel ImageViewer { get; }
    public PaletteStripViewModel PaletteStrip { get; }
    public PixelEditorViewModel PixelEditor { get; }

    public ObservableCollection<string> RecentProjects { get; } = [];

    [ObservableProperty]
    private string? currentProjectPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string projectTitle = "Untitled";

    /// <summary>True whenever the in-memory project has changes not yet written to disk — nothing autosaves, so this drives the title-bar "*" and the discard-confirmation prompts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool hasUnsavedChanges;

    public string WindowTitle => $"ZX Next Graphics Converter — {ProjectTitle}{(HasUnsavedChanges ? " *" : "")}";

    /// <summary>Raised (handled by code-behind, which owns dialog ownership) when the user wants to re-quantize the currently selected tile/sprite.</summary>
    public event Action<Guid, DitherMode>? ReQuantizeRequested;

    public MainViewModel(
        ProjectTreeViewModel tree,
        SourceImagesPanelViewModel sourceImages,
        ImageViewerViewModel imageViewer,
        PaletteStripViewModel paletteStrip,
        PixelEditorViewModel pixelEditor,
        IImageDecoder decoder)
    {
        Tree = tree;
        SourceImages = sourceImages;
        ImageViewer = imageViewer;
        PaletteStrip = paletteStrip;
        PixelEditor = pixelEditor;
        _decoder = decoder;

        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectTreeViewModel.SelectedNode))
            {
                OnSelectionChanged();
            }
        };

        SourceImages.SourceImageAdded += source => _project.SourceImages.Add(source);
        SourceImages.DitherModeChangedByUser += async (sourceImageId, mode) => await ReQuantizeBySourceImageAsync(sourceImageId, mode);
        PaletteStrip.OptimizeBankRequested += async () => await OptimizeCurrentBankAsync();

        RefreshRecentProjects();
    }

    /// <summary>A busy-indicator status update: <paramref name="Current"/>/<paramref name="Total"/> (both set, Total > 0) draws a real filling percentage; either omitted keeps the indeterminate marquee — used for single Core calls with no countable sub-steps (e.g. a plain project save).</summary>
    private readonly record struct BusyProgress(string Message, int? Current = null, int? Total = null);

    /// <summary>
    /// Shows a non-closable "please wait" indicator (owner window disabled) for the duration of
    /// <paramref name="work"/>, so any operation slow enough to matter — project load/save, bulk
    /// re-quantize, atlas slicing, export — never just freezes the UI with no feedback. The heavy
    /// part of <paramref name="work"/> should run via <c>Task.Run</c> so the UI thread stays free to
    /// keep the indicator animating and pump <paramref name="work"/>'s progress reports; anything
    /// that touches WPF-bound collections (the tree, source images list) must happen back on the
    /// UI thread, i.e. after an awaited <c>Task.Run</c> call, never inside it.
    /// </summary>
    private static async Task RunBusyAsync(string initialMessage, Func<IProgress<BusyProgress>, Task> work)
    {
        var owner = Application.Current.MainWindow;
        var busy = new BusyWindow { Owner = owner, Message = initialMessage };
        if (owner is not null) owner.IsEnabled = false;
        busy.Show();

        try
        {
            var progress = new Progress<BusyProgress>(p =>
            {
                busy.Message = p.Message;
                busy.SetProgress(p.Current, p.Total);
            });
            await work(progress);
        }
        finally
        {
            busy.AllowCloseAndClose();
            if (owner is not null) owner.IsEnabled = true;
        }
    }

    /// <summary>Decodes a source image's pixels — used both to import directly and to feed the atlas slicer preview.</summary>
    public DecodedImage DecodeSource(SourceImageViewModel source) => _decoder.Decode(source.Model.FilePath);

    /// <summary>Called by the tree view when a source image dropped onto a category folder (or 8bpp sub-folder) already matches the target cell size exactly.</summary>
    public ImportOutcome ImportIntoCategory(SourceImageViewModel source, AssetCategory category, string folderPath, int? maxFourBppColors = null)
    {
        var decoded = DecodeSource(source);
        var result = AssetImporter.Import(_project, source.Model, decoded.Rgba32, category, folderPath, source.DitherMode, maxFourBppColors);

        if (result.Success)
        {
            Tree.AddAssetNode(result.Asset!);
            HasUnsavedChanges = true;
            PixelEditor.StatusText = $"Imported {result.Asset!.Name} into {folderPath} ({category}).";
        }
        else
        {
            PixelEditor.StatusText = $"Import failed: {result.Error}";
        }

        return new ImportOutcome(result.Success, result.Reason, category, result.Error);
    }

    /// <summary>Called after the user confirms the Layer2 placement dialog — <paramref name="placedRgba32"/> is already cropped/padded to its final <paramref name="width"/>x<paramref name="height"/> by <see cref="Layer2PlacementViewModel.BuildPlacedRgba"/>.</summary>
    public ImportOutcome ImportLayer2Placement(SourceImageViewModel source, AssetCategory category, string folderPath, byte[] placedRgba32, int width, int height)
    {
        var placedSource = new SourceImage
        {
            Id = source.Model.Id,
            FileName = source.Model.FileName,
            FilePath = source.Model.FilePath,
            Width = width,
            Height = height
        };
        var result = AssetImporter.Import(_project, placedSource, placedRgba32, category, folderPath, source.DitherMode);

        if (result.Success)
        {
            Tree.AddAssetNode(result.Asset!);
            HasUnsavedChanges = true;
            PixelEditor.StatusText = $"Imported {result.Asset!.Name} into {folderPath} ({category}), {width}x{height}.";
        }
        else
        {
            PixelEditor.StatusText = $"Import failed: {result.Error}";
        }

        return new ImportOutcome(result.Success, result.Reason, category, result.Error);
    }

    /// <summary>Called after the user confirms the atlas slicer dialog for an oversized dropped sprite-sheet/tile-sheet image — can be thousands of cells, so it runs off the UI thread behind a busy indicator. Not used for Layer2 categories, which go through <see cref="ImportLayer2Placement"/> instead (exactly one output image, never a repeating grid).</summary>
    public async Task ImportSlicedAsync(SourceImageViewModel source, AssetCategory category, string folderPath, AtlasSliceParameters parameters, bool skipDuplicateCells)
    {
        var decoded = DecodeSource(source);
        var rects = parameters.ComputeCellRects(decoded.Width, decoded.Height);

        await RunBusyAsync($"Slicing {source.Model.FileName}...", async progress =>
        {
            var seenCellContent = new HashSet<string>();
            var imported = new List<GraphicsAsset>();
            var failed = 0;
            var duplicatesSkipped = 0;
            var reportStep = Math.Max(1, rects.Count / 100);

            await Task.Run(() =>
            {
                for (var i = 0; i < rects.Count; i++)
                {
                    var rect = rects[i];
                    if (i % reportStep == 0 || i == rects.Count - 1)
                    {
                        progress.Report(new BusyProgress($"Slicing {source.Model.FileName}: cell {i + 1}/{rects.Count}...", i + 1, rects.Count));
                    }

                    var cellRgba = PixelRectExtractor.Extract(decoded.Rgba32, decoded.Width, rect);

                    if (skipDuplicateCells)
                    {
                        var signature = Convert.ToBase64String(cellRgba);
                        if (!seenCellContent.Add(signature))
                        {
                            duplicatesSkipped++;
                            continue;
                        }
                    }

                    var cellSource = new SourceImage
                    {
                        Id = source.Model.Id,
                        FileName = $"{source.Model.FileName}_{rect.X}_{rect.Y}",
                        FilePath = source.Model.FilePath,
                        Width = rect.Width,
                        Height = rect.Height
                    };

                    var result = AssetImporter.Import(_project, cellSource, cellRgba, category, folderPath, source.DitherMode, null, rect.X, rect.Y);
                    if (result.Success) imported.Add(result.Asset!);
                    else failed++;
                }
            });

            foreach (var asset in imported) Tree.AddAssetNode(asset);

            if (imported.Count > 0) HasUnsavedChanges = true;

            var message = $"Sliced {source.Model.FileName}: {imported.Count} cell(s) imported into {folderPath}.";
            if (duplicatesSkipped > 0) message += $" {duplicatesSkipped} duplicate(s) skipped.";
            if (failed > 0) message += $" {failed} failed (likely palette overflow — try Re-quantize category from the overflow dialog on a single-tile import, or reduce colours before slicing).";
            PixelEditor.StatusText = message;
        });
    }

    /// <summary>Re-converts one already-placed tile/sprite from its original source region (tracked via SourceOffsetX/Y), replacing its pixels/palette assignment in place.</summary>
    public ImportOutcome ReQuantizeAsset(Guid assetId, DitherMode newDitherMode, int? maxFourBppColors)
    {
        var asset = _project.Assets.FirstOrDefault(a => a.Id == assetId);
        if (asset is null) return new ImportOutcome(false, ImportFailureReason.None, default, "Asset no longer exists.");

        if (asset.SourceImageId is not { } sourceId ||
            _project.SourceImages.FirstOrDefault(s => s.Id == sourceId) is not { } source)
        {
            PixelEditor.StatusText = "Can't re-quantize: source image is no longer in the project.";
            return new ImportOutcome(false, ImportFailureReason.None, asset.Category, "Source image missing.");
        }

        var decoded = _decoder.Decode(source.FilePath);
        var rect = new PixelRect(asset.SourceOffsetX, asset.SourceOffsetY, asset.Width, asset.Height);
        var cellRgba = PixelRectExtractor.Extract(decoded.Rgba32, decoded.Width, rect);
        var cellSource = new SourceImage { Id = source.Id, FileName = asset.Name, FilePath = source.FilePath, Width = asset.Width, Height = asset.Height };

        var originalName = asset.Name;

        var result = AssetImporter.Import(_project, cellSource, cellRgba, asset.Category, asset.FolderPath, newDitherMode, maxFourBppColors, asset.SourceOffsetX, asset.SourceOffsetY);
        if (!result.Success)
        {
            PixelEditor.StatusText = $"Re-quantize failed: {result.Error}";
            return new ImportOutcome(false, result.Reason, asset.Category, result.Error);
        }

        _project.Assets.Remove(asset);
        result.Asset!.Name = originalName;
        Tree.ReplaceAssetNode(asset.Id, result.Asset); // keeps its position in the list instead of jumping to the bottom

        // The re-quantized asset may have landed in a different (or brand-new) slot, leaving its
        // OLD slot with nothing left using it — drop any such now-orphaned slot so the bank never
        // silently accumulates dead palettes nobody references.
        if (asset.Category.UsesPaletteBank()) _project.CompactPaletteBank(asset.Category);

        HasUnsavedChanges = true;
        PixelEditor.StatusText = $"Re-quantized {originalName} (dither: {newDitherMode}).";
        return new ImportOutcome(true, ImportFailureReason.None, asset.Category, null);
    }

    /// <summary>
    /// Rebuilds an entire 4bpp category's palette bank from scratch, re-quantizing every asset in
    /// it down to at most <paramref name="maxColorsPerTile"/> colours each. Not undoable (the undo
    /// stack is cleared) — a bulk structural change like this can't be safely snapshotted with the
    /// current single-level undo model. Any tile that still doesn't fit even after reduction is
    /// removed from the project rather than left with a now-invalid palette slot reference; the
    /// user is told which ones need to be re-imported by hand.
    /// </summary>
    public async Task ReQuantizeCategoryAsync(AssetCategory category, int maxColorsPerTile)
    {
        if (!category.UsesPaletteBank()) return;

        await RunBusyAsync($"Re-quantizing {category}...", async progress =>
        {
            _undoStack.Clear();

            var assetsInCategory = _project.Assets.Where(a => a.Category == category).OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
            _project.BankFor(category).Clear();

            var succeeded = 0;
            var failed = new List<string>();
            var replaced = new List<(Guid OldId, GraphicsAsset New)>();
            var removedIds = new List<Guid>();

            await Task.Run(() =>
            {
                for (var i = 0; i < assetsInCategory.Count; i++)
                {
                    var asset = assetsInCategory[i];
                    progress.Report(new BusyProgress($"Re-quantizing {category}: {asset.Name} ({i + 1}/{assetsInCategory.Count})...", i + 1, assetsInCategory.Count));

                    if (asset.SourceImageId is not { } sourceId ||
                        _project.SourceImages.FirstOrDefault(s => s.Id == sourceId) is not { } source)
                    {
                        _project.Assets.Remove(asset);
                        removedIds.Add(asset.Id);
                        failed.Add(asset.Name);
                        continue;
                    }

                    var decoded = _decoder.Decode(source.FilePath);
                    var rect = new PixelRect(asset.SourceOffsetX, asset.SourceOffsetY, asset.Width, asset.Height);
                    var cellRgba = PixelRectExtractor.Extract(decoded.Rgba32, decoded.Width, rect);
                    var cellSource = new SourceImage { Id = source.Id, FileName = asset.Name, FilePath = source.FilePath, Width = asset.Width, Height = asset.Height };

                    var result = AssetImporter.Import(_project, cellSource, cellRgba, category, asset.FolderPath, asset.DitherMode, maxColorsPerTile, asset.SourceOffsetX, asset.SourceOffsetY);
                    _project.Assets.Remove(asset);

                    if (result.Success)
                    {
                        result.Asset!.Name = asset.Name;
                        replaced.Add((asset.Id, result.Asset));
                        succeeded++;
                    }
                    else
                    {
                        removedIds.Add(asset.Id);
                        failed.Add(asset.Name);
                    }
                }
            });

            foreach (var (oldId, newAsset) in replaced) Tree.ReplaceAssetNode(oldId, newAsset);
            foreach (var id in removedIds) Tree.RemoveAssetNode(id);

            HasUnsavedChanges = true;
            PixelEditor.StatusText = failed.Count == 0
                ? $"Re-quantized {succeeded} tile(s) in {category} to ≤{maxColorsPerTile} colours each."
                : $"Re-quantized {succeeded} tile(s); {failed.Count} still didn't fit and were REMOVED — re-import from Source Images: {string.Join(", ", failed)}.";
        });
    }

    /// <summary>
    /// Called from the palette strip's "Optimize bank" button (only visible while a 4bpp bank is
    /// shown). Re-packs every tile/sprite in the bank into as few slots as possible via
    /// <see cref="PaletteBankOptimizer"/> — colours never change, no asset can be removed (it's
    /// all-or-nothing: if any tile's colours somehow can't be placed, nothing is changed at all),
    /// only slot assignments shift. Not undoable (clears the undo stack), same reasoning as the
    /// other bulk palette operations.
    /// </summary>
    private async Task OptimizeCurrentBankAsync()
    {
        if (PaletteStrip.CurrentBank?.Category is not { } category) return;

        var confirm = MessageBox.Show(
            $"Re-optimize the {category} palette bank? This may move some tiles/sprites to a different (or new) palette slot to reduce how many slots are used overall — the colours themselves never change. Not undoable.",
            "Optimize palette bank", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        await RunBusyAsync($"Optimizing {category} palette bank...", async _ =>
        {
            var result = await Task.Run(() => PaletteBankOptimizer.Optimize(_project, category));

            if (!result.Success)
            {
                PixelEditor.StatusText = $"Couldn't optimize {category}: {result.Error}";
                return;
            }

            _undoStack.Clear();
            HasUnsavedChanges = true;

            OnSelectionChanged(); // re-derive whatever's shown (palette strip / bitmap) first — slot assignments and indices may have moved
            PixelEditor.StatusText = result.SlotsBefore == result.SlotsAfter
                ? $"Optimized {category}: already using the fewest slots possible ({result.SlotsAfter}/{PaletteBank.MaxSlots})."
                : $"Optimized {category}: {result.SlotsBefore} → {result.SlotsAfter} palette(s) used.";
        });
    }

    /// <summary>Called by the tree view (Ctrl+R) to start re-quantizing the currently selected asset.</summary>
    [RelayCommand]
    private void RequestReQuantize()
    {
        if (Tree.SelectedNode is not { IsFolder: false, AssetId: { } assetId }) return;
        RequestReQuantizeById(assetId);
    }

    /// <summary>Same as the Ctrl+R command, but for an explicit asset id — used by the tree's right-click "Re-quantize..." menu item, which doesn't go through tree selection.</summary>
    public void RequestReQuantizeById(Guid assetId)
    {
        var asset = _project.Assets.FirstOrDefault(a => a.Id == assetId);
        if (asset is null) return;
        ReQuantizeRequested?.Invoke(assetId, asset.DitherMode);
    }

    /// <summary>Whether another asset (not <paramref name="assetId"/> itself) already uses this name — names become ASM export labels, so they must be unique project-wide. Case-insensitive, since not every Z80 assembler treats labels as case-sensitive.</summary>
    public bool IsAssetNameTaken(Guid assetId, string name) =>
        _project.Assets.Any(a => a.Id != assetId && string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Renames a tile/sprite — updates both the stored asset (used as its ASM export label) and its tree display.</summary>
    public void RenameAsset(Guid assetId, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;

        var asset = _project.Assets.FirstOrDefault(a => a.Id == assetId);
        if (asset is null) return;

        if (IsAssetNameTaken(assetId, newName))
        {
            PixelEditor.StatusText = $"Rename failed: \"{newName}\" is already used by another tile/sprite (names must be unique — they become ASM labels).";
            return;
        }

        asset.Name = newName;
        var node = Tree.FindLeafNode(assetId);
        if (node is not null) node.Name = newName;

        if (_selectedAsset?.Id == assetId) ImageViewer.SelectedAssetName = newName;

        HasUnsavedChanges = true;
        PixelEditor.StatusText = $"Renamed to {newName}.";
    }

    /// <summary>
    /// Re-quantizes every tile/sprite that was placed from a given source image (drag once, sliced
    /// into many tiles, or dropped several times) using the given dithering. Overflows are reported
    /// but NOT individually dialog-prompted (that would be a dialog per tile) — use the per-tile
    /// Ctrl+R / Re-quantize... flow (which does prompt) for any that fail here. Can touch many assets
    /// at once, so it runs off the UI thread behind a busy indicator.
    /// </summary>
    public async Task ReQuantizeBySourceImageAsync(Guid sourceImageId, DitherMode newDitherMode)
    {
        var affected = _project.Assets.Where(a => a.SourceImageId == sourceImageId).ToList();
        if (affected.Count == 0)
        {
            PixelEditor.StatusText = "No tiles/sprites have been placed from this image yet.";
            return;
        }

        var source = _project.SourceImages.FirstOrDefault(s => s.Id == sourceImageId);
        if (source is null)
        {
            PixelEditor.StatusText = "Can't re-quantize: source image is no longer in the project.";
            return;
        }

        await RunBusyAsync("Re-quantizing placements...", async progress =>
        {
            var succeeded = 0;
            var overflowed = new List<string>();
            var replaced = new List<(Guid OldId, GraphicsAsset New)>();

            await Task.Run(() =>
            {
                var decoded = _decoder.Decode(source.FilePath);

                for (var i = 0; i < affected.Count; i++)
                {
                    var asset = affected[i];
                    progress.Report(new BusyProgress($"Re-quantizing {asset.Name} ({i + 1}/{affected.Count})...", i + 1, affected.Count));

                    var rect = new PixelRect(asset.SourceOffsetX, asset.SourceOffsetY, asset.Width, asset.Height);
                    var cellRgba = PixelRectExtractor.Extract(decoded.Rgba32, decoded.Width, rect);
                    var cellSource = new SourceImage { Id = source.Id, FileName = asset.Name, FilePath = source.FilePath, Width = asset.Width, Height = asset.Height };

                    var result = AssetImporter.Import(_project, cellSource, cellRgba, asset.Category, asset.FolderPath, newDitherMode, null, asset.SourceOffsetX, asset.SourceOffsetY);
                    if (result.Success)
                    {
                        _project.Assets.Remove(asset);
                        result.Asset!.Name = asset.Name;
                        replaced.Add((asset.Id, result.Asset));
                        succeeded++;
                    }
                    else if (result.Reason == ImportFailureReason.PaletteOverflow)
                    {
                        overflowed.Add(asset.Name);
                    }
                }

                // Every re-quantized asset may have moved to a different slot, orphaning its old one.
                foreach (var fourBppCategory in replaced.Select(r => r.New.Category).Where(c => c.UsesPaletteBank()).Distinct())
                {
                    _project.CompactPaletteBank(fourBppCategory);
                }
            });

            foreach (var (oldId, newAsset) in replaced) Tree.ReplaceAssetNode(oldId, newAsset);

            if (succeeded > 0) HasUnsavedChanges = true;
            PixelEditor.StatusText = overflowed.Count == 0
                ? $"Re-quantized {succeeded} tile(s)/sprite(s) placed from this image."
                : $"Re-quantized {succeeded}; {overflowed.Count} hit palette overflow and were left unchanged: {string.Join(", ", overflowed)}.";
        });
    }

    /// <summary>Called by the pixel editor view when a paint stroke (mouse down) starts, so every pixel touched during the drag is captured as one undo step instead of one step per pixel.</summary>
    public void BeginPaintStroke()
    {
        _strokeOriginalIndices.Clear();
        _strokeAsset = _selectedAsset;
    }

    /// <summary>Called by the pixel editor view when the user paints a pixel, using the currently selected palette swatch as the colour. Restricted to the asset's own assigned palette by construction — there is no way to pass an out-of-palette colour here.</summary>
    public void PaintPixel(int x, int y)
    {
        if (_selectedAsset is null) return;
        if (PaletteStrip.SelectedSwatchIndex is not { } newIndex)
        {
            PixelEditor.StatusText = "Pick a palette colour first (click a swatch above), then paint.";
            return;
        }

        var asset = _selectedAsset;
        var oldIndex = AssetPixelEditor.GetPixelIndex(asset, x, y);
        if (oldIndex == newIndex) return;

        if (asset == _strokeAsset)
        {
            _strokeOriginalIndices.TryAdd((x, y), oldIndex);
        }

        AssetPixelEditor.SetPixelIndex(asset, x, y, newIndex);
        RefreshSelectedAssetRender();
        HasUnsavedChanges = true;
    }

    /// <summary>Called by the pixel editor view when a paint stroke ends (mouse up) — commits the whole stroke as a single undo step.</summary>
    public void EndPaintStroke()
    {
        if (_strokeAsset is null || _strokeOriginalIndices.Count == 0)
        {
            _strokeAsset = null;
            return;
        }

        var asset = _strokeAsset;
        var restores = new Dictionary<(int X, int Y), int>(_strokeOriginalIndices);
        _undoStack.Push(() =>
        {
            foreach (var (pixel, oldIndex) in restores)
            {
                AssetPixelEditor.SetPixelIndex(asset, pixel.X, pixel.Y, oldIndex);
            }
            RefreshSelectedAssetRender();
        });

        _strokeOriginalIndices.Clear();
        _strokeAsset = null;
    }

    /// <summary>Eyedropper: called when the user Ctrl+clicks a pixel in either the viewer or the pixel editor — picks that pixel's palette index as the current paint colour.</summary>
    public void PickColorAt(int x, int y)
    {
        if (_selectedAsset is null) return;
        if (x < 0 || y < 0 || x >= _selectedAsset.Width || y >= _selectedAsset.Height) return;

        var index = AssetPixelEditor.GetPixelIndex(_selectedAsset, x, y);
        PaletteStrip.SelectSwatch(index);
        PixelEditor.StatusText = $"Picked colour at ({x},{y}) — palette index {index}.";
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _undoStack.Pop().Invoke();
        HasUnsavedChanges = true;
        PixelEditor.StatusText = "Undone.";
    }

    /// <summary>
    /// Deletes the checkbox-marked tiles/sprites, or (if none are checked) the single natively
    /// selected one. Always safe: palette banks/folder palettes and source images are independent,
    /// standing objects that never require a specific asset to exist, so there is no consistency
    /// conflict to check for — deleting an asset only ever removes that one row from the project.
    /// </summary>
    [RelayCommand]
    private void DeleteSelected()
    {
        var targetIds = Tree.GetMultiSelectedAssetIds();
        if (targetIds.Count == 0 && Tree.SelectedNode is { IsFolder: false, AssetId: { } singleId })
        {
            targetIds = [singleId];
        }
        if (targetIds.Count == 0) return;

        var confirmMessage = targetIds.Count == 1
            ? "Delete this tile/sprite?"
            : $"Delete {targetIds.Count} selected tiles/sprites?";
        var confirm = MessageBox.Show(confirmMessage, "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var removed = new List<GraphicsAsset>();
        foreach (var id in targetIds)
        {
            var asset = _project.Assets.FirstOrDefault(a => a.Id == id);
            if (asset is null) continue;

            _project.RemoveAsset(id);
            Tree.RemoveAssetNode(id); // clears SelectedNode (and so the edit panels, via OnSelectionChanged) if this was the selected asset
            removed.Add(asset);
        }

        Tree.ClearMultiSelection();
        if (removed.Count == 0) return;

        _undoStack.Push(() =>
        {
            foreach (var asset in removed)
            {
                _project.Assets.Add(asset);
                Tree.AddAssetNode(asset);
            }
        });

        HasUnsavedChanges = true;
        PixelEditor.StatusText = $"Deleted {removed.Count} tile(s)/sprite(s).";
    }

    /// <summary>
    /// Permanently deletes a user-created 8bpp sub-folder: every tile/sprite in it plus its own
    /// flat palette. Not undoable — unlike single-asset delete, a folder can hold many assets
    /// and its own standing palette object, and safely snapshotting all of that just for undo
    /// isn't worth the complexity for a rare, deliberate, explicitly-confirmed action (same call
    /// already made for bulk category re-quantize).
    /// </summary>
    public void DeleteFolder(TreeNodeViewModel folderNode)
    {
        if (!folderNode.IsFolder || folderNode.IsCategoryRoot) return;
        if (folderNode.Category is not { } category || folderNode.FolderPath is not { } folderPath) return;

        var affected = _project.Assets.Where(a => a.FolderPath == folderPath).ToList();

        var message = affected.Count == 0
            ? $"Delete empty folder \"{folderNode.Name}\"?"
            : $"Delete folder \"{folderNode.Name}\" and its {affected.Count} tile(s)/sprite(s)? This cannot be undone.";
        if (MessageBox.Show(message, "Delete folder", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        foreach (var asset in affected) _project.RemoveAsset(asset.Id);
        _project.FolderPalettesFor(category).Remove(folderPath);
        Tree.RemoveFolderNode(folderNode);
        _undoStack.Clear();

        HasUnsavedChanges = true;
        PixelEditor.StatusText = affected.Count == 0
            ? $"Deleted empty folder \"{folderNode.Name}\"."
            : $"Deleted folder \"{folderNode.Name}\" and {affected.Count} tile(s)/sprite(s).";
    }

    /// <summary>
    /// Permanently deletes a source image from the library. If any tiles/sprites were placed
    /// from it, warns and deletes those too (they'd otherwise be orphaned — re-quantize/re-crop
    /// needs the source that no longer exists). Not undoable, for the same reason as
    /// <see cref="DeleteFolder"/>.
    /// </summary>
    public void DeleteSourceImage(Guid sourceImageId)
    {
        var source = _project.SourceImages.FirstOrDefault(s => s.Id == sourceImageId);
        if (source is null) return;

        var affected = _project.Assets.Where(a => a.SourceImageId == sourceImageId).ToList();

        var message = affected.Count == 0
            ? $"Delete source image \"{source.FileName}\"?"
            : $"Delete source image \"{source.FileName}\"? This will also delete the {affected.Count} tile(s)/sprite(s) placed from it. This cannot be undone.";
        if (MessageBox.Show(message, "Delete source image", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        foreach (var asset in affected)
        {
            _project.RemoveAsset(asset.Id);
            Tree.RemoveAssetNode(asset.Id);
        }

        _project.SourceImages.Remove(source);
        SourceImages.RemoveById(sourceImageId);
        _undoStack.Clear();

        HasUnsavedChanges = true;
        PixelEditor.StatusText = affected.Count == 0
            ? $"Deleted source image \"{source.FileName}\"."
            : $"Deleted source image \"{source.FileName}\" and {affected.Count} tile(s)/sprite(s) placed from it.";
    }

    /// <summary>Called (from code-behind, which owns the picker dialog) after the user confirms a new colour for a palette slot in the Next-colour picker. Mutates the shared palette object in place, so every tile/sprite using it re-renders correctly next time it's shown.</summary>
    public void SetPaletteColor(int index, NextColor newColor)
    {
        if (PaletteStrip.CurrentPalette is not { } palette) return;

        var previous = palette.Slots[index];
        palette.SetAt(index, newColor);

        _undoStack.Push(() =>
        {
            palette.SetAt(index, previous);
            PaletteStrip.RefreshSwatches();
            RefreshSelectedAssetRender();
        });

        PaletteStrip.RefreshSwatches();
        RefreshSelectedAssetRender();
        HasUnsavedChanges = true;
        PixelEditor.StatusText = $"Palette colour {index} changed — every tile/sprite using this palette updates live.";
    }

    private void RefreshSelectedAssetRender()
    {
        if (_selectedAsset is null) return;
        var rendered = NextBitmapRenderer.Render(_selectedAsset, _project);
        ImageViewer.Bitmap = rendered;
        PixelEditor.Bitmap = rendered;
    }

    [RelayCommand]
    private async Task OpenRecentProject(string path)
    {
        if (!ConfirmDiscardUnsavedChanges()) return;
        await LoadProjectFromAsync(path);
    }

    [RelayCommand]
    private async Task SaveProject()
    {
        if (CurrentProjectPath is null)
        {
            PixelEditor.StatusText = "This project has no location yet — use File → Save Project As... first.";
            return;
        }
        await SaveToAsync(CurrentProjectPath);
    }

    /// <summary>
    /// True if the caller should proceed (New/Open/Close). If there are unsaved changes, asks
    /// Save/Don't Save/Cancel — saving in place when the project already has a location, or
    /// falling back to a plain discard-or-cancel prompt when it doesn't (no location to save to
    /// without opening a Save As dialog, which callers of this method don't have an owner window for).
    /// </summary>
    public bool ConfirmDiscardUnsavedChanges()
    {
        if (!HasUnsavedChanges) return true;

        if (CurrentProjectPath is not null)
        {
            var result = MessageBox.Show(
                "Save changes to the current project before continuing?",
                "Unsaved changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            switch (result)
            {
                case MessageBoxResult.Yes:
                    SaveTo(CurrentProjectPath);
                    return true;
                case MessageBoxResult.No:
                    return true;
                default:
                    return false;
            }
        }

        var discard = MessageBox.Show(
            "This project has unsaved changes and no save location yet (use Save Project As first if you want to keep them). Continue and discard them?",
            "Unsaved changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return discard == MessageBoxResult.Yes;
    }

    /// <summary>Creates a brand-new project directly at the given file path (name + location chosen by the user in the New Project dialog) and saves it immediately, so it always has a real location on disk. Always empty (no source images yet), so no busy indicator needed.</summary>
    public void CreateNewProject(string projectFilePath)
    {
        _project = new ProjectState();
        RebuildFromProject();
        SaveTo(projectFilePath);
    }

    /// <summary>Re-decodes every source image in the project, which can be slow for a project with many/large images — runs off the UI thread behind a busy indicator.</summary>
    public async Task LoadProjectFromAsync(string path)
    {
        await RunBusyAsync("Opening project...", async progress =>
        {
            ProjectState loaded;
            try
            {
                loaded = await Task.Run(() => ProjectService.Load(path));
            }
            catch (Exception ex)
            {
                PixelEditor.StatusText = $"Failed to open project at {path}: {ex.Message}";
                return;
            }

            _project = loaded;
            CurrentProjectPath = path;
            ProjectTitle = Path.GetFileNameWithoutExtension(path);
            HasUnsavedChanges = false;
            RecentProjectsStore.AddRecent(path);
            RefreshRecentProjects();
            await RebuildFromProjectAsync(progress);
            PixelEditor.StatusText = $"Opened project from {path}.";
        });
    }

    /// <summary>Explicit user-triggered save (Save / Save As), wrapped with a busy indicator since a project with many images can take a moment to write. The plain synchronous <see cref="SaveTo"/> below stays for internal call sites (New Project, the unsaved-changes discard prompt) where a project is either always empty or the operation is a rare "confirm before quitting" gate not worth the extra async plumbing.</summary>
    public async Task SaveToAsync(string path)
    {
        await RunBusyAsync("Saving project...", async _ =>
        {
            try
            {
                await Task.Run(() => ProjectService.Save(_project, path));
            }
            catch (Exception ex)
            {
                PixelEditor.StatusText = $"Failed to save project to {path}: {ex.Message}";
                return;
            }

            CurrentProjectPath = path;
            ProjectTitle = Path.GetFileNameWithoutExtension(path);
            HasUnsavedChanges = false;
            RecentProjectsStore.AddRecent(path);
            RefreshRecentProjects();
            PixelEditor.StatusText = $"Project saved to {path}.";
        });
    }

    public void SaveTo(string path)
    {
        try
        {
            ProjectService.Save(_project, path);
        }
        catch (Exception ex)
        {
            PixelEditor.StatusText = $"Failed to save project to {path}: {ex.Message}";
            return;
        }

        CurrentProjectPath = path;
        ProjectTitle = Path.GetFileNameWithoutExtension(path);
        HasUnsavedChanges = false;
        RecentProjectsStore.AddRecent(path);
        RefreshRecentProjects();
        PixelEditor.StatusText = $"Project saved to {path}.";
    }

    /// <summary>Computes the chunk/ASM plan for every folder that has assets, without writing anything to disk — used to populate the Export dialog's preview.</summary>
    public List<FolderExportResult> PreviewExport() => ExportService.ExportAll(_project);

    /// <summary>Writes the given (already-computed) export plan to disk — can be a lot of files for a big project, so it runs off the UI thread behind a busy indicator.</summary>
    public async Task WriteExportAsync(IReadOnlyList<FolderExportResult> results, string outputDirectory)
    {
        await RunBusyAsync("Exporting...", async _ =>
        {
            await Task.Run(() => ExportService.WriteToDisk(results, outputDirectory));
        });
    }

    /// <summary>Sub-folder structure isn't persisted separately — it's implied by folder-palette keys and asset FolderPaths, so reconstruct it before adding the leaf asset nodes.</summary>
    private void RebuildTreeAndFolders()
    {
        Tree.Reset();

        var folderPaths = new HashSet<string>();
        foreach (var path in _project.Sprite8BppFolderPalettes.Keys) folderPaths.Add(path);
        foreach (var path in _project.Tile8BppFolderPalettes.Keys) folderPaths.Add(path);
        foreach (var path in _project.Layer2_256x192FolderPalettes.Keys) folderPaths.Add(path);
        foreach (var path in _project.Layer2_320x256FolderPalettes.Keys) folderPaths.Add(path);
        foreach (var path in _project.Layer2_640x256x4FolderPalettes.Keys) folderPaths.Add(path);
        foreach (var asset in _project.Assets) folderPaths.Add(asset.FolderPath);
        foreach (var path in folderPaths.OrderBy(p => p, StringComparer.Ordinal)) Tree.EnsureFolderPath(path);

        foreach (var asset in _project.Assets) Tree.AddAssetNode(asset);
    }

    private void ResetEditorsAfterProjectChange()
    {
        _selectedAsset = null;
        _undoStack.Clear();
        ImageViewer.Bitmap = null;
        ImageViewer.SelectedAssetName = "(no selection)";
        PixelEditor.Bitmap = null;
        PixelEditor.AssetWidth = 0;
        PixelEditor.AssetHeight = 0;
        PaletteStrip.Clear();
    }

    /// <summary>Used only for a brand-new (always-empty) project — no images to decode, so no async/busy-indicator machinery is needed.</summary>
    private void RebuildFromProject()
    {
        RebuildTreeAndFolders();

        SourceImages.Images.Clear();
        foreach (var source in _project.SourceImages)
        {
            var decoded = _decoder.Decode(source.FilePath);
            SourceImages.AddExisting(source, decoded.Rgba32);
        }

        ResetEditorsAfterProjectChange();
    }

    /// <summary>Used when opening an existing project — decodes every source image off the UI thread, reporting progress, since that's the genuinely slow part for a project with many/large images.</summary>
    private async Task RebuildFromProjectAsync(IProgress<BusyProgress> progress)
    {
        RebuildTreeAndFolders();
        SourceImages.Images.Clear();

        var sources = _project.SourceImages.ToList();
        var decodedSources = await Task.Run(() =>
        {
            var results = new List<(SourceImage Source, byte[] Rgba32)>(sources.Count);
            for (var i = 0; i < sources.Count; i++)
            {
                progress.Report(new BusyProgress($"Decoding source image {i + 1}/{sources.Count}...", i + 1, sources.Count));
                results.Add((sources[i], _decoder.Decode(sources[i].FilePath).Rgba32));
            }
            return results;
        });

        foreach (var (source, rgba32) in decodedSources) SourceImages.AddExisting(source, rgba32);

        ResetEditorsAfterProjectChange();
    }

    private void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        foreach (var path in RecentProjectsStore.Load()) RecentProjects.Add(path);
    }

    private void OnSelectionChanged()
    {
        var node = Tree.SelectedNode;
        var asset = node?.AssetId is { } id ? _project.Assets.FirstOrDefault(a => a.Id == id) : null;
        _selectedAsset = asset;
        _undoStack.Clear();

        if (asset is not null)
        {
            var rendered = NextBitmapRenderer.Render(asset, _project);
            ImageViewer.Bitmap = rendered;
            ImageViewer.SelectedAssetName = asset.Name;

            PixelEditor.Bitmap = rendered;
            PixelEditor.AssetWidth = asset.Width;
            PixelEditor.AssetHeight = asset.Height;

            var usesBank = asset.Category.UsesPaletteBank();
            var palette = usesBank
                ? _project.BankFor(asset.Category).Slots[asset.PaletteSlotIndex]
                : _project.GetOrCreateFolderPalette(asset.Category, asset.FolderPath);
            var bank = usesBank ? _project.BankFor(asset.Category) : null;
            var usedColors = usesBank ? CountDistinctColorsUsed(asset, palette.TransparentIndex) : (int?)null;

            PaletteStrip.ShowPalette(
                palette,
                $"{asset.Category}, slot {asset.PaletteSlotIndex}, dither: {asset.DitherMode}",
                usesBank,
                asset.PaletteSlotIndex,
                bank,
                usedColors);

            PixelEditor.StatusText = $"Editing {asset.Name} — pick a palette colour above, then click/drag on the pixels to paint.";
        }
        else if (node is { IsFolder: true, Category: AssetCategory.Sprite4Bpp or AssetCategory.Tile4Bpp })
        {
            ImageViewer.Bitmap = null;
            ImageViewer.SelectedAssetName = "(no selection)";
            PixelEditor.Bitmap = null;
            PixelEditor.AssetWidth = 0;
            PixelEditor.AssetHeight = 0;

            var bank = _project.BankFor(node.Category!.Value);
            var usedPalettes = bank.Slots.Count;
            var totalColors = bank.Slots.Sum(slot => slot.Slots.Count(c => c is not null));
            PaletteStrip.ShowBank(bank, $"{node.Category} — palette bank overview");
            PixelEditor.StatusText = $"{node.Category}: {usedPalettes}/{PaletteBank.MaxSlots} palette(s) used, {totalColors} colour(s) total.";
        }
        else
        {
            ImageViewer.Bitmap = null;
            ImageViewer.SelectedAssetName = "(no selection)";
            PixelEditor.Bitmap = null;
            PixelEditor.AssetWidth = 0;
            PixelEditor.AssetHeight = 0;
            PaletteStrip.Clear();
            PixelEditor.StatusText = "Select a tile or sprite to edit its pixels.";
        }
    }

    /// <summary>How many distinct non-transparent palette indices a 4bpp asset's own pixels actually reference — its shared palette slot itself may hold more colours, added there by other tiles/sprites.</summary>
    private static int CountDistinctColorsUsed(GraphicsAsset asset, int transparentIndex)
    {
        var seen = new HashSet<int>();
        for (var y = 0; y < asset.Height; y++)
        {
            for (var x = 0; x < asset.Width; x++)
            {
                var index = AssetPixelEditor.GetPixelIndex(asset, x, y);
                if (index != transparentIndex) seen.Add(index);
            }
        }
        return seen.Count;
    }
}
