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

    /// <summary>Read-only access to the live project state for windows (Metatile/Map Editor) that need broad read/write access beyond what a narrow callback could reasonably capture — mirrors how Core services (MetatileService, etc.) already take ProjectState directly rather than individual fields.</summary>
    public ProjectState Project => _project;

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

    public string WindowTitle => $"ZxNext Studio — {ProjectTitle}{(HasUnsavedChanges ? " *" : "")}";

    /// <summary>Raised (handled by code-behind, which owns dialog ownership) when the user wants to re-quantize the currently selected tile/sprite.</summary>
    public event Action<Guid, DitherMode>? ReQuantizeRequested;

    /// <summary>Raised (handled by code-behind, which owns window ownership) when the user asks for Help — F1 or the Help menu.</summary>
    public event Action? HelpRequested;

    [RelayCommand]
    private void ShowHelp() => HelpRequested?.Invoke();

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
        SourceImages.DeleteImageRequested += DeleteSourceImage;
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

    /// <summary>
    /// Core (ReservedBlankAssetService) has no UI dependency, so when it silently auto-creates a
    /// category's reserved blank tile as a side effect of some OTHER operation — a plain import, an
    /// Atlas Slicer batch, or (via EnsureBlankMetatile) creating the first metatile or map of a given
    /// Kind+GridSize — nothing ever calls Tree.AddAssetNode for it. Without this, the tile exists in
    /// ProjectState.Assets (correct for export/save) but has no tree node at all until the next full
    /// project load, which rebuilds the tree from every asset (see the Load handler's own loop) — a real
    /// gap this call plugs by adding any missing node (always at the front, matching where the reserved
    /// blank's SortIndex already puts it for export). Cheap, safe to call after any operation that MIGHT
    /// have created one, even when it didn't — every already-present node is skipped as a no-op.
    /// </summary>
    public void SyncReservedBlankAssetNodes()
    {
        foreach (var asset in _project.Assets.Where(a => a.IsReservedBlank))
        {
            if (Tree.FindLeafNode(asset.Id) is null) Tree.AddAssetNode(asset, _project, insertAtFront: true);
        }
    }

    /// <summary>Called by the tree view when a source image dropped onto a category folder (or 8bpp sub-folder) already matches the target cell size exactly.</summary>
    public ImportOutcome ImportIntoCategory(SourceImageViewModel source, AssetCategory category, string folderPath, int? maxColors = null)
    {
        var decoded = DecodeSource(source);
        var result = AssetImporter.Import(_project, source.Model, decoded.Rgba32, category, folderPath, DitherMode.None, maxColors);

        if (result.Success)
        {
            Tree.AddAssetNode(result.Asset!, _project);
            HasUnsavedChanges = true;
            PixelEditor.StatusText = $"Imported {result.Asset!.Name} into {folderPath} ({category}).";
        }
        else if (result.Reason == ImportFailureReason.RedundantWithReservedBlank)
        {
            PixelEditor.StatusText = result.Error ?? ""; // not a real failure — friendly "already covered" message
        }
        else
        {
            PixelEditor.StatusText = $"Import failed: {result.Error}";
        }

        SyncReservedBlankAssetNodes();
        return new ImportOutcome(result.Success, result.Reason, category, result.Error);
    }

    /// <summary>
    /// Called after the user confirms the Layer2 placement dialog — <paramref name="placedRgba32"/>
    /// is already cropped/padded to its final <paramref name="width"/>x<paramref name="height"/> by
    /// <see cref="Layer2PlacementViewModel.BuildPlacedRgba"/>. <paramref name="offsetX"/>/
    /// <paramref name="offsetY"/>/<paramref name="cropWidth"/>/<paramref name="cropHeight"/> record
    /// EXACTLY what was read from the source (which can be smaller than width/height when padded)
    /// so a later re-quantize can reproduce this same composition instead of over-reading past the
    /// source's actual bounds. <paramref name="maxColors"/> matters most for Layer2_640x256x4 (only
    /// 15 usable colours) — real art almost always needs reducing to fit.
    /// </summary>
    public ImportOutcome ImportLayer2Placement(
        SourceImageViewModel source, AssetCategory category, string folderPath, byte[] placedRgba32, int width, int height,
        int offsetX, int offsetY, int cropWidth, int cropHeight, int? maxColors = null)
    {
        var placedSource = new SourceImage
        {
            Id = source.Model.Id,
            FileName = source.Model.FileName,
            FilePath = source.Model.FilePath,
            Width = width,
            Height = height
        };
        var result = AssetImporter.Import(_project, placedSource, placedRgba32, category, folderPath, DitherMode.None, maxColors,
            sourceOffsetX: offsetX, sourceOffsetY: offsetY, sourceCropWidth: cropWidth, sourceCropHeight: cropHeight);

        if (result.Success)
        {
            Tree.AddAssetNode(result.Asset!, _project);
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
    public async Task ImportSlicedAsync(SourceImageViewModel source, AssetCategory category, string folderPath, AtlasSliceParameters parameters, bool skipDuplicateCells, bool placeTransparentTileFirst = false)
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

                    var result = AssetImporter.Import(_project, cellSource, cellRgba, category, folderPath, DitherMode.None, null, rect.X, rect.Y);
                    if (result.Success) imported.Add(result.Asset!);
                    else if (result.Reason == ImportFailureReason.RedundantWithReservedBlank) duplicatesSkipped++; // pixel-identical to the reserved blank tile — same bucket as a same-batch duplicate
                    else failed++;
                }
            });

            var movedTransparentToFront = false;
            Guid? transparentAssetId = null;
            if (placeTransparentTileFirst)
            {
                // Only the FIRST fully-transparent import (there could be several identical ones if
                // duplicate-skipping is off) needs moving — the rest, if any, just stay wherever they
                // landed; there's nothing meaningfully "more correct" about moving every one of them.
                var transparentAsset = imported.FirstOrDefault(a => TransparentTileDetector.IsAssetFullyTransparent(_project, a));
                if (transparentAsset is not null)
                {
                    // SortIndex only needs to be LOWER than every sibling in this category for
                    // AssetExportIndexer's OrderBy-based ranking to put it first — it doesn't need to be
                    // dense or non-negative (see NextSortIndex, which only ever reads the Max()).
                    var siblingIndices = _project.Assets.Where(a => a.Category == category && a.Id != transparentAsset.Id).Select(a => a.SortIndex).ToList();
                    transparentAsset.SortIndex = (siblingIndices.Count > 0 ? siblingIndices.Min() : 0) - 1;
                    movedTransparentToFront = true;
                    transparentAssetId = transparentAsset.Id;
                }
            }

            // The tree's display order is plain insertion order, not SortIndex (see AddAssetNode's own
            // doc comment) — the transparent asset needs to go in at the front of its folder explicitly,
            // or the SortIndex change above would be invisible in the tree despite being real for export.
            foreach (var asset in imported) Tree.AddAssetNode(asset, _project, insertAtFront: asset.Id == transparentAssetId);
            SyncReservedBlankAssetNodes(); // the category's reserved blank tile may have been auto-created mid-slice (first cell in) and never got a node of its own above

            if (imported.Count > 0) HasUnsavedChanges = true;

            var message = $"Sliced {source.Model.FileName}: {imported.Count} cell(s) imported into {folderPath}.";
            if (movedTransparentToFront) message += " Transparent tile placed first.";
            if (duplicatesSkipped > 0) message += $" {duplicatesSkipped} duplicate(s) skipped.";
            if (failed > 0) message += $" {failed} failed (likely palette overflow — try Re-quantize category from the overflow dialog on a single-tile import, or reduce colours before slicing).";
            PixelEditor.StatusText = message;
        });
    }

    /// <summary>
    /// Slices an atlas into gridSize*gridSize-tile blocks (e.g. 16x16 for gridSize=2) for Tile4Bpp/Tile8Bpp
    /// only — each block is split into its 8x8 tiles (<see cref="AtlasSliceParameters.ComputeSubCellRects"/>),
    /// every tile imported exactly like <see cref="ImportSlicedAsync"/> does, and a <see cref="Metatile"/> is
    /// auto-built from the resulting tile ids and added to the project's metatile library. Duplicate-tile
    /// detection is scoped to THIS one slicing pass only (raw-pixel signatures of every sub-tile cut so
    /// far in this atlas) — deliberately not project-wide, since palettes differ across the project and a
    /// cross-project pixel match wouldn't mean anything (confirmed with the user). If any sub-tile of a
    /// block fails to import (palette overflow etc.), the whole block is skipped — no partially-built
    /// metatile with a missing cell is ever created.
    /// </summary>
    public async Task ImportSlicedAsMetatilesAsync(SourceImageViewModel source, AssetCategory category, string folderPath,
        AtlasSliceParameters blockParameters, int gridSize, bool skipDuplicateCells)
    {
        var decoded = DecodeSource(source);
        var blocks = blockParameters.ComputeCellRects(decoded.Width, decoded.Height);
        var kind = category == AssetCategory.Tile4Bpp ? MetatileKind.FourBpp : MetatileKind.EightBpp;

        await RunBusyAsync($"Slicing {source.Model.FileName} into metatiles...", async progress =>
        {
            var seenTileSignatures = new Dictionary<string, Guid>();
            var newlyImported = new List<GraphicsAsset>();
            var metatilesCreated = 0;
            var tilesReused = 0;
            var blocksFailed = 0;
            var reportStep = Math.Max(1, blocks.Count / 100);

            await Task.Run(() =>
            {
                for (var b = 0; b < blocks.Count; b++)
                {
                    var block = blocks[b];
                    if (b % reportStep == 0 || b == blocks.Count - 1)
                    {
                        progress.Report(new BusyProgress($"Slicing {source.Model.FileName}: block {b + 1}/{blocks.Count}...", b + 1, blocks.Count));
                    }

                    var subRects = AtlasSliceParameters.ComputeSubCellRects(block, gridSize);
                    var cells = new List<MetatileCell>(subRects.Count);
                    var blockOk = true;

                    foreach (var subRect in subRects)
                    {
                        var subRgba = PixelRectExtractor.Extract(decoded.Rgba32, decoded.Width, subRect);
                        var signature = skipDuplicateCells ? Convert.ToBase64String(subRgba) : null;

                        if (signature is not null && seenTileSignatures.TryGetValue(signature, out var existingId))
                        {
                            tilesReused++;
                            cells.Add(new MetatileCell { TileAssetId = existingId });
                            continue;
                        }

                        var subSource = new SourceImage
                        {
                            Id = source.Model.Id,
                            FileName = $"{source.Model.FileName}_{subRect.X}_{subRect.Y}",
                            FilePath = source.Model.FilePath,
                            Width = subRect.Width,
                            Height = subRect.Height
                        };

                        var result = AssetImporter.Import(_project, subSource, subRgba, category, folderPath, DitherMode.None, null, subRect.X, subRect.Y);
                        Guid tileId;
                        if (result.Success)
                        {
                            newlyImported.Add(result.Asset!);
                            tileId = result.Asset!.Id;
                        }
                        else if (result.Reason == ImportFailureReason.RedundantWithReservedBlank)
                        {
                            tileId = ReservedBlankAssetService.EnsureBlankTile(_project, category).Id;
                            tilesReused++;
                        }
                        else
                        {
                            blockOk = false;
                            break;
                        }

                        if (signature is not null) seenTileSignatures[signature] = tileId;
                        cells.Add(new MetatileCell { TileAssetId = tileId });
                    }

                    if (!blockOk)
                    {
                        blocksFailed++;
                        continue;
                    }

                    var autoName = $"{source.Model.FileName}_{block.X}_{block.Y}";
                    var metatileResult = MetatileService.Create(_project, autoName, kind, gridSize, cells);
                    if (metatileResult.Success) metatilesCreated++;
                    else blocksFailed++;
                }
            });

            foreach (var asset in newlyImported) Tree.AddAssetNode(asset, _project);
            SyncReservedBlankAssetNodes();

            if (newlyImported.Count > 0 || metatilesCreated > 0) HasUnsavedChanges = true;

            var message = $"Sliced {source.Model.FileName}: {metatilesCreated} metatile(s) created, {newlyImported.Count} tile(s) imported, {tilesReused} tile(s) reused.";
            if (blocksFailed > 0) message += $" {blocksFailed} block(s) skipped (likely palette overflow — reduce colours before slicing).";
            PixelEditor.StatusText = message;
        });
    }

    /// <summary>
    /// Re-crops an asset's own source region, correctly reproducing a Layer2 placement's padding
    /// (a padded canvas is bigger than what was actually read from the source — naively re-cropping
    /// a canvas-sized rectangle would run off the end of the source) as well as an ordinary tile's
    /// exact-size crop.
    /// </summary>
    private static byte[] BuildCellRgba(DecodedImage decoded, GraphicsAsset asset)
    {
        if (asset.Category.IsLayer2())
        {
            return Layer2Composer.Compose(decoded.Rgba32, decoded.Width, decoded.Height,
                asset.SourceOffsetX, asset.SourceOffsetY, asset.SourceCropWidth, asset.SourceCropHeight,
                asset.Width, asset.Height);
        }

        var rect = new PixelRect(asset.SourceOffsetX, asset.SourceOffsetY, asset.Width, asset.Height);
        return PixelRectExtractor.Extract(decoded.Rgba32, decoded.Width, rect);
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
        var cellRgba = BuildCellRgba(decoded, asset);
        var cellSource = new SourceImage { Id = source.Id, FileName = asset.Name, FilePath = source.FilePath, Width = asset.Width, Height = asset.Height };

        var originalName = asset.Name;

        var result = _project.ReplaceFlatPaletteAsset(asset, () =>
            AssetImporter.Import(_project, cellSource, cellRgba, asset.Category, asset.FolderPath, newDitherMode, maxFourBppColors, asset.SourceOffsetX, asset.SourceOffsetY, excludeAssetIdFromNameCheck: asset.Id,
                sourceCropWidth: asset.SourceCropWidth, sourceCropHeight: asset.SourceCropHeight));
        if (!result.Success)
        {
            PixelEditor.StatusText = $"Re-quantize failed: {result.Error}";
            return new ImportOutcome(false, result.Reason, asset.Category, result.Error);
        }

        result.Asset!.Name = originalName;
        result.Asset.SortIndex = asset.SortIndex; // re-quantize replaces the asset object; keep its original export/tile-index position
        Tree.ReplaceAssetNode(asset.Id, result.Asset, _project); // keeps its position in the list instead of jumping to the bottom

        if (asset.Category.UsesPaletteBank())
        {
            // The re-quantized asset may have landed in a different (or brand-new) slot, leaving its
            // OLD slot with nothing left using it — drop any such now-orphaned slot so the bank never
            // silently accumulates dead palettes nobody references.
            _project.CompactPaletteBank(asset.Category);
        }
        else
        {
            // ReplaceFlatPaletteAsset may have remapped every OTHER asset sharing this folder's flat
            // palette too (not just this one) — refresh whatever's currently shown, and drop any
            // pending undo entry, since it could reference now-stale pixel indices.
            _undoStack.Clear();
            OnSelectionChanged();
        }

        HasUnsavedChanges = true;
        PixelEditor.StatusText = $"Re-quantized {originalName} (dither: {newDitherMode}).";
        return new ImportOutcome(true, ImportFailureReason.None, asset.Category, null);
    }

    /// <summary>
    /// Rebuilds an entire 4bpp category's palette bank from scratch, re-quantizing every asset in
    /// it down to at most <paramref name="maxColorsPerTile"/> colours each. Not undoable (the undo
    /// stack is cleared) — a bulk structural change like this can't be safely snapshotted with the
    /// current single-level undo model. Any tile that still doesn't fit even after reduction, or
    /// that lost its source image, is removed from the project rather than left with a now-invalid
    /// palette slot reference; the user is told which ones need to be re-imported by hand. Every
    /// removed/replaced tile's <see cref="MetatileCell.TileAssetId"/> references are kept valid
    /// (remapped to the replacement, or to the category's reserved blank if there's no
    /// replacement) — see <see cref="ProjectState.RemapMetatileTileReferences"/> for why this
    /// matters. The category's own reserved blank tile is never fed through re-quantization itself
    /// (it has no source image, so it would always hit the "no source" removal branch) — instead
    /// it's explicitly recreated fresh in the rebuilt bank up front, and every metatile cell that
    /// referenced the old one is remapped to the new one.
    /// </summary>
    public async Task ReQuantizeCategoryAsync(AssetCategory category, int maxColorsPerTile)
    {
        if (!category.UsesPaletteBank()) return;

        await RunBusyAsync($"Re-quantizing {category}...", async progress =>
        {
            _undoStack.Clear();

            var hasReservedBlank = category is AssetCategory.Tile4Bpp or AssetCategory.Tile8Bpp;
            var assetsInCategory = _project.Assets.Where(a => a.Category == category && !a.IsReservedBlank).OrderBy(a => a.SortIndex).ToList();
            var oldBlank = hasReservedBlank ? _project.Assets.FirstOrDefault(a => a.Category == category && a.IsReservedBlank) : null;
            _project.BankFor(category).Clear();
            if (oldBlank is not null) _project.Assets.Remove(oldBlank);

            // Recreate the reserved blank first, in the now-empty bank, so every tile that can't be
            // carried forward (overflow or missing source) has somewhere safe to redirect its
            // metatile cells to instead of going dangling. Sprite4Bpp (the only other bank category)
            // has no reserved-blank concept at all, so this is skipped entirely for it.
            var newBlank = hasReservedBlank ? ReservedBlankAssetService.EnsureBlankTile(_project, category) : null;

            var succeeded = 0;
            var failed = new List<string>();
            var replaced = new List<(Guid OldId, GraphicsAsset New)>();
            var removedIds = new List<Guid>();

            if (oldBlank is not null && newBlank is not null)
            {
                _project.RemapMetatileTileReferences(oldBlank.Id, newBlank.Id);
                replaced.Add((oldBlank.Id, newBlank));
            }

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
                        if (newBlank is not null) _project.RemapMetatileTileReferences(asset.Id, newBlank.Id);
                        removedIds.Add(asset.Id);
                        failed.Add(asset.Name);
                        continue;
                    }

                    var decoded = _decoder.Decode(source.FilePath);
                    var rect = new PixelRect(asset.SourceOffsetX, asset.SourceOffsetY, asset.Width, asset.Height);
                    var cellRgba = PixelRectExtractor.Extract(decoded.Rgba32, decoded.Width, rect);
                    var cellSource = new SourceImage { Id = source.Id, FileName = asset.Name, FilePath = source.FilePath, Width = asset.Width, Height = asset.Height };

                    var result = AssetImporter.Import(_project, cellSource, cellRgba, category, asset.FolderPath, asset.DitherMode, maxColorsPerTile, asset.SourceOffsetX, asset.SourceOffsetY, excludeAssetIdFromNameCheck: asset.Id);
                    _project.Assets.Remove(asset);

                    if (result.Success)
                    {
                        result.Asset!.Name = asset.Name;
                        result.Asset.SortIndex = asset.SortIndex; // full category rebuild replaces every asset; keep original relative order
                        _project.RemapMetatileTileReferences(asset.Id, result.Asset.Id);
                        replaced.Add((asset.Id, result.Asset));
                        succeeded++;
                    }
                    else
                    {
                        if (newBlank is not null) _project.RemapMetatileTileReferences(asset.Id, newBlank.Id);
                        removedIds.Add(asset.Id);
                        failed.Add(asset.Name);
                    }
                }
            });

            foreach (var (oldId, newAsset) in replaced) Tree.ReplaceAssetNode(oldId, newAsset, _project);
            foreach (var id in removedIds) Tree.RemoveAssetNode(id, _project);

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
    /// Re-quantizes every tile/sprite/Layer2 image currently placed in ONE specific folder, using
    /// the given dithering — scoped to that folder alone. Deliberately NOT scoped by source image:
    /// the same source image can legitimately be dropped into several folders (each with its own
    /// palette) and each needs to be free to use different dithering, so a folder-level trigger is
    /// the only grain that makes sense — re-quantizing here must never touch assets sitting in a
    /// different folder just because they happen to share a source image. Triggered from the tree's
    /// folder right-click menu. Overflows are reported but NOT individually dialog-prompted (that
    /// would be a dialog per tile) — use the per-tile Ctrl+R / Re-quantize... flow (which does
    /// prompt) for any that fail here. Can touch many assets at once, so it runs off the UI thread
    /// behind a busy indicator.
    /// </summary>
    public async Task ReQuantizeFolderAsync(AssetCategory category, string folderPath, DitherMode newDitherMode)
    {
        var affected = _project.Assets.Where(a => a.Category == category && a.FolderPath == folderPath).ToList();
        if (affected.Count == 0)
        {
            PixelEditor.StatusText = "This folder has no tiles/sprites to re-quantize yet.";
            return;
        }

        await ReQuantizeAssetsAsync(affected, category, newDitherMode, "Re-quantizing folder...", "in this folder");
    }

    /// <summary>
    /// Re-quantizes an explicit set of tiles/sprites — the tree's "Re-quantize N selected..."
    /// context menu action, for a Ctrl/Shift-click multi-selection. All given ids must share one
    /// category (the tree's context menu disables this action otherwise, so it can safely assume
    /// that here); same per-asset flow and overflow reporting as <see cref="ReQuantizeFolderAsync"/>,
    /// just scoped to an explicit id set instead of a whole folder.
    /// </summary>
    public async Task ReQuantizeSelectedAsync(IReadOnlyList<Guid> assetIds, DitherMode newDitherMode)
    {
        var affected = _project.Assets.Where(a => assetIds.Contains(a.Id)).ToList();
        if (affected.Count == 0) return;

        var category = affected[0].Category;
        await ReQuantizeAssetsAsync(affected, category, newDitherMode, "Re-quantizing selected...", "in the selection");
    }

    private async Task ReQuantizeAssetsAsync(List<GraphicsAsset> affected, AssetCategory category, DitherMode newDitherMode, string busyMessage, string scopeDescription)
    {
        await RunBusyAsync(busyMessage, async progress =>
        {
            var succeeded = 0;
            var overflowed = new List<string>();
            var missingSource = new List<string>();
            var replaced = new List<(Guid OldId, GraphicsAsset New)>();

            await Task.Run(() =>
            {
                var decodedBySourceId = new Dictionary<Guid, DecodedImage>();

                for (var i = 0; i < affected.Count; i++)
                {
                    var asset = affected[i];
                    progress.Report(new BusyProgress($"Re-quantizing {asset.Name} ({i + 1}/{affected.Count})...", i + 1, affected.Count));

                    if (asset.SourceImageId is not { } sourceId ||
                        _project.SourceImages.FirstOrDefault(s => s.Id == sourceId) is not { } source)
                    {
                        missingSource.Add(asset.Name);
                        continue;
                    }

                    if (!decodedBySourceId.TryGetValue(sourceId, out var decoded))
                    {
                        decoded = _decoder.Decode(source.FilePath);
                        decodedBySourceId[sourceId] = decoded;
                    }

                    var cellRgba = BuildCellRgba(decoded, asset);
                    var cellSource = new SourceImage { Id = source.Id, FileName = asset.Name, FilePath = source.FilePath, Width = asset.Width, Height = asset.Height };

                    // Frees this asset's OWN colours before attempting the replacement — otherwise an
                    // asset that already uses most/all of a tight flat palette (Layer2_640x256x4's 16
                    // colours especially) would be spuriously blocked by its own about-to-be-discarded
                    // colours. All-or-nothing per asset: a failed replacement restores everything for
                    // THIS asset (and any sibling the compaction touched) exactly as it was.
                    var result = _project.ReplaceFlatPaletteAsset(asset, () =>
                        AssetImporter.Import(_project, cellSource, cellRgba, asset.Category, asset.FolderPath, newDitherMode, null, asset.SourceOffsetX, asset.SourceOffsetY, excludeAssetIdFromNameCheck: asset.Id,
                            sourceCropWidth: asset.SourceCropWidth, sourceCropHeight: asset.SourceCropHeight));
                    if (result.Success)
                    {
                        result.Asset!.Name = asset.Name;
                        result.Asset.SortIndex = asset.SortIndex; // re-quantize replaces the asset object; keep its original export/tile-index position
                        replaced.Add((asset.Id, result.Asset));
                        succeeded++;
                    }
                    else if (result.Reason is ImportFailureReason.PaletteOverflow or ImportFailureReason.FlatPaletteFull)
                    {
                        overflowed.Add(asset.Name);
                    }
                }

                // Bank categories aren't compacted by ReplaceFlatPaletteAsset (only flat folder
                // palettes are) — a re-quantized asset may have landed in a different/new slot,
                // leaving its old one orphaned, so compact the bank once after the whole batch.
                if (replaced.Count > 0 && category.UsesPaletteBank())
                {
                    _project.CompactPaletteBank(category);
                }
            });

            foreach (var (oldId, newAsset) in replaced) Tree.ReplaceAssetNode(oldId, newAsset, _project);

            if (succeeded > 0)
            {
                HasUnsavedChanges = true;
                // Palette compaction above can remap every surviving asset's pixel indices — any
                // pending undo entry (e.g. a delete-with-restore) could point at now-stale indices.
                _undoStack.Clear();
                OnSelectionChanged(); // re-derive whatever's shown — palette contents/positions may have moved
            }

            var problems = new List<string>();
            if (overflowed.Count > 0) problems.Add($"{overflowed.Count} hit palette overflow and were left unchanged: {string.Join(", ", overflowed)}");
            if (missingSource.Count > 0) problems.Add($"{missingSource.Count} have no source image left and were skipped: {string.Join(", ", missingSource)}");

            PixelEditor.StatusText = problems.Count == 0
                ? $"Re-quantized {succeeded} tile(s)/sprite(s) {scopeDescription} (dither: {newDitherMode})."
                : $"Re-quantized {succeeded}; {string.Join("; ", problems)}.";
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
    /// Deletes whatever's currently multi-selected (Ctrl/Shift-click in the tree), or — if nothing's
    /// multi-selected — the single last-clicked node. Always safe: palette banks/folder palettes and
    /// source images are independent, standing objects that never require a specific asset to exist,
    /// so there is no consistency conflict to check for — deleting an asset only ever removes that
    /// one row from the project.
    /// </summary>
    [RelayCommand]
    private void DeleteSelected()
    {
        var targetIds = Tree.GetMultiSelectedAssetIds();
        if (targetIds.Count == 0 && Tree.SelectedNode is { IsFolder: false, AssetId: { } singleId })
        {
            targetIds = [singleId];
        }

        if (targetIds.Count == 0)
        {
            // No leaf asset checked/selected — if the Delete key was pressed while a user-created
            // folder is selected instead, delegate to DeleteFolder so Delete behaves consistently
            // everywhere in the tree, not just on leaves (folders were previously only deletable
            // via their right-click menu).
            if (Tree.SelectedNode is { IsFolder: true, IsCategoryRoot: false } folderNode)
            {
                DeleteFolder(folderNode);
            }
            return;
        }

        var targetAssets = targetIds
            .Select(id => _project.Assets.FirstOrDefault(a => a.Id == id))
            .Where(asset => asset is not null)
            .Select(asset => asset!)
            .ToList();

        // The reserved blank tile is still unconditionally undeletable — unrelated to the cascade below,
        // it has no "delete it too" option, ever.
        var reservedBlanks = targetAssets.Where(a => a.IsReservedBlank).ToList();
        if (reservedBlanks.Count > 0)
        {
            var reasons = reservedBlanks.Select(a => $"Cannot delete '{a.Name}': it's the reserved blank tile every map's grid layer relies on.");
            MessageBox.Show(string.Join("\n", reasons), "Cannot delete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Tiles used in metatiles / sprites placed on maps are no longer a hard block — see
        // CascadeDeletionService's own doc comment. The confirmation dialog spells out everything that
        // would ALSO go: metatiles containing a deleted tile, the map cells that placed them (become
        // Blank), and every map placement of a deleted sprite.
        var impact = CascadeDeletionService.PlanAssetDeletion(_project, targetAssets);

        var confirmLines = new List<string>
        {
            targetAssets.Count == 1 ? "Delete this tile/sprite?" : $"Delete {targetAssets.Count} selected tiles/sprites?"
        };
        if (!impact.IsEmpty)
        {
            if (impact.AffectedMetatiles.Count > 0)
            {
                confirmLines.Add($"Also deletes {impact.AffectedMetatiles.Count} metatile(s) that use them: {string.Join(", ", impact.AffectedMetatiles.Select(m => m.Name))}.");
            }
            if (impact.AffectedMapCells.Count > 0)
            {
                var cellDetail = string.Join(", ", impact.AffectedMapCells.Select(x => $"{x.Map.Name} ({x.CellCount} cell(s))"));
                confirmLines.Add($"{impact.AffectedMapCells.Sum(x => x.CellCount)} map cell(s) will become Blank: {cellDetail}.");
            }
            if (impact.AffectedSpritePlacements.Count > 0)
            {
                var placementDetail = string.Join(", ", impact.AffectedSpritePlacements.Select(x => $"{x.Map.Name} ({x.PlacementCount})"));
                confirmLines.Add($"{impact.AffectedSpritePlacements.Sum(x => x.PlacementCount)} sprite placement(s) will be removed: {placementDetail}.");
            }
            confirmLines.Add("This cannot be undone.");
        }

        var confirm = MessageBox.Show(string.Join("\n", confirmLines), "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (!impact.IsEmpty)
        {
            CascadeDeletionService.ExecuteAssetDeletion(_project, targetAssets, impact);
        }

        var removed = new List<GraphicsAsset>();
        var affectedFlatFolders = new HashSet<(AssetCategory Category, string FolderPath)>();
        var affectedBankCategories = new HashSet<AssetCategory>();
        foreach (var asset in targetAssets)
        {
            _project.RemoveAsset(asset.Id);
            Tree.RemoveAssetNode(asset.Id, _project); // clears SelectedNode (and so the edit panels, via OnSelectionChanged) if this was the selected asset
            removed.Add(asset);
            if (asset.Category.UsesPaletteBank()) affectedBankCategories.Add(asset.Category);
            else affectedFlatFolders.Add((asset.Category, asset.FolderPath));
        }

        Tree.ClearMultiSelection();
        if (removed.Count == 0) return;

        // A flat folder palette (or, for tile/sprite 4bpp, a bank slot) can lose colours no other
        // surviving asset still uses — compact it so that space is usable again. This remaps every
        // surviving asset's pixel indices, so a plain undo that just re-adds the removed assets back
        // with their OLD indices would point at the wrong colours; clear undo instead, same as every
        // other operation in this codebase that rebuilds a shared palette. The cascade above (metatile/
        // map changes) is likewise not undoable, for the same "too complex to safely snapshot" reasoning
        // DeleteFolder/DeleteSourceImage already use.
        if (affectedFlatFolders.Count > 0 || affectedBankCategories.Count > 0 || !impact.IsEmpty)
        {
            foreach (var (category, folderPath) in affectedFlatFolders)
            {
                _project.CompactFlatPalette(category, folderPath);
            }
            foreach (var category in affectedBankCategories)
            {
                _project.CompactPaletteBank(category);
            }
            _undoStack.Clear();
            RefreshSelectedAssetRender();
            PixelEditor.StatusText = impact.IsEmpty
                ? $"Deleted {removed.Count} tile(s)/sprite(s) and freed unused palette colours."
                : $"Deleted {removed.Count} tile(s)/sprite(s), {impact.AffectedMetatiles.Count} metatile(s), and updated affected map(s).";
        }
        else
        {
            _undoStack.Push(() =>
            {
                foreach (var asset in removed)
                {
                    _project.Assets.Add(asset);
                    Tree.AddAssetNode(asset, _project);
                }
            });
            PixelEditor.StatusText = $"Deleted {removed.Count} tile(s)/sprite(s).";
        }

        HasUnsavedChanges = true;
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

        var blockingReasons = affected
            .Select(asset => ReferenceIntegrityService.CanDeleteAsset(_project, asset))
            .Where(check => !check.CanDelete)
            .Select(check => check.BlockingReason!)
            .ToList();
        if (blockingReasons.Count > 0)
        {
            MessageBox.Show(string.Join("\n", blockingReasons), "Cannot delete folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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

        var blockingReasons = affected
            .Select(asset => ReferenceIntegrityService.CanDeleteAsset(_project, asset))
            .Where(check => !check.CanDelete)
            .Select(check => check.BlockingReason!)
            .ToList();
        if (blockingReasons.Count > 0)
        {
            MessageBox.Show(string.Join("\n", blockingReasons), "Cannot delete source image", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var message = affected.Count == 0
            ? $"Delete source image \"{source.FileName}\"?"
            : $"Delete source image \"{source.FileName}\"? This will also delete the {affected.Count} tile(s)/sprite(s) placed from it. This cannot be undone.";
        if (MessageBox.Show(message, "Delete source image", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var affectedFlatFolders = new HashSet<(AssetCategory Category, string FolderPath)>();
        var affectedBankCategories = new HashSet<AssetCategory>();
        foreach (var asset in affected)
        {
            _project.RemoveAsset(asset.Id);
            Tree.RemoveAssetNode(asset.Id, _project);
            if (asset.Category.UsesPaletteBank()) affectedBankCategories.Add(asset.Category);
            else affectedFlatFolders.Add((asset.Category, asset.FolderPath));
        }

        foreach (var (category, folderPath) in affectedFlatFolders)
        {
            _project.CompactFlatPalette(category, folderPath);
        }

        // Mirrors the single-asset delete path (see DeleteSelectedCommand) — CompactPaletteBank is the
        // ONLY thing that ever frees a 4bpp bank slot orphaned by the last asset using it, and this
        // cascading delete had been skipping it entirely, leaving a dead, still-"Optimize"-able,
        // save/reload-surviving palette slot behind for a folder with zero real assets left in it.
        foreach (var category in affectedBankCategories)
        {
            _project.CompactPaletteBank(category);
        }

        _project.SourceImages.Remove(source);
        SourceImages.RemoveById(sourceImageId);
        _undoStack.Clear();
        if (affectedFlatFolders.Count > 0 || affectedBankCategories.Count > 0) RefreshSelectedAssetRender();

        HasUnsavedChanges = true;
        PixelEditor.StatusText = affected.Count == 0
            ? $"Deleted source image \"{source.FileName}\"."
            : $"Deleted source image \"{source.FileName}\" and {affected.Count} tile(s)/sprite(s) placed from it.";
    }

    /// <summary>Called (from code-behind, which owns the picker dialog) after the user confirms a new colour for a palette slot in the Next-colour picker — a single, deliberate, infrequent action (not a live-drag callback), never called more than once per dialog confirm. Mutates the shared palette object in place, so re-renders every tile/sprite using it right away, both the big preview and every one of their tree thumbnails (see RefreshThumbnailsSharingPalette) — cheap even for a full 256-asset category (each render is a handful of pixels), so this stays synchronous rather than needing a background pass.</summary>
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
            RefreshThumbnailsSharingPalette();
        });

        PaletteStrip.RefreshSwatches();
        RefreshSelectedAssetRender();
        RefreshThumbnailsSharingPalette();
        HasUnsavedChanges = true;
        PixelEditor.StatusText = $"Palette colour {index} changed — every tile/sprite using this palette updates live.";
    }

    private void RefreshSelectedAssetRender()
    {
        if (_selectedAsset is null) return;
        var rendered = NextBitmapRenderer.Render(_selectedAsset, _project);
        ImageViewer.Bitmap = rendered;
        PixelEditor.Bitmap = rendered;
        // Same bitmap, no extra render cost — keeps the tree row's thumbnail in sync with pixel edits.
        if (Tree.FindLeafNode(_selectedAsset.Id) is { } node) node.Thumbnail = rendered;
    }

    /// <summary>
    /// A palette edit (SetPaletteColor) affects every asset sharing that exact palette — the whole 4bpp
    /// bank SLOT (not just the selected asset's own tile/sprite) or, for a flat category, the whole
    /// folder — not just <see cref="_selectedAsset"/>. Without this, every OTHER affected asset's tree
    /// thumbnail silently kept showing its pre-edit colours until something else happened to re-render it
    /// (selecting it, or a project reload) — much more noticeable now that thumbnails exist at all than
    /// it ever was for the big preview alone (which only ever showed the one selected asset anyway).
    /// </summary>
    private void RefreshThumbnailsSharingPalette()
    {
        if (_selectedAsset is not { } reference) return;
        var category = reference.Category;
        var affected = category.UsesPaletteBank()
            ? _project.Assets.Where(a => a.Category == category && a.PaletteSlotIndex == reference.PaletteSlotIndex)
            : _project.Assets.Where(a => a.Category == category && a.FolderPath == reference.FolderPath);

        foreach (var asset in affected)
        {
            if (Tree.FindLeafNode(asset.Id) is { } node) node.Thumbnail = NextBitmapRenderer.Render(asset, _project);
        }
    }

    /// <summary>
    /// Drag-and-drop reorder from the tree: moves <paramref name="draggedAssetId"/> to sit immediately
    /// before/after <paramref name="targetAssetId"/>, then renumbers real export indices to match. Only
    /// ever touches SortIndex/tree position, never FolderPath — an 8bpp category's sub-folders each own a
    /// separate flat palette, so "moving" a tile to a DIFFERENT folder would mean re-quantizing it, a
    /// fundamentally different (and riskier) operation this does not attempt; both nodes must already
    /// share one folder for those categories (4bpp categories have no sub-folders to begin with, so this
    /// is never a restriction for them). No-op (silently) on any invalid combination — the caller (a
    /// completed drag) has no user-visible way to report a rejection anyway; DragOver already only offers
    /// the Move cursor for combinations this will actually accept.
    /// </summary>
    public void ReorderAsset(Guid draggedAssetId, Guid targetAssetId, bool insertAfter)
    {
        if (draggedAssetId == targetAssetId) return;
        var dragged = _project.Assets.FirstOrDefault(a => a.Id == draggedAssetId);
        var target = _project.Assets.FirstOrDefault(a => a.Id == targetAssetId);
        if (dragged is null || target is null) return;
        if (dragged.IsReservedBlank) return; // must always stay first
        if (dragged.Category != target.Category) return;
        if (!dragged.Category.UsesPaletteBank() && dragged.FolderPath != target.FolderPath) return;
        if (target.IsReservedBlank && !insertAfter) return; // would bump it out of the first slot

        var draggedNode = Tree.FindLeafNode(draggedAssetId);
        var targetNode = Tree.FindLeafNode(targetAssetId);
        if (draggedNode is null || targetNode is null) return;

        var parent = Tree.MoveLeafNode(draggedNode, targetNode, insertAfter);
        if (parent is null) return;

        var newOrder = parent.Children
            .Where(c => c.AssetId is not null)
            .Select(c => _project.Assets.First(a => a.Id == c.AssetId!.Value))
            .ToList();
        AssetReorderService.Reorder(newOrder);
        Tree.RefreshExportIndices(dragged.Category, _project);
        HasUnsavedChanges = true;
        PixelEditor.StatusText = $"Reordered {dragged.Name}.";
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

    /// <summary>
    /// Computes the chunk/ASM plan for every folder (or, for Layer2, every image) that has assets,
    /// without writing anything to disk — used both to populate the Export dialog's initial preview
    /// (called with a plain "always 8KB" selector) and to regenerate the FINAL plan right before
    /// writing, once the user has picked a possibly-different chunk size per row in the dialog.
    /// </summary>
    public List<FolderExportResult> PreviewExport(Func<string, ExportChunkSize> chunkSizeForRow, Func<string, PixelExportOrder>? pixelOrderForRow = null) =>
        ExportService.ExportAll(_project, chunkSizeForRow, pixelOrderForRow);

    /// <summary>
    /// Recomputes just ONE export row (by its RowKey) at a newly-picked chunk size — used for the
    /// Export dialog's live per-row preview when the user changes a ComboBox. Deliberately does NOT
    /// go through <see cref="PreviewExport"/>/<see cref="ExportService.ExportAll"/> (which would
    /// re-pack every OTHER row too just to read one back out) — for a project with many folders or
    /// a large Layer2 image, that would mean redoing real work (byte packing, or Layer2's
    /// per-pixel hardware reordering) on every single ComboBox change instead of only for the row
    /// that actually changed.
    /// </summary>
    public (int ChunkCount, int DataBytes) RecomputeExportRow(string rowKey, ExportChunkSize size)
    {
        var chunkSizeBytes = size.ToByteBoundary();

        var layer2Asset = _project.Assets.FirstOrDefault(a => a.Category.IsLayer2() && a.Name == rowKey);
        if (layer2Asset is not null)
        {
            var result = Layer2Exporter.Export(layer2Asset, _project.GetOrCreateFolderPalette(layer2Asset.Category, layer2Asset.FolderPath), chunkSizeBytes);
            return (result.Chunks.Count, result.Chunks.Sum(c => c.Data.Length));
        }

        var assets = _project.Assets
            .Where(a => !a.Category.IsLayer2() && a.FolderPath == rowKey)
            .OrderBy(a => a.SortIndex)
            .ToList();
        if (assets.Count == 0) return (0, 0);

        var folderResult = ExportService.ExportFolder(_project, rowKey, assets, chunkSizeBytes);
        return (folderResult.Chunks.Count, folderResult.Chunks.Sum(c => c.Data.Length));
    }

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

        // Adding in SortIndex order (not raw list order) is what makes a drag-reordered tree still look
        // reordered after a save/reload — Reorder only ever touches SortIndex, never _project.Assets'
        // own list order (see AssetReorderService's own doc comment), and AddAssetNode always just
        // appends to whichever folder it resolves to, in whatever order it's called.
        foreach (var asset in _project.Assets.OrderBy(a => a.Category).ThenBy(a => a.SortIndex))
        {
            Tree.AddAssetNode(asset, _project);
        }
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
        else if (node is { IsFolder: true, Category: { } category })
        {
            ImageViewer.Bitmap = null;
            ImageViewer.SelectedAssetName = "(no selection)";
            PixelEditor.Bitmap = null;
            PixelEditor.AssetWidth = 0;
            PixelEditor.AssetHeight = 0;

            if (category.UsesPaletteBank())
            {
                var bank = _project.BankFor(category);
                var usedPalettes = bank.Slots.Count;
                var totalColors = bank.Slots.Sum(slot => slot.Slots.Count(c => c is not null));
                PaletteStrip.ShowBank(bank, $"{category} — palette bank overview");
                PixelEditor.StatusText = $"{category}: {usedPalettes}/{PaletteBank.MaxSlots} palette(s) used, {totalColors} colour(s) total.";
            }
            else if (node.FolderPath is { } folderPath && _project.FolderPalettesFor(category).TryGetValue(folderPath, out var palette))
            {
                var capacity = category.FlatPaletteCapacity();
                var usedColors = palette.Slots.Count(c => c is not null);
                PaletteStrip.ShowPalette(palette, $"{node.Name} ({category}) — folder palette, {usedColors}/{capacity} colour(s) used", isFourBpp: false, paletteSlotIndex: 0);
                PixelEditor.StatusText = $"{node.Name}: {usedColors}/{capacity} colour(s) used in this folder's palette.";
            }
            else
            {
                PaletteStrip.Clear();
                PixelEditor.StatusText = $"{node.Name}: no images placed yet — palette is empty.";
            }
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
