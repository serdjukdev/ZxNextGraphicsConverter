using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>
/// Backs the modal "Object Types" management dialog, opened from the Map Editor's Sprites layer (types
/// only ever matter there — see <see cref="MapEditorViewModel"/>). Add/rename/delete apply directly to
/// <see cref="ProjectState.ObjectTypes"/> immediately, same as the Map/Metatile Editors' own list
/// management (no separate OK/Cancel — <see cref="HasChanges"/> is read once by the caller after the
/// dialog closes, same convention as <see cref="MapEditorViewModel.HasChanges"/>).
/// </summary>
public partial class ObjectTypesViewModel : ObservableObject
{
    private readonly ProjectState _project;

    public ObservableCollection<ObjectType> Types { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameTypeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteTypeCommand))]
    // Without this the Add-subtype button would never re-evaluate its CanExecute and would sit disabled
    // forever: a generated RelayCommand only re-checks when something explicitly tells it to.
    [NotifyCanExecuteChangedFor(nameof(AddSubtypeCommand))]
    private ObjectType? selectedType;

    [ObservableProperty]
    private string newTypeName = "";

    [ObservableProperty]
    private string renameText = "";

    /// <summary>The selected type's subtypes, wrapped so the list can show the bit each one owns.</summary>
    public ObservableCollection<ObjectSubtypeRow> Subtypes { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameSubtypeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSubtypeCommand))]
    private ObjectSubtypeRow? selectedSubtype;

    [ObservableProperty]
    private string subtypeName = "";

    /// <summary>Which bit of the user byte the selected subtype owns. Writing it moves the subtype, unless a sibling already owns that bit.</summary>
    [ObservableProperty]
    private int selectedSubtypeBit;

    /// <summary>0-7, for the bit picker.</summary>
    public IReadOnlyList<int> BitChoices { get; } = Enumerable.Range(0, ObjectType.MaxSubtypes).ToList();

    /// <summary>
    /// The selected type's palette number, width and height, mirrored into the view model rather than
    /// bound straight at the model: writing them has to mark the project dirty, and a plain binding to a
    /// non-observable <see cref="ObjectType"/> could not. Guarded by <see cref="_loadingSelection"/> so
    /// merely selecting another type in the list doesn't count as an edit.
    /// </summary>
    [ObservableProperty]
    private int selectedPaletteIndex;

    [ObservableProperty]
    private int selectedWidth = ObjectType.DefaultSize;

    [ObservableProperty]
    private int selectedHeight = ObjectType.DefaultSize;

    private bool _loadingSelection;

    [ObservableProperty]
    private string? statusText;

    [ObservableProperty]
    private bool isStatusError;

    public bool HasChanges { get; private set; }

    /// <summary>Drives the per-type object-data fields' IsEnabled: they describe whichever type is selected, so with nothing selected there is nothing to edit.</summary>
    public bool HasSelectedType => SelectedType is not null;

    public ObjectTypesViewModel(ProjectState project)
    {
        _project = project;
        Refresh();
    }

    partial void OnSelectedTypeChanged(ObjectType? value)
    {
        RenameText = value?.Name ?? "";
        OnPropertyChanged(nameof(HasSelectedType));
        RefreshSubtypes();

        _loadingSelection = true;
        SelectedPaletteIndex = value?.PaletteIndex ?? 0;
        SelectedWidth = value?.Width ?? ObjectType.DefaultSize;
        SelectedHeight = value?.Height ?? ObjectType.DefaultSize;
        _loadingSelection = false;
    }

    partial void OnSelectedPaletteIndexChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 15);
        if (clamped != value)
        {
            SelectedPaletteIndex = clamped;
            return;
        }
        if (_loadingSelection || SelectedType is null || SelectedType.PaletteIndex == clamped) return;

        SelectedType.PaletteIndex = clamped;
        HasChanges = true;
    }

    partial void OnSelectedWidthChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 255);
        if (clamped != value)
        {
            SelectedWidth = clamped;
            return;
        }
        if (_loadingSelection || SelectedType is null || SelectedType.Width == clamped) return;

        SelectedType.Width = clamped;
        HasChanges = true;
    }

    partial void OnSelectedHeightChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 255);
        if (clamped != value)
        {
            SelectedHeight = clamped;
            return;
        }
        if (_loadingSelection || SelectedType is null || SelectedType.Height == clamped) return;

        SelectedType.Height = clamped;
        HasChanges = true;
    }

    private void Refresh()
    {
        Types.Clear();
        foreach (var type in _project.ObjectTypes) Types.Add(type);
    }

    [RelayCommand]
    private void AddType()
    {
        var result = ObjectTypeService.Create(_project, NewTypeName);
        if (!result.Success)
        {
            StatusText = result.Error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        Refresh();
        SelectedType = Types.First(t => t.Id == result.Type!.Id);
        NewTypeName = "";
        StatusText = $"Added '{result.Type!.Name}'.";
        IsStatusError = false;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RenameType()
    {
        if (SelectedType is null) return;

        var (success, error) = ObjectTypeService.Rename(_project, SelectedType, RenameText);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        Refresh();
        SelectedType = Types.First(t => t.Id == SelectedType.Id);
        StatusText = $"Renamed to '{SelectedType.Name}'.";
        IsStatusError = false;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteType()
    {
        if (SelectedType is null) return;

        var check = ReferenceIntegrityService.CanDeleteObjectType(_project, SelectedType);
        if (!check.CanDelete)
        {
            StatusText = check.BlockingReason;
            IsStatusError = true;
            return;
        }

        var deletedName = SelectedType.Name;
        ObjectTypeService.Delete(_project, SelectedType);
        HasChanges = true;
        SelectedType = null;
        Refresh();
        StatusText = $"Deleted '{deletedName}'.";
        IsStatusError = false;
    }

    /// <summary>List order here is also the export order: object_types.asm's `equ` numbering, and the init-priority sort every map's _objects.asm now applies (see MapExporter.BuildObjectRecords). Silently no-ops at the boundary, same shape as MapEditorViewModel.MoveLayerUp/Down: no separate CanExecute wiring needed.</summary>
    [RelayCommand]
    private void MoveTypeUp()
    {
        if (SelectedType is null) return;
        ObjectTypeService.MoveUp(_project, SelectedType);
        HasChanges = true;
        var moved = SelectedType;
        Refresh();
        SelectedType = Types.First(t => t.Id == moved.Id);
    }

    /// <summary>Same shape as <see cref="MoveTypeUp"/>, one position later instead.</summary>
    [RelayCommand]
    private void MoveTypeDown()
    {
        if (SelectedType is null) return;
        ObjectTypeService.MoveDown(_project, SelectedType);
        HasChanges = true;
        var moved = SelectedType;
        Refresh();
        SelectedType = Types.First(t => t.Id == moved.Id);
    }

    private bool HasSelection() => SelectedType is not null;

    private bool HasSubtypeSelection() => SelectedSubtype is not null;

    private void RefreshSubtypes()
    {
        Subtypes.Clear();
        if (SelectedType is null) return;
        foreach (var subtype in SelectedType.Subtypes.OrderBy(st => st.BitIndex))
        {
            Subtypes.Add(new ObjectSubtypeRow(subtype));
        }
    }

    partial void OnSelectedSubtypeChanged(ObjectSubtypeRow? value)
    {
        SubtypeName = value?.Model.Name ?? "";
        _loadingSelection = true;
        SelectedSubtypeBit = value?.Model.BitIndex ?? 0;
        _loadingSelection = false;
    }

    /// <summary>Moving the selected subtype onto another bit. Refused (and reverted in the picker) when a sibling subtype of the same type already owns it.</summary>
    partial void OnSelectedSubtypeBitChanged(int value)
    {
        if (_loadingSelection || SelectedType is null || SelectedSubtype is not { } row) return;
        if (row.Model.BitIndex == value) return;

        var (success, error) = ObjectTypeService.SetSubtypeBit(SelectedType, row.Model, value);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            _loadingSelection = true;
            SelectedSubtypeBit = row.Model.BitIndex;
            _loadingSelection = false;
            return;
        }

        HasChanges = true;
        var movedId = row.Model.Id;
        RefreshSubtypes();
        SelectedSubtype = Subtypes.FirstOrDefault(r => r.Model.Id == movedId);
        StatusText = $"'{row.Model.Name}' now owns bit {value} (value {row.Model.BitValue}).";
        IsStatusError = false;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddSubtype()
    {
        if (SelectedType is null) return;

        var (success, subtype, error) = ObjectTypeService.AddSubtype(_project, SelectedType, SubtypeName);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        RefreshSubtypes();
        SelectedSubtype = Subtypes.FirstOrDefault(r => r.Model.Id == subtype!.Id);
        SubtypeName = "";
        StatusText = $"Added subtype '{subtype!.Name}' on bit {subtype.BitIndex} (value {subtype.BitValue}).";
        IsStatusError = false;
    }

    [RelayCommand(CanExecute = nameof(HasSubtypeSelection))]
    private void RenameSubtype()
    {
        if (SelectedSubtype is not { } row) return;

        var (success, error) = ObjectTypeService.RenameSubtype(_project, row.Model, SubtypeName);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        var renamedId = row.Model.Id;
        RefreshSubtypes();
        SelectedSubtype = Subtypes.FirstOrDefault(r => r.Model.Id == renamedId);
        StatusText = $"Renamed subtype to '{row.Model.Name}'.";
        IsStatusError = false;
    }

    /// <summary>
    /// Deletes the selected subtype. Deliberately leaves every placed object's user byte alone: that byte
    /// is raw game data the user may also be setting by hand, so clearing a bit across every map would be
    /// a worse surprise than leaving it set (see ObjectTypeService.DeleteSubtype).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSubtypeSelection))]
    private void DeleteSubtype()
    {
        if (SelectedType is null || SelectedSubtype is not { } row) return;

        var deletedName = row.Model.Name;
        ObjectTypeService.DeleteSubtype(SelectedType, row.Model);
        HasChanges = true;
        SelectedSubtype = null;
        RefreshSubtypes();
        StatusText = $"Deleted subtype '{deletedName}'. Any user byte already set on a map is left as it is.";
        IsStatusError = false;
    }
}

/// <summary>One row of the subtype list: the model plus the "name (bit N, value M)" text the list shows.</summary>
public class ObjectSubtypeRow(ObjectSubtype model)
{
    public ObjectSubtype Model { get; } = model;

    public string Display => $"{Model.Name}  (bit {Model.BitIndex}, value {Model.BitValue})";
}
