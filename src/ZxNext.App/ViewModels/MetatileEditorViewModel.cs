using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.App.Rendering;
using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

public record MetatileKindOption(MetatileKind Kind, string Label);
public record MetatileGridSizeOption(int Value, string Label);

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

    public IReadOnlyList<MetatileGridSizeOption> AvailableGridSizes { get; } =
    [
        new(2, "2x2"), new(3, "3x3"), new(4, "4x4")
    ];

    /// <summary>Set true by a successful Create or Delete — read once by the caller (MainWindow) after the dialog closes, to decide whether to mark the project as having unsaved changes.</summary>
    public bool HasChanges { get; private set; }

    [ObservableProperty]
    private MetatileKind selectedKind = MetatileKind.FourBpp;

    [ObservableProperty]
    private int draftGridSize = 2;

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
    [NotifyCanExecuteChangedFor(nameof(DeleteMetatileCommand))]
    private MetatileListItemViewModel? selectedMetatile;

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
        RefreshTilePalette();
        RefreshMetatileList();
        ResetDraft();
    }

    partial void OnSelectedKindChanged(MetatileKind value)
    {
        SelectedPaletteTile = null;
        SelectedMetatile = null;
        RefreshTilePalette();
        RefreshMetatileList();
        ResetDraft();
    }

    partial void OnDraftGridSizeChanged(int value) => ResetDraft();

    partial void OnDraftNameChanged(string value) => UpdateDraftValidity();

    private void RefreshTilePalette()
    {
        var category = SelectedKind == MetatileKind.FourBpp ? AssetCategory.Tile4Bpp : AssetCategory.Tile8Bpp;
        TilePalette = new ObservableCollection<TilePaletteItemViewModel>(
            _project.Assets.Where(a => a.Category == category).OrderBy(a => a.SortIndex).Select(a => new TilePaletteItemViewModel(a, _project)));
    }

    private void RefreshMetatileList() =>
        Metatiles = new ObservableCollection<MetatileListItemViewModel>(
            _project.Metatiles.Where(m => m.Kind == SelectedKind).OrderBy(m => m.SortIndex).Select(m => new MetatileListItemViewModel(m, _project)));

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
            Cells = DraftCells.Select(c => new MetatileCell
            {
                TileAssetId = c.TileAsset!.Id,
                MirrorX = c.MirrorX,
                MirrorY = c.MirrorY,
                Rotate = c.Rotate,
                PaletteSlotOverride = c.PaletteSlotOverride
            }).ToList()
        };
        DraftPreview = TileGridBitmapRenderer.RenderMetatile(draft, _project);
    }

    [RelayCommand]
    private void AssignSelectedTileToCell(MetatileCellViewModel? cell)
    {
        if (cell is null || SelectedPaletteTile is null) return;
        cell.TileAsset = SelectedPaletteTile.Asset;
        cell.PaletteSlotOverride = null; // a newly-assigned tile always starts at its own native slot — an override left over from whatever tile occupied this cell before would very likely point at the wrong colours for the new one
    }

    [RelayCommand]
    private void CreateMetatile()
    {
        if (!IsDraftValid)
        {
            StatusText = "Assign a tile to every cell first.";
            IsStatusError = true;
            return;
        }

        var cells = DraftCells.Select(c => new MetatileCell
        {
            TileAssetId = c.TileAsset!.Id,
            MirrorX = c.MirrorX,
            MirrorY = c.MirrorY,
            Rotate = c.Rotate,
            PaletteSlotOverride = c.PaletteSlotOverride
        }).ToList();

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

    [RelayCommand(CanExecute = nameof(CanDeleteMetatile))]
    private void DeleteMetatile()
    {
        if (SelectedMetatile is null) return;

        var check = ReferenceIntegrityService.CanDeleteMetatile(_project, SelectedMetatile.Metatile);
        if (!check.CanDelete)
        {
            StatusText = check.BlockingReason;
            IsStatusError = true;
            return;
        }

        var deletedName = SelectedMetatile.Metatile.Name;
        MetatileService.Delete(_project, SelectedMetatile.Metatile);
        HasChanges = true;
        SelectedMetatile = null;
        RefreshMetatileList();
        StatusText = $"Deleted '{deletedName}'.";
        IsStatusError = false;
    }

    private bool CanDeleteMetatile() => SelectedMetatile is not null;
}
