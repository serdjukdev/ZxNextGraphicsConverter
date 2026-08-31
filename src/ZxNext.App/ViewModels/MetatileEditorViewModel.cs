using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.App.Rendering;
using ZxNext.App.Views;
using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

public record MetatileKindOption(MetatileKind Kind, string Label);

/// <summary>
/// Backs the modal Metatile Editor. Left side: a visual tile palette (click a tile, then click a draft
/// cell to paint it there) and the project-wide metatile library for the selected Kind (browse/delete,
/// both shown as rendered thumbnails, not just names). Right side: build a new metatile by painting each
/// cell of a GridSize x GridSize draft, with a large live preview. Every Create/Delete mutates
/// <see cref="ProjectState"/> directly and immediately — no separate "commit" step, matching every other
/// panel in this app.
/// </summary>
public partial class MetatileEditorViewModel : ObservableObject
{
    private readonly ProjectState _project;

    public IReadOnlyList<MetatileKindOption> AvailableKinds { get; } =
    [
        new(MetatileKind.FourBpp, "4bpp (hardware Tilemap)"),
        new(MetatileKind.EightBpp, "8bpp (software Tile layer)")
    ];

    /// <summary>Set true by a successful Create or Delete — read once by the caller (MainWindow) after the dialog closes, to decide whether to mark the project as having unsaved changes.</summary>
    public bool HasChanges { get; private set; }

    /// <summary>Tile assets removed via <see cref="DeleteUnderlyingTile"/> (the GridSize=1 "deleting a metatile really means deleting its one tile" path) — this window has no Project Tree of its own to keep in sync, so MainWindow reads this once the dialog closes and removes the matching tree nodes itself, the same way it already does for a tile deleted directly from the tree.</summary>
    public List<Guid> DeletedTileAssetIds { get; } = [];

    [ObservableProperty]
    private MetatileKind selectedKind = MetatileKind.FourBpp;

    /// <summary>
    /// The whole project's fixed metatile size (see <see cref="ProjectState.MetatileGridSize"/>'s own doc
    /// comment) — no longer a per-metatile choice, so unlike every other Draft* property this is a
    /// plain read-only value, set once in the constructor. Normally already locked (New Project dialog, or
    /// MainWindow's once-per-legacy-project load-time prompt) by the time this ViewModel is constructed;
    /// the <c>?? 2</c> fallback below only actually applies for a legacy project whose load-time prompt got
    /// cancelled, so this editor can't null-reference even then (it'll just create metatiles at a size the
    /// project hasn't really committed to).
    /// </summary>
    public int DraftGridSize { get; }

    [ObservableProperty]
    private string draftName = "metatile";

    [ObservableProperty]
    private ObservableCollection<TilePaletteItemViewModel> tilePalette = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaintModeActive))]
    private TilePaletteItemViewModel? selectedPaletteTile;

    /// <summary>True once a palette tile is selected — drives the "these cells are ready to be clicked" highlight on every draft cell, and the instructional banner above the grid.</summary>
    public bool IsPaintModeActive => SelectedPaletteTile is not null;

    [ObservableProperty]
    private ObservableCollection<MetatileListItemViewModel> metatiles = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingExisting))]
    [NotifyPropertyChangedFor(nameof(CreateOrSaveButtonText))]
    private MetatileListItemViewModel? selectedMetatile;

    /// <summary>True while an existing metatile is loaded into the draft — Create/Save writes back into this SAME metatile instead of making a new one (Update never changes its Kind, see MetatileService.Update; changing SelectedKind while editing clears SelectedMetatile via OnSelectedKindChanged, exiting edit mode rather than trying to switch the loaded metatile's Kind in place).</summary>
    public bool IsEditingExisting => SelectedMetatile is not null;

    public string CreateOrSaveButtonText => IsEditingExisting ? "Save Changes" : "Create Metatile";

    [ObservableProperty]
    private ObservableCollection<MetatileCellViewModel> draftCells = [];

    [ObservableProperty]
    private WriteableBitmap? draftPreview;

    [ObservableProperty]
    private bool isDraftValid;

    [ObservableProperty]
    private string? statusText;

    [ObservableProperty]
    private bool isStatusError;

    public MetatileEditorViewModel(ProjectState project)
    {
        _project = project;
        DraftGridSize = project.MetatileGridSize ?? 2;
        EnsureBlankForCurrentSelection();
        RefreshTilePalette();
        RefreshMetatileList();
        ResetDraft();
    }

    partial void OnSelectedKindChanged(MetatileKind value)
    {
        SelectedPaletteTile = null;
        SelectedMetatile = null;
        EnsureBlankForCurrentSelection();
        RefreshTilePalette();
        RefreshMetatileList();
        ResetDraft();
    }

    /// <summary>
    /// Opening the editor on an empty Kind (for the project's one fixed GridSize) shows its reserved
    /// blank metatile right away, matching the same "always there, index 0" guarantee everywhere else —
    /// not deferred until the user happens to create their first real metatile of that Kind. Cheap,
    /// idempotent (see ReservedBlankAssetService) — a no-op once it already exists.
    /// </summary>
    private void EnsureBlankForCurrentSelection()
    {
        var alreadyExists = _project.Metatiles.Any(m => m.Kind == SelectedKind && m.GridSize == DraftGridSize && m.IsReservedBlank);
        if (alreadyExists) return; // no-op — don't spuriously mark the project dirty just for opening/browsing the editor

        ReservedBlankAssetService.EnsureBlankMetatile(_project, SelectedKind, DraftGridSize);
        HasChanges = true; // genuinely just created a metatile (and, the very first time, its category's reserved blank tile)
    }

    partial void OnDraftNameChanged(string value) => UpdateDraftValidity();

    /// <summary>Loads an existing metatile into the draft for editing — Create/Save then writes back into this SAME metatile (see <see cref="IsEditingExisting"/>). Only fires for a genuine selection (the list itself is already filtered to SelectedKind, so there's never a Kind mismatch to reconcile); deselecting (value null, e.g. via NewMetatile) does NOT touch the draft — the caller is responsible for resetting it if that's what deselecting should mean.</summary>
    partial void OnSelectedMetatileChanged(MetatileListItemViewModel? value)
    {
        if (value is null) return;

        var metatile = value.Metatile;
        DraftName = metatile.Name;
        // metatile.GridSize is always exactly DraftGridSize (the project-wide lock — see MetatileService.Create's
        // own enforcement), so DraftCells is already the right size from the constructor's ResetDraft() call;
        // nothing here needs to (or can, DraftGridSize is get-only) trigger another one.

        for (var i = 0; i < metatile.Cells.Count && i < DraftCells.Count; i++)
        {
            var sourceCell = metatile.Cells[i];
            var targetCell = DraftCells[i];
            targetCell.TileAsset = _project.Assets.FirstOrDefault(a => a.Id == sourceCell.TileAssetId);
            targetCell.MirrorX = sourceCell.MirrorX;
            targetCell.MirrorY = sourceCell.MirrorY;
            targetCell.Rotate = sourceCell.Rotate;
            targetCell.PaletteSlotOverride = sourceCell.PaletteSlotOverride;
        }
    }

    private void RefreshTilePalette()
    {
        var category = SelectedKind == MetatileKind.FourBpp ? AssetCategory.Tile4Bpp : AssetCategory.Tile8Bpp;
        TilePalette = new ObservableCollection<TilePaletteItemViewModel>(
            _project.Assets.Where(a => a.Category == category).OrderBy(a => a.SortIndex).Select(a => new TilePaletteItemViewModel(a, _project)));
    }

    private void RefreshMetatileList() =>
        Metatiles = new ObservableCollection<MetatileListItemViewModel>(
            _project.Metatiles.Where(m => m.Kind == SelectedKind).OrderBy(m => m.SortIndex).Select(m => new MetatileListItemViewModel(m, _project)));

    /// <summary>
    /// Drag-and-drop reorder: moves <paramref name="dragged"/> to sit immediately before/after
    /// <paramref name="target"/> in this Kind's gallery, then reassigns real export indices to match —
    /// rewriting every map cell that referenced one of the affected metatiles (see
    /// <see cref="MetatileReorderService"/>, since unlike a tile/sprite's SortIndex a metatile's IS the
    /// literal exported map-cell byte). Restricted to the SAME GridSize — the project is normally locked
    /// to one shared GridSize anyway (see ProjectState.MetatileGridSize), so this only ever matters for a
    /// legacy project migrated in with a genuine pre-existing mix; reordering across GridSizes would be
    /// numerically harmless but pointless (no map ever compares ranks across GridSizes) and needlessly
    /// widens what gets touched. Uses ObservableCollection.Move (not Remove+Insert) so WPF repositions the
    /// existing container instead of tearing it down — same fix as the main tree's own drag-reorder had
    /// to apply, to avoid the exact same "scroll jumps to the top on every drop" bug.
    /// </summary>
    public void ReorderMetatile(Guid draggedMetatileId, Guid targetMetatileId, bool insertAfter)
    {
        if (draggedMetatileId == targetMetatileId) return;
        var draggedItem = Metatiles.FirstOrDefault(m => m.Metatile.Id == draggedMetatileId);
        var targetItem = Metatiles.FirstOrDefault(m => m.Metatile.Id == targetMetatileId);
        if (draggedItem is null || targetItem is null) return;

        var dragged = draggedItem.Metatile;
        var target = targetItem.Metatile;
        if (dragged.IsReservedBlank) return; // must always stay first
        if (dragged.GridSize != target.GridSize) return;
        if (target.IsReservedBlank && !insertAfter) return; // would bump it out of the first slot

        var oldIndex = Metatiles.IndexOf(draggedItem);
        var targetIndex = Metatiles.IndexOf(targetItem);
        var targetIndexAfterRemoval = oldIndex < targetIndex ? targetIndex - 1 : targetIndex;
        var newIndex = insertAfter ? targetIndexAfterRemoval + 1 : targetIndexAfterRemoval;
        Metatiles.Move(oldIndex, newIndex);

        var sameGroup = Metatiles
            .Where(m => m.Metatile.GridSize == dragged.GridSize)
            .Select(m => m.Metatile)
            .ToList();
        MetatileReorderService.Reorder(_project, sameGroup);

        HasChanges = true;
        StatusText = $"Reordered '{dragged.Name}'.";
        IsStatusError = false;
    }

    private void ResetDraft()
    {
        var isFourBpp = SelectedKind == MetatileKind.FourBpp;
        var cells = new ObservableCollection<MetatileCellViewModel>();
        for (var i = 0; i < DraftGridSize * DraftGridSize; i++)
        {
            var cell = new MetatileCellViewModel(_project, isFourBpp);
            cell.PropertyChanged += (_, _) => OnDraftCellChanged();
            cells.Add(cell);
        }
        DraftCells = cells;
        OnDraftCellChanged();
    }

    private void OnDraftCellChanged()
    {
        RenderDraftPreview();
        UpdateDraftValidity();
    }

    private void UpdateDraftValidity() =>
        IsDraftValid = !string.IsNullOrWhiteSpace(DraftName) && DraftCells.Count > 0 && DraftCells.All(c => c.TileAsset is not null);

    private void RenderDraftPreview()
    {
        if (DraftCells.Count == 0 || DraftCells.Any(c => c.TileAsset is null))
        {
            DraftPreview = null;
            return;
        }

        var draft = new Metatile
        {
            Name = "(preview)",
            Kind = SelectedKind,
            GridSize = DraftGridSize,
            Cells = BuildCellsFromDraft()
        };
        DraftPreview = TileGridBitmapRenderer.RenderMetatile(draft, _project);
    }

    /// <summary>Only ever called once <see cref="IsDraftValid"/> is confirmed true, so every cell is guaranteed to have a TileAsset.</summary>
    private List<MetatileCell> BuildCellsFromDraft() =>
        DraftCells.Select(c => new MetatileCell
        {
            TileAssetId = c.TileAsset!.Id,
            MirrorX = c.MirrorX,
            MirrorY = c.MirrorY,
            Rotate = c.Rotate,
            PaletteSlotOverride = c.PaletteSlotOverride
        }).ToList();

    /// <summary>Ctrl+click clears a cell back to empty so a mis-painted cell can be corrected before Create/Save (matches the Map Editor's Ctrl=erase convention) — every cell still needs a real tile before the draft is valid to save, this is a drafting convenience, not a way to leave a permanent gap. Otherwise a plain click paints it with the selected palette tile.</summary>
    [RelayCommand]
    private void AssignSelectedTileToCell(MetatileCellViewModel? cell)
    {
        if (cell is null) return;

        if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            cell.TileAsset = null;
            cell.PaletteSlotOverride = null;
            return;
        }

        if (SelectedPaletteTile is null) return;
        AssignTileToCell(cell, SelectedPaletteTile.Asset);
    }

    /// <summary>Shared by the click-to-paint command above and drag-and-drop from the tile palette (MetatileEditorWindow.xaml.cs's drop handler) — the one real assignment, so the two entry points can never diverge.</summary>
    public void AssignTileToCell(MetatileCellViewModel cell, GraphicsAsset asset)
    {
        cell.TileAsset = asset;
        cell.PaletteSlotOverride = null; // a newly-assigned tile always starts at its own native slot — an override left over from whatever tile occupied this cell before would very likely point at the wrong colours for the new one
    }

    /// <summary>Creates a new metatile, or — if <see cref="SelectedMetatile"/> is currently loaded into the draft for editing — saves the changes back into that SAME metatile in place instead (see <see cref="MetatileService.Update"/>: Id/Kind/GridSize/SortIndex never change, so every map already placing it just picks up the new cells).</summary>
    [RelayCommand]
    private void CreateMetatile()
    {
        if (!IsDraftValid)
        {
            StatusText = "Assign a tile to every cell first.";
            IsStatusError = true;
            return;
        }

        var cells = BuildCellsFromDraft();

        if (SelectedMetatile is { } editing)
        {
            var updateResult = MetatileService.Update(_project, editing.Metatile, DraftName, cells);
            if (!updateResult.Success)
            {
                StatusText = updateResult.Error;
                IsStatusError = true;
                return;
            }

            var savedName = updateResult.Metatile!.Name;
            HasChanges = true;
            RefreshMetatileList();
            // RefreshMetatileList() rebuilds every MetatileListItemViewModel from scratch, so the OLD
            // SelectedMetatile instance is no longer present in the new Metatiles collection — if left
            // as-is, the SelectedItem binding drops it to null on its own, which silently flips
            // IsEditingExisting to false and leaves the draft's already-edited cells sitting there, so
            // the very next Create click would create a DUPLICATE of what was just saved instead of
            // being a no-op. Explicitly finishing the edit (same shape as NewMetatile) makes the
            // post-save state unambiguous instead of relying on whatever the binding happens to do.
            SelectedMetatile = null;
            DraftName = "metatile";
            ResetDraft();
            StatusText = $"Saved '{savedName}'.";
            IsStatusError = false;
            return;
        }

        var result = MetatileService.Create(_project, DraftName, SelectedKind, DraftGridSize, cells);
        if (!result.Success)
        {
            StatusText = result.Error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        RefreshMetatileList();
        ResetDraft();
        StatusText = $"Created '{result.Metatile!.Name}'.";
        IsStatusError = false;
    }

    /// <summary>Deselects whatever's loaded for editing and clears the draft, so the next Create starts a brand-new metatile instead of saving over the one that was selected.</summary>
    [RelayCommand]
    private void NewMetatile()
    {
        SelectedMetatile = null;
        DraftName = "metatile";
        ResetDraft();
    }

    /// <summary>
    /// Called by the View with whatever's currently selected in the "Existing metatiles" ListBox (native
    /// WPF multi-select — Ctrl/Shift+click; not a bound Command since SelectedItems isn't a bindable
    /// property). Exactly one selected uses <see cref="DeleteOneMetatile"/>'s detailed per-map confirmation;
    /// two or more use <see cref="DeleteMetatilesBatch"/>'s single "delete N?" confirmation instead.
    /// </summary>
    public void DeleteMetatiles(IReadOnlyList<Metatile> metatiles)
    {
        if (metatiles.Count == 0) return;
        if (metatiles.Count == 1)
        {
            DeleteOneMetatile(metatiles[0]);
            return;
        }
        DeleteMetatilesBatch(metatiles.ToList(), $"Delete {metatiles.Count} metatile(s)?", "Delete metatiles");
    }

    private void DeleteOneMetatile(Metatile metatile)
    {
        // The reserved blank metatile stays unconditionally undeletable — unrelated to the cascade below.
        if (metatile.IsReservedBlank)
        {
            StatusText = $"Cannot delete '{metatile.Name}': it's the reserved blank metatile every map's grid cells default to.";
            IsStatusError = true;
            return;
        }

        // GridSize=1 metatiles are thin 1:1 wrappers around a single tile (see ProjectState.MetatileGridSize's
        // own doc comment: "painting a GridSize=1 map is really painting tiles directly"). Deleting just the
        // wrapper here would leave the tile itself behind with no metatile to reference it — permanently
        // unpaintable on any map (the Map Editor's palette is driven by the metatile list, not the raw tile
        // list) while still occupying palette/bank space. So for GridSize=1, "delete this metatile" really
        // means "delete the underlying tile", routed through the SAME cascade the Project Tree's own tile
        // delete already uses (CascadeDeletionService), which already correctly removes this wrapper
        // metatile as part of that cascade. Falls through to the plain metatile-only delete below only for
        // the (never created by this app's own UI) case of a non-reserved 1x1 metatile whose cell happens
        // to reference the reserved blank tile — that tile itself must never become deletable.
        if (metatile.GridSize == 1)
        {
            var tileAsset = _project.Assets.FirstOrDefault(a => a.Id == metatile.Cells[0].TileAssetId);
            if (tileAsset is not null && !tileAsset.IsReservedBlank)
            {
                DeleteUnderlyingTile(tileAsset);
                return;
            }
        }

        // Being placed on a map is no longer a hard block — confirm the cascade instead (affected cells
        // become Blank, see MetatileService.DeleteCascading).
        var maps = ReferenceIntegrityService.FindMapsReferencingMetatile(_project, metatile);
        if (maps.Count > 0)
        {
            var detail = string.Join(", ", maps.Select(x => $"{x.Map.Name} ({x.CellCount} cell(s))"));
            var confirm = ScrollableMessageWindow.Show(
                $"'{metatile.Name}' is placed on map(s) {detail}. Those cells will become Blank. This cannot be undone.\n\nDelete anyway?",
                "Delete metatile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var deletedName = metatile.Name;
        MetatileService.DeleteCascading(_project, metatile);
        HasChanges = true;
        // Selecting a metatile in the library always loads it into the draft (see
        // OnSelectedMetatileChanged), so whatever's in the draft right now IS the thing just deleted —
        // leaving it there would let the next Create click recreate a copy of what was just removed.
        SelectedMetatile = null;
        DraftName = "metatile";
        ResetDraft();
        RefreshMetatileList();
        StatusText = $"Deleted '{deletedName}'.";
        IsStatusError = false;
    }

    /// <summary>
    /// GridSize=1's real delete target — see the comment where this is called from. Mirrors MainViewModel's
    /// own tile-delete flow (CascadeDeletionService plan/execute, RemoveAsset, palette compaction) but
    /// scoped to exactly one tile and without any Project Tree of its own to update — <see cref="DeletedTileAssetIds"/>
    /// carries the id back to MainWindow for that, once this dialog closes.
    /// </summary>
    private void DeleteUnderlyingTile(GraphicsAsset tileAsset)
    {
        var impact = CascadeDeletionService.PlanAssetDeletion(_project, [tileAsset]);

        var confirmLines = new List<string> { $"Delete '{tileAsset.Name}'?" };
        if (!impact.IsEmpty)
        {
            if (impact.AffectedMetatiles.Count > 0)
            {
                confirmLines.Add($"Also deletes {impact.AffectedMetatiles.Count} metatile(s) that use it: {string.Join(", ", impact.AffectedMetatiles.Select(m => m.Name))}.");
            }
            if (impact.AffectedMapCells.Count > 0)
            {
                var cellDetail = string.Join(", ", impact.AffectedMapCells.Select(x => $"{x.Map.Name} ({x.CellCount} cell(s))"));
                confirmLines.Add($"{impact.AffectedMapCells.Sum(x => x.CellCount)} map cell(s) will become Blank: {cellDetail}.");
            }
        }
        confirmLines.Add("This cannot be undone.");

        var confirm = ScrollableMessageWindow.Show(string.Join("\n", confirmLines), "Delete tile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        ExecuteTileDeletion(tileAsset, impact);
        SelectedMetatile = null;
        DraftName = "metatile";
        ResetDraft();
        RefreshMetatileList();
        RefreshTilePalette();
        StatusText = $"Deleted '{tileAsset.Name}'.";
        IsStatusError = false;
    }

    /// <summary>The actual mutation shared by <see cref="DeleteUnderlyingTile"/> (single item, its own confirmation) and <see cref="DeleteUnused"/> (a whole batch, confirmed once for the group) — cascade execute, RemoveAsset, palette compaction, and recording the id for MainWindow's Project Tree sync. No confirmation and no StatusText/list-refresh here — each caller owns those on its own terms.</summary>
    private void ExecuteTileDeletion(GraphicsAsset tileAsset, CascadeDeletionImpact impact)
    {
        if (!impact.IsEmpty) CascadeDeletionService.ExecuteAssetDeletion(_project, [tileAsset], impact);

        _project.RemoveAsset(tileAsset.Id);
        if (tileAsset.Category.UsesPaletteBank()) _project.CompactPaletteBank(tileAsset.Category);
        else _project.CompactFlatPalette(tileAsset.Category, tileAsset.FolderPath);

        DeletedTileAssetIds.Add(tileAsset.Id);
        HasChanges = true;
    }

    /// <summary>
    /// Deletes every metatile of the current <see cref="SelectedKind"/> that no map places — the reserved
    /// blank is never a candidate (it's never "used" by a map cell reference in the first place, but is
    /// excluded explicitly rather than relying on that). On a GridSize=1 project this deletes the
    /// underlying tile instead, same as a single "Delete selected" now does (see <see cref="DeleteMetatile"/>) —
    /// but the "unused" check there is stricter than a plain <see cref="ReferenceIntegrityService.FindMapsReferencingMetatile"/>
    /// call: it asks <see cref="CascadeDeletionService.PlanAssetDeletion"/> whether deleting the TILE would
    /// blank any real map cell, which also catches the (obscure, never producible via this app's own UI)
    /// case of a second 1x1 metatile manually created to wrap the same tile — if THAT sibling is placed on
    /// a map, the tile itself is not actually safe to delete even though the one metatile being considered
    /// here is. Deliberately checks <see cref="CascadeDeletionImpact.AffectedMapCells"/>, NOT
    /// <see cref="CascadeDeletionImpact.IsEmpty"/> — IsEmpty only looks at AffectedMetatiles/AffectedSpritePlacements,
    /// and AffectedMetatiles always includes the tile's own wrapper (itself), so IsEmpty is never true for
    /// any tile that has a metatile at all and would wrongly skip every single candidate. One confirmation
    /// for the whole batch, not one per metatile.
    /// </summary>
    [RelayCommand]
    private void DeleteUnused()
    {
        var toDelete = new List<Metatile>();
        foreach (var item in Metatiles)
        {
            var metatile = item.Metatile;
            if (metatile.IsReservedBlank) continue;

            if (metatile.GridSize == 1)
            {
                var tileAsset = _project.Assets.FirstOrDefault(a => a.Id == metatile.Cells[0].TileAssetId);
                if (tileAsset is null || tileAsset.IsReservedBlank) continue;
                if (CascadeDeletionService.PlanAssetDeletion(_project, [tileAsset]).AffectedMapCells.Count > 0) continue;
                toDelete.Add(metatile);
            }
            else if (ReferenceIntegrityService.FindMapsReferencingMetatile(_project, metatile).Count == 0)
            {
                toDelete.Add(metatile);
            }
        }

        if (toDelete.Count == 0)
        {
            StatusText = "No unused metatiles to delete.";
            IsStatusError = false;
            return;
        }

        DeleteMetatilesBatch(toDelete, $"Delete {toDelete.Count} unused metatile(s)?", "Delete unused metatiles");
    }

    /// <summary>
    /// Shared batch-delete core for <see cref="DeleteUnused"/> and <see cref="DeleteMetatile"/> (when one
    /// or more metatiles are Ctrl+click multi-selected) — one confirmation for the whole group, then the
    /// same GridSize=1-aware tile-vs-metatile routing <see cref="DeleteMetatile"/>'s single-item path uses.
    /// The confirmation aggregates map-cell impact across EVERY metatile in the batch into one combined
    /// per-map breakdown (same shape as <see cref="DeleteOneMetatile"/>'s own single-item detail, and as
    /// MainViewModel's own multi-asset delete confirmation) — for <see cref="DeleteUnused"/> specifically
    /// this is always empty by construction (nothing "unused" can have map impact), but a hand-picked
    /// multi-selection can very much include metatiles still placed on maps, and the caller has no way to
    /// know that without this. Re-checks each metatile is still actually present before deleting it, since
    /// deleting one GridSize=1 entry can cascade-remove a sibling metatile too (an obscure duplicate-wrapper
    /// edge case — see <see cref="DeleteUnused"/>'s own doc comment). Multi-selection flags need no explicit
    /// clearing — <see cref="RefreshMetatileList"/> rebuilds every item fresh (defaulting to unselected).
    /// </summary>
    private void DeleteMetatilesBatch(List<Metatile> metatiles, string headline, string confirmTitle)
    {
        var perMapCells = new Dictionary<MapAsset, int>();
        foreach (var metatile in metatiles)
        {
            if (metatile.IsReservedBlank) continue;

            if (metatile.GridSize == 1)
            {
                var tileAsset = _project.Assets.FirstOrDefault(a => a.Id == metatile.Cells[0].TileAssetId);
                if (tileAsset is null || tileAsset.IsReservedBlank) continue;
                foreach (var (map, count) in CascadeDeletionService.PlanAssetDeletion(_project, [tileAsset]).AffectedMapCells)
                {
                    perMapCells[map] = perMapCells.GetValueOrDefault(map) + count;
                }
            }
            else
            {
                foreach (var (map, count) in ReferenceIntegrityService.FindMapsReferencingMetatile(_project, metatile))
                {
                    perMapCells[map] = perMapCells.GetValueOrDefault(map) + count;
                }
            }
        }

        var confirmLines = new List<string> { headline };
        if (perMapCells.Count > 0)
        {
            var detail = string.Join(", ", perMapCells.Select(kv => $"{kv.Key.Name} ({kv.Value} cell(s))"));
            confirmLines.Add($"{perMapCells.Values.Sum()} map cell(s) will become Blank: {detail}.");
        }
        confirmLines.Add("This cannot be undone.");

        var confirm = ScrollableMessageWindow.Show(string.Join("\n", confirmLines), confirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var deletedCount = 0;
        foreach (var metatile in metatiles)
        {
            if (!_project.Metatiles.Contains(metatile) || metatile.IsReservedBlank) continue;

            if (metatile.GridSize == 1)
            {
                var tileAsset = _project.Assets.FirstOrDefault(a => a.Id == metatile.Cells[0].TileAssetId);
                if (tileAsset is null || tileAsset.IsReservedBlank) continue;
                ExecuteTileDeletion(tileAsset, CascadeDeletionService.PlanAssetDeletion(_project, [tileAsset]));
            }
            else
            {
                MetatileService.DeleteCascading(_project, metatile);
                HasChanges = true;
            }
            deletedCount++;
        }

        SelectedMetatile = null;
        DraftName = "metatile";
        ResetDraft();
        RefreshMetatileList();
        RefreshTilePalette();
        StatusText = $"Deleted {deletedCount} metatile(s).";
        IsStatusError = false;
    }
}
