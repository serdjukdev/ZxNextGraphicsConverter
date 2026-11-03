using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.App.Rendering;
using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Settings;

namespace ZxNext.App.ViewModels;

/// <summary>
/// One bit of an object's user byte, as a toggle in the editor. <see cref="SubtypeName"/> is the subtype
/// that owns this bit, when one does, so the eight buttons also show which bits are named and which are
/// free for a hand-entered parameter.
/// </summary>
public partial class UserByteBitViewModel : ObservableObject
{
    private readonly Action<int, bool> _onToggled;

    public int BitIndex { get; }
    public byte BitValue => (byte)(1 << BitIndex);
    public string Label => BitValue.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? subtypeName;

    [ObservableProperty]
    private bool isSet;

    public string ToolTipText => SubtypeName is null
        ? $"Bit {BitIndex} (value {BitValue}). No subtype owns it, so it is yours to use as a parameter."
        : $"Bit {BitIndex} (value {BitValue}) = subtype '{SubtypeName}'.";

    public UserByteBitViewModel(int bitIndex, Action<int, bool> onToggled)
    {
        BitIndex = bitIndex;
        _onToggled = onToggled;
    }

    partial void OnIsSetChanged(bool value) => _onToggled(BitIndex, value);
}

/// <summary>
/// One button in the sprite palette's folder strip: a sprite category's root folder, or one of its
/// sub-folders. Clicking it shows that folder's sprites, which is the only way a sub-folder sprite can be
/// used in an object (the palette would otherwise be every sprite in the project at once).
/// </summary>
public partial class SpriteFolderViewModel : ObservableObject
{
    public AssetCategory Category { get; }

    /// <summary>The folder's full path, exactly as an asset stores it.</summary>
    public string FolderPath { get; }

    public string Display { get; }

    [ObservableProperty]
    private bool isSelected;

    public SpriteFolderViewModel(AssetCategory category, string folderPath, string display)
    {
        Category = category;
        FolderPath = folderPath;
        Display = display;
    }
}

/// <summary>One cell of a row in the object editor: the sprite drawn there, or nothing.</summary>
public partial class ObjectCellViewModel : ObservableObject
{
    private readonly ProjectState _project;

    public int RowIndex { get; }
    public int Col { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private GraphicsAsset? sprite;

    /// <summary>Rendered once per repaint rather than on every binding read: a property getter that decodes pixels would re-run on every layout pass.</summary>
    [ObservableProperty]
    private WriteableBitmap? preview;

    partial void OnSpriteChanged(GraphicsAsset? value) =>
        Preview = value is null ? null : NextBitmapRenderer.Render(value, _project);

    public string ToolTipText => Sprite is null
        ? $"Row {RowIndex}, cell {Col}: empty. Pick a sprite below, then click here. Right-click clears a cell."
        : $"Row {RowIndex}, cell {Col}: {Sprite.Name}. Right-click clears it.";

    public ObjectCellViewModel(ProjectState project, int rowIndex, int col, GraphicsAsset? sprite)
    {
        _project = project;
        RowIndex = rowIndex;
        Col = col;
        this.sprite = sprite;
        preview = sprite is null ? null : NextBitmapRenderer.Render(sprite, project);
    }
}

/// <summary>
/// One row of the object editor: its cells, its own length (the +/- buttons) and its own pixel offset. The
/// row is also DRAWN shifted by that offset, so the editor's own layout shows the shape the finished
/// object has rather than a tidy rectangle that lies about it.
/// </summary>
public partial class ObjectRowViewModel : ObservableObject
{
    private readonly Action<ObjectRowViewModel> _onOffsetChanged;

    /// <summary>How many screen pixels one source pixel takes in the editor's layout: the cell button's size over the 16-pixel sprite it shows. Used to draw a row's offset at the same scale as its cells.</summary>
    public const double CellButtonSize = 56.0;
    private const double Scale = CellButtonSize / MapObjectDefinition.CellPixels;

    public int RowIndex { get; }

    public ObservableCollection<ObjectCellViewModel> Cells { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisualMargin))]
    private int offsetX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisualMargin))]
    private int offsetY;

    /// <summary>
    /// How far the WHOLE object has to be pushed right/down for no row to need a negative margin: the
    /// largest leftward/upward nudge any of its rows has, as a positive number. Set by the editor view
    /// model whenever a layout or an offset changes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisualMargin))]
    private int baseOffsetX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisualMargin))]
    private int baseOffsetY;

    /// <summary>
    /// The row's offset turned into a margin, so its cells sit where its sprites will. Measured from the
    /// object's leftmost/topmost edge (<see cref="BaseOffsetX"/>) rather than from the row's own grid
    /// position, so the value is NEVER negative: a negative margin would slide the cells on top of the
    /// row's own controls to their left, which is exactly what it did before this existed.
    /// </summary>
    public Thickness VisualMargin => new(Math.Max(0, OffsetX + BaseOffsetX) * Scale, Math.Max(0, OffsetY + BaseOffsetY) * Scale, 0, 0);

    public string Label => $"Row {RowIndex}";

    public ObjectRowViewModel(int rowIndex, Action<ObjectRowViewModel> onOffsetChanged)
    {
        RowIndex = rowIndex;
        _onOffsetChanged = onOffsetChanged;
    }

    partial void OnOffsetXChanged(int value) => _onOffsetChanged(this);

    partial void OnOffsetYChanged(int value) => _onOffsetChanged(this);
}

/// <summary>
/// Backs the Object Editor: the library of named objects, each built from ROWS of sprites (see
/// <see cref="MapObjectDefinition"/>). The point is that the Map Editor can then place a whole platform or
/// character as one unit and draw its real shape, instead of a lone sprite standing in for it.
///
/// <para>
/// Rows rather than a rectangle, because real compositions are not rectangular: a character is one sprite
/// over two, and each row carries its own length and its own offset (the two lower sprites centred under
/// the head sit at dx = -8). Everything else is deliberately like the Metatile Editor: a list of entries
/// on one side, a layout plus a sprite palette on the other, click a sprite then click a cell. Edits apply
/// to the selected object IMMEDIATELY: there is nothing here that has to change as a set.
/// </para>
/// </summary>
public partial class ObjectEditorViewModel : ObservableObject
{
    private readonly ProjectState _project;
    private readonly MapRenderCache _renderCache = new();
    private bool _loadingSelection;

    public ObservableCollection<MapObjectDefinition> Definitions { get; } = [];

    public ObservableCollection<ObjectRowViewModel> Rows { get; } = [];

    public ObservableCollection<TilePaletteItemViewModel> SpritePalette { get; } = [];

    /// <summary>The folder strip above the sprite palette: every sprite category root plus every sub-folder that actually holds sprites.</summary>
    public ObservableCollection<SpriteFolderViewModel> SpriteFolders { get; } = [];

    /// <summary>Types to choose from, plus a leading null entry for "no type" (the ComboBox shows it as an empty row).</summary>
    public ObservableCollection<ObjectType?> Types { get; } = [];

    /// <summary>
    /// The selected type's subtypes as a pickable list, with a leading null for "none". A SHORTCUT over
    /// <see cref="MapObjectDefinition.UserByte"/>, not a second place to store anything: picking one sets
    /// its bit and clears the other subtypes' bits, and the selection itself is read back out of the byte.
    /// </summary>
    public ObservableCollection<ObjectSubtype?> Subtypes { get; } = [];

    /// <summary>The eight bits of the user byte, edited right here rather than in a dialog: with a type whose bits are partly named and partly a parameter, both have to be visible at once.</summary>
    public ObservableCollection<UserByteBitViewModel> UserByteBits { get; } = [];

    public IReadOnlyList<ObjectSubtype> SubtypesOfSelectedType => SelectedType?.Subtypes ?? [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRowCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private MapObjectDefinition? selectedDefinition;

    [ObservableProperty]
    private TilePaletteItemViewModel? selectedPaletteSprite;

    [ObservableProperty]
    private SpriteFolderViewModel? selectedSpriteFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtypesOfSelectedType))]
    private ObjectType? selectedType;

    [ObservableProperty]
    private ObjectSubtype? selectedSubtype;

    /// <summary>The user byte as a plain number, two-way with the bit toggles and the subtype picker.</summary>
    [ObservableProperty]
    private int userByteNumber;

    /// <summary>The whole object drawn as one picture, offsets applied: the only honest preview of a composition whose pieces overlap.</summary>
    [ObservableProperty]
    private WriteableBitmap? compositePreview;

    /// <summary>Ticked (the default): the exported width/height is what the picture measures. Unticked: the two numbers are used instead, which is what a hitbox usually needs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualSize))]
    private bool autoSize = true;

    /// <summary>Drives the two size boxes' IsEnabled: they are only editable while the size is NOT taken from the picture.</summary>
    public bool ManualSize => !AutoSize;

    [ObservableProperty]
    private int sizePixelsWidth;

    [ObservableProperty]
    private int sizePixelsHeight;

    [ObservableProperty]
    private string? statusText;

    [ObservableProperty]
    private bool isStatusError;

    /// <summary>Read once by the caller after the dialog closes, same convention as the Map/Metatile Editors.</summary>
    public bool HasChanges { get; private set; }

    public bool HasSelection => SelectedDefinition is not null;

    public string SizeText
    {
        get
        {
            if (SelectedDefinition is not { } d) return "";

            var (_, _, measuredWidth, measuredHeight) = d.MeasureBounds();
            var measured = $"the picture measures {measuredWidth} x {measuredHeight} px";
            return d.WidthPixelsOverride is null && d.HeightPixelsOverride is null
                ? $"{measured}, and that is what is exported as its width/height"
                : $"{measured}, exported as {d.WidthPixels} x {d.HeightPixels} px (set by hand)";
        }
    }

    public string PaletteText => SelectedDefinition is { } d && ObjectDefinitionService.PaletteSlotOf(_project, d) is { } slot
        ? $"Palette slot {slot}, taken from the sprites themselves (exported as this object's palette byte)"
        : "";

    /// <summary>Shown when the object mixes 4bpp palette slots: one object is one palette on hardware, so the odd sprites out would render with the wrong colours.</summary>
    public string MixedPaletteWarning
    {
        get
        {
            if (SelectedDefinition is not { } d) return "";

            var mixed = ObjectDefinitionService.FindMixedPaletteSprites(_project, d);
            return mixed.Count == 0
                ? ""
                : $"These sprites are in a different palette slot and would render with the wrong colours: {string.Join(", ", mixed)}.";
        }
    }

    /// <summary>The names of any subtype bits set in the user byte, for the line next to the bit buttons.</summary>
    public string UserByteText
    {
        get
        {
            if (SelectedDefinition is not { } d) return "";

            var named = SubtypesOfSelectedType.Where(st => (d.UserByte & st.BitValue) != 0).Select(st => st.Name).ToList();
            return named.Count == 0 ? "" : $"= {string.Join(" + ", named)}";
        }
    }

    public ObjectEditorViewModel(ProjectState project)
    {
        _project = project;
        for (var bit = 0; bit < 8; bit++) UserByteBits.Add(new UserByteBitViewModel(bit, OnUserByteBitToggled));
        RefreshSpriteFolders();   // ...which selects the first folder and fills the palette from it
        RefreshTypeRows();        // ...which also fills the Objects tab's own Type combo
        RefreshDefinitions();
        SelectedDefinition = Definitions.FirstOrDefault();
    }

    private void RefreshDefinitions()
    {
        Definitions.Clear();
        foreach (var definition in _project.ObjectDefinitions) Definitions.Add(definition);
    }

    /// <summary>
    /// The folder strip: each sprite category's root, then each of its sub-folders that holds sprites. Every
    /// sprite in the project is reachable through it, sub-folder "banks" included, because an object is only
    /// a picture: unlike a map placement, nothing about it depends on a sprite having a global export index.
    /// </summary>
    private void RefreshSpriteFolders()
    {
        SpriteFolders.Clear();

        foreach (var category in new[] { AssetCategory.Sprite4Bpp, AssetCategory.Sprite8Bpp })
        {
            var root = category.ToFolderPath();
            if (!_project.Assets.Any(a => a.Category == category)) continue;

            SpriteFolders.Add(new SpriteFolderViewModel(category, root, category.ToString()));

            var subfolders = _project.Assets
                .Where(a => a.Category == category && a.FolderPath != root)
                .OrderBy(a => a.SortIndex)      // folders appear in the order their sprites do, not alphabetically
                .Select(a => a.FolderPath)
                .Distinct();

            foreach (var path in subfolders)
            {
                // Just the sub-folder's own name on the button: the category is already the button to its left.
                SpriteFolders.Add(new SpriteFolderViewModel(category, path, path.Split('/').Last()));
            }
        }

        SelectedSpriteFolder = SpriteFolders.FirstOrDefault();
    }

    /// <summary>
    /// Shows the selected folder's own sprites IN THEIR REAL ORDER, i.e. by SortIndex, the same order the
    /// Project Tree lists them in and the export numbers them in. Alphabetical would be a different order
    /// from the one the user arranged, which makes finding a sprite harder, not easier.
    /// </summary>
    private void RefreshSpritePalette()
    {
        SpritePalette.Clear();
        if (SelectedSpriteFolder is not { } folder) return;

        var candidates = _project.Assets
            .Where(a => a.Category == folder.Category && a.FolderPath == folder.FolderPath)
            .OrderBy(a => a.SortIndex);
        foreach (var asset in candidates)
        {
            SpritePalette.Add(new TilePaletteItemViewModel(asset, _project, _renderCache));
        }
    }

    [RelayCommand]
    private void SelectSpriteFolder(SpriteFolderViewModel? folder)
    {
        if (folder is not null) SelectedSpriteFolder = folder;
    }

    partial void OnSelectedSpriteFolderChanged(SpriteFolderViewModel? value)
    {
        foreach (var folder in SpriteFolders) folder.IsSelected = ReferenceEquals(folder, value);
        SelectedPaletteSprite = null;
        RefreshSpritePalette();
    }

    private void RefreshTypes()
    {
        Types.Clear();
        Types.Add(null); // "no type"
        foreach (var type in _project.ObjectTypes) Types.Add(type);
    }

    private void RefreshSubtypes()
    {
        Subtypes.Clear();
        Subtypes.Add(null); // the "none" row
        foreach (var subtype in SubtypesOfSelectedType.OrderBy(st => st.BitIndex)) Subtypes.Add(subtype);
        SyncSelectedSubtypeFromUserByte();
        SyncUserByteControls();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        if (SelectedDefinition is not { } d) return;

        for (var rowIndex = 0; rowIndex < d.Rows.Count; rowIndex++)
        {
            var row = d.Rows[rowIndex];
            var rowVm = new ObjectRowViewModel(rowIndex, OnRowOffsetChanged);
            for (var col = 0; col < row.Cells.Count; col++)
            {
                var sprite = row.Cells[col] is { } assetId ? _project.Assets.FirstOrDefault(a => a.Id == assetId) : null;
                rowVm.Cells.Add(new ObjectCellViewModel(_project, rowIndex, col, sprite));
            }

            // Set the offsets with the guard raised, so building the list is not mistaken for the user editing it.
            var wasLoading = _loadingSelection;
            _loadingSelection = true;
            rowVm.OffsetX = row.OffsetX;
            rowVm.OffsetY = row.OffsetY;
            _loadingSelection = wasLoading;

            Rows.Add(rowVm);
        }

        UpdateRowVisualBase();
    }

    /// <summary>
    /// Pushes every row's visual shift into positive territory: the object is drawn from its own leftmost
    /// (and topmost) edge, so a row nudged left just means every other row starts further right, instead of
    /// one row taking a negative margin and overlapping the controls beside it.
    /// </summary>
    private void UpdateRowVisualBase()
    {
        if (Rows.Count == 0) return;

        var baseX = Math.Max(0, -Rows.Min(r => r.OffsetX));
        var baseY = Math.Max(0, -Rows.Min(r => r.OffsetY));
        foreach (var row in Rows)
        {
            row.BaseOffsetX = baseX;
            row.BaseOffsetY = baseY;
        }
    }

    private void RefreshComposite() =>
        CompositePreview = SelectedDefinition is { } d ? TileGridBitmapRenderer.RenderObjectDefinition(d, _project) : null;

    /// <summary>Pushes the definition's exported size into the two boxes and the auto tick. Guarded like the user-byte controls, since writing those boxes is also how the user edits it.</summary>
    private void SyncSizeControls()
    {
        var wasLoading = _loadingSelection;
        _loadingSelection = true;
        AutoSize = SelectedDefinition is { WidthPixelsOverride: null, HeightPixelsOverride: null };
        SizePixelsWidth = SelectedDefinition?.WidthPixels ?? 0;
        SizePixelsHeight = SelectedDefinition?.HeightPixels ?? 0;
        _loadingSelection = wasLoading;
    }

    /// <summary>Pushes the definition's byte into the number box and the eight toggles. Guarded, since writing either of those is also how the user edits the byte.</summary>
    private void SyncUserByteControls()
    {
        var wasLoading = _loadingSelection;
        _loadingSelection = true;
        var value = SelectedDefinition?.UserByte ?? 0;
        UserByteNumber = value;
        foreach (var bit in UserByteBits)
        {
            bit.IsSet = (value & bit.BitValue) != 0;
            bit.SubtypeName = SubtypesOfSelectedType.FirstOrDefault(st => st.BitIndex == bit.BitIndex)?.Name;
        }
        _loadingSelection = wasLoading;
    }

    /// <summary>Which subtype the current user byte reads as: the one whose bit is set. With none or several set it shows "none", since several at once is not a subtype but hand-set bits.</summary>
    private void SyncSelectedSubtypeFromUserByte()
    {
        var wasLoading = _loadingSelection;
        _loadingSelection = true;
        var set = SelectedDefinition is { } d
            ? SubtypesOfSelectedType.Where(st => (d.UserByte & st.BitValue) != 0).ToList()
            : [];
        SelectedSubtype = set.Count == 1 ? set[0] : null;
        _loadingSelection = wasLoading;
    }

    partial void OnSelectedDefinitionChanged(MapObjectDefinition? value)
    {
        _loadingSelection = true;
        SelectedType = value?.TypeId is { } typeId ? _project.ObjectTypes.FirstOrDefault(t => t.Id == typeId) : null;
        RefreshSubtypes();
        _loadingSelection = false;

        RebuildRows();
        RefreshComposite();
        SyncSizeControls();
        NotifyDerived();
    }

    partial void OnSelectedTypeChanged(ObjectType? value)
    {
        RefreshSubtypes();
        NotifyDerived();
        if (_loadingSelection || SelectedDefinition is not { } d) return;

        d.TypeId = value?.Id;
        HasChanges = true;
    }

    /// <summary>Picking a subtype sets its own bit and clears the other subtypes' bits, leaving every other bit of the byte exactly as it was.</summary>
    partial void OnSelectedSubtypeChanged(ObjectSubtype? value)
    {
        if (_loadingSelection || SelectedDefinition is not { } d) return;

        var updated = d.UserByte;
        foreach (var subtype in SubtypesOfSelectedType) updated &= (byte)~subtype.BitValue;
        if (value is not null) updated |= value.BitValue;

        ApplyUserByte(updated);
    }

    private void OnUserByteBitToggled(int bitIndex, bool isSet)
    {
        if (_loadingSelection || SelectedDefinition is not { } d) return;

        ApplyUserByte(isSet ? (byte)(d.UserByte | (1 << bitIndex)) : (byte)(d.UserByte & ~(1 << bitIndex)));
    }

    partial void OnUserByteNumberChanged(int value)
    {
        if (_loadingSelection) return;

        var clamped = Math.Clamp(value, 0, 255);
        if (clamped != value)
        {
            UserByteNumber = clamped;
            return;
        }
        ApplyUserByte((byte)clamped);
    }

    public void ApplyUserByte(byte value)
    {
        if (SelectedDefinition is not { } d || d.UserByte == value) return;

        d.UserByte = value;
        HasChanges = true;
        SyncSelectedSubtypeFromUserByte();
        SyncUserByteControls();
        NotifyDerived();
        StatusText = $"'{d.Name}' user byte = {value}.";
        IsStatusError = false;
    }

    /// <summary>A row's offset changed in its own picker: store it (snapped), then redraw, since the row's cells and the composite both move.</summary>
    private void OnRowOffsetChanged(ObjectRowViewModel rowVm)
    {
        if (_loadingSelection || SelectedDefinition is not { } d) return;

        ObjectDefinitionService.SetRowOffset(d, rowVm.RowIndex, rowVm.OffsetX, rowVm.OffsetY);
        if (ObjectDefinitionService.RowAt(d, rowVm.RowIndex) is not { } stored) return;

        var wasLoading = _loadingSelection;
        _loadingSelection = true;
        rowVm.OffsetX = stored.OffsetX;   // the service clamps, so the box shows what was actually stored
        rowVm.OffsetY = stored.OffsetY;
        _loadingSelection = wasLoading;

        HasChanges = true;
        UpdateRowVisualBase();
        RefreshComposite();
        SyncSizeControls();
        NotifyDerived();
        StatusText = $"Row {rowVm.RowIndex} nudged {stored.OffsetX},{stored.OffsetY} px.";
        IsStatusError = false;
    }

    /// <summary>Drag-and-drop in the object list: the dragged object takes the target's place. Only the palette's order changes, so nothing else needs remapping.</summary>
    public void ReorderDefinition(MapObjectDefinition dragged, MapObjectDefinition target)
    {
        if (ReferenceEquals(dragged, target)) return;

        ObjectDefinitionService.Reorder(_project, dragged, target);
        HasChanges = true;
        RefreshDefinitions();
        SelectedDefinition = Definitions.FirstOrDefault(d => d.Id == dragged.Id);
        StatusText = $"Moved '{dragged.Name}'.";
        IsStatusError = false;
    }

    /// <summary>Called by the view once the Add dialog has collected a name: the window owns the dialog, the view model owns the project.</summary>
    public void CreateDefinition(string name)
    {
        var result = ObjectDefinitionService.Create(_project, name);
        if (!result.Success)
        {
            StatusText = result.Error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        RefreshDefinitions();
        SelectedDefinition = Definitions.First(d => d.Id == result.Definition!.Id);
        StatusText = $"Created '{result.Definition!.Name}'. Add rows and cells, then click a sprite and click a cell.";
        IsStatusError = false;
    }

    /// <summary>True when another object already uses this name: the Add/Rename dialogs validate against it as the user types.</summary>
    public bool IsNameTaken(string candidate, MapObjectDefinition? exclude = null) =>
        _project.ObjectDefinitions.Any(d => d != exclude && string.Equals(d.Name, candidate.Trim(), StringComparison.OrdinalIgnoreCase));

    public void RenameDefinition(MapObjectDefinition definition, string newName)
    {
        var d = definition;

        var (success, error) = ObjectDefinitionService.Rename(_project, d, newName);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        var renamedId = d.Id;
        RefreshDefinitions();
        SelectedDefinition = Definitions.First(x => x.Id == renamedId);
        StatusText = $"Renamed to '{d.Name}'.";
        IsStatusError = false;
    }

    /// <summary>Deleting an object leaves every placement of it on its map, drawing as the single sprite it carries: a placement owns its own exported data (see ObjectDefinitionService.Delete).</summary>
    public void DeleteDefinition(MapObjectDefinition definition)
    {
        var d = definition;
        var deletedName = d.Name;
        var placements = _project.Maps.Sum(m => m.SpriteLayer.Count(p => p.ObjectDefinitionId == d.Id));
        ObjectDefinitionService.Delete(_project, d);
        HasChanges = true;
        SelectedDefinition = null;
        RefreshDefinitions();
        SelectedDefinition = Definitions.FirstOrDefault();
        StatusText = placements == 0
            ? $"Deleted '{deletedName}'."
            : $"Deleted '{deletedName}'. {placements} object(s) already on maps stay where they are and keep their own sprite.";
        IsStatusError = false;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddRow()
    {
        if (SelectedDefinition is not { } d) return;
        Report(ObjectDefinitionService.AddRow(d), $"Row added ({d.Rows.Count} rows now).");
    }

    [RelayCommand]
    private void RemoveRow(ObjectRowViewModel? row)
    {
        if (row is null || SelectedDefinition is not { } d) return;
        Report(ObjectDefinitionService.RemoveRow(d, row.RowIndex), $"Row {row.RowIndex} removed.");
    }

    [RelayCommand]
    private void AddCell(ObjectRowViewModel? row)
    {
        if (row is null || SelectedDefinition is not { } d) return;
        var result = ObjectDefinitionService.AddCell(d, row.RowIndex);
        Report(result, result.Success ? $"Row {row.RowIndex} is now {d.Rows[row.RowIndex].Cells.Count} cells wide." : "");
    }

    [RelayCommand]
    private void RemoveCell(ObjectRowViewModel? row)
    {
        if (row is null || SelectedDefinition is not { } d) return;
        var result = ObjectDefinitionService.RemoveCell(d, row.RowIndex);
        Report(result, result.Success ? $"Row {row.RowIndex} is now {d.Rows[row.RowIndex].Cells.Count} cells wide." : "");
    }

    /// <summary>Shared tail of every layout change: refuse with the service's own message, or rebuild everything that depends on the layout.</summary>
    private void Report((bool Success, string? Error) result, string successText)
    {
        if (!result.Success)
        {
            StatusText = result.Error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        RebuildRows();
        RefreshComposite();
        SyncSizeControls();
        NotifyDerived();
        StatusText = successText;
        IsStatusError = false;
    }

    /// <summary>Paints the clicked cell with the selected palette sprite. The same sprite can fill as many cells as you like, which is how a platform repeats its centre piece.</summary>
    [RelayCommand]
    private void PaintCell(ObjectCellViewModel? cell)
    {
        if (cell is null || SelectedDefinition is not { } d) return;
        if (SelectedPaletteSprite is null)
        {
            StatusText = "Pick a sprite below first, then click a cell.";
            IsStatusError = true;
            return;
        }

        ObjectDefinitionService.SetCell(d, cell.RowIndex, cell.Col, SelectedPaletteSprite.Asset.Id);
        cell.Sprite = SelectedPaletteSprite.Asset;
        HasChanges = true;
        RefreshComposite();
        SyncSizeControls();
        NotifyDerived();
        StatusText = $"Row {cell.RowIndex}, cell {cell.Col} = {SelectedPaletteSprite.Asset.Name}.";
        IsStatusError = false;
    }

    [RelayCommand]
    private void ClearCell(ObjectCellViewModel? cell)
    {
        if (cell is null || SelectedDefinition is not { } d) return;

        ObjectDefinitionService.SetCell(d, cell.RowIndex, cell.Col, null);
        cell.Sprite = null;
        HasChanges = true;
        RefreshComposite();
        SyncSizeControls();
        NotifyDerived();
        StatusText = $"Row {cell.RowIndex}, cell {cell.Col} cleared.";
        IsStatusError = false;
    }

    partial void OnAutoSizeChanged(bool value)
    {
        if (_loadingSelection || SelectedDefinition is not { } d) return;

        if (value)
        {
            d.WidthPixelsOverride = null;
            d.HeightPixelsOverride = null;
        }
        else
        {
            // Pin whatever is showing, so unticking changes nothing until a number is actually edited.
            d.WidthPixelsOverride = SizePixelsWidth;
            d.HeightPixelsOverride = SizePixelsHeight;
        }
        HasChanges = true;
        SyncSizeControls();
        NotifyDerived();
    }

    partial void OnSizePixelsWidthChanged(int value) => ApplyManualSize();

    partial void OnSizePixelsHeightChanged(int value) => ApplyManualSize();

    private void ApplyManualSize()
    {
        if (_loadingSelection || AutoSize || SelectedDefinition is not { } d) return;

        d.WidthPixelsOverride = Math.Clamp(SizePixelsWidth, 0, 255);
        d.HeightPixelsOverride = Math.Clamp(SizePixelsHeight, 0, 255);
        HasChanges = true;
        NotifyDerived();
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(UserByteText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(PaletteText));
        OnPropertyChanged(nameof(MixedPaletteWarning));
    }
}
