using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>
/// One row of the Types tab: an object type with the EXPORT NUMBER it currently has, which is simply its
/// position in the list. The number is never stored (see <see cref="ObjectTypeService.MoveUp"/>), so
/// dragging a type changes it, and with it the init order of that type's objects in every map's exported
/// object list.
/// </summary>
public class ObjectTypeRowViewModel(ObjectType type, int exportNumber)
{
    public ObjectType Type { get; } = type;
    public int ExportNumber { get; } = exportNumber;
    public string Display => $"{ExportNumber}   {Type.Name}";
}

/// <summary>One row of the subtype list: the subtype plus the bit it owns and that bit's value.</summary>
public class ObjectSubtypeRowViewModel(ObjectSubtype subtype)
{
    public ObjectSubtype Subtype { get; } = subtype;
    public string Display => $"{Subtype.Name}   (bit {Subtype.BitIndex}, value {Subtype.BitValue})";
}

/// <summary>
/// The Types half of the Object Editor: object types and their subtypes, which used to live in a separate
/// "Manage Types..." dialog opened from the Map Editor. They moved here because this is where the user
/// already picks a type and a user byte, and a type is not map data: it is project-wide exported data (the
/// object record's type byte, the <c>obj_&lt;name&gt;</c> numbers, each map's per-type counts, and the
/// init order those numbers give).
/// </summary>
public partial class ObjectEditorViewModel
{
    public ObservableCollection<ObjectTypeRowViewModel> TypeRows { get; } = [];

    public ObservableCollection<ObjectSubtypeRowViewModel> SubtypeRows { get; } = [];

    /// <summary>False = the Objects tab (the default), true = the Types tab. Two lists in one window rather than a second window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsObjectsTabActive))]
    private bool isTypesTabActive;

    public bool IsObjectsTabActive => !IsTypesTabActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManagedTypeSelection))]
    [NotifyPropertyChangedFor(nameof(ManagedTypeHeader))]
    private ObjectTypeRowViewModel? selectedManagedType;

    [ObservableProperty]
    private ObjectSubtypeRowViewModel? selectedSubtypeRow;

    /// <summary>The selected type's own size defaults: what a placement with this type but NO object exports (an object of its own beats them, see MapExporter).</summary>
    [ObservableProperty]
    private int managedTypeWidth = ObjectType.DefaultSize;

    [ObservableProperty]
    private int managedTypeHeight = ObjectType.DefaultSize;

    /// <summary>Which bit of the user byte the selected subtype owns; writing it moves the subtype unless a sibling already owns that bit.</summary>
    [ObservableProperty]
    private int selectedSubtypeBit;

    public bool HasManagedTypeSelection => SelectedManagedType is not null;

    public string ManagedTypeHeader => SelectedManagedType is { } row
        ? $"'{row.Type.Name}' exports as obj_… equ {row.ExportNumber}"
        : "No type selected";

    public IReadOnlyList<int> BitChoices { get; } = Enumerable.Range(0, ObjectType.MaxSubtypes).ToList();

    [RelayCommand]
    private void ShowObjectsTab() => IsTypesTabActive = false;

    [RelayCommand]
    private void ShowTypesTab() => IsTypesTabActive = true;

    private void RefreshTypeRows()
    {
        var selectedId = SelectedManagedType?.Type.Id;
        TypeRows.Clear();
        for (var i = 0; i < _project.ObjectTypes.Count; i++)
        {
            TypeRows.Add(new ObjectTypeRowViewModel(_project.ObjectTypes[i], i));
        }

        SelectedManagedType = selectedId is { } id ? TypeRows.FirstOrDefault(r => r.Type.Id == id) : null;
        RefreshTypes();   // the Objects tab's own Type combo lists the same types
    }

    private void RefreshSubtypeRows()
    {
        var selectedId = SelectedSubtypeRow?.Subtype.Id;
        SubtypeRows.Clear();
        if (SelectedManagedType is { } row)
        {
            foreach (var subtype in row.Type.Subtypes.OrderBy(st => st.BitIndex))
            {
                SubtypeRows.Add(new ObjectSubtypeRowViewModel(subtype));
            }
        }

        SelectedSubtypeRow = selectedId is { } id ? SubtypeRows.FirstOrDefault(r => r.Subtype.Id == id) : null;
    }

    partial void OnSelectedManagedTypeChanged(ObjectTypeRowViewModel? value)
    {
        var wasLoading = _loadingSelection;
        _loadingSelection = true;
        ManagedTypeWidth = value?.Type.Width ?? ObjectType.DefaultSize;
        ManagedTypeHeight = value?.Type.Height ?? ObjectType.DefaultSize;
        _loadingSelection = wasLoading;

        RefreshSubtypeRows();
    }

    partial void OnSelectedSubtypeRowChanged(ObjectSubtypeRowViewModel? value)
    {
        var wasLoading = _loadingSelection;
        _loadingSelection = true;
        SelectedSubtypeBit = value?.Subtype.BitIndex ?? 0;
        _loadingSelection = wasLoading;
    }

    partial void OnManagedTypeWidthChanged(int value) => ApplyManagedTypeDefaults();

    partial void OnManagedTypeHeightChanged(int value) => ApplyManagedTypeDefaults();

    private void ApplyManagedTypeDefaults()
    {
        if (_loadingSelection || SelectedManagedType is not { } row) return;

        row.Type.Width = Math.Clamp(ManagedTypeWidth, 0, 255);
        row.Type.Height = Math.Clamp(ManagedTypeHeight, 0, 255);
        HasChanges = true;
    }

    /// <summary>Moving the selected subtype onto another bit; refused (and reverted in the picker) when a sibling subtype of the same type already owns it.</summary>
    partial void OnSelectedSubtypeBitChanged(int value)
    {
        if (_loadingSelection || SelectedManagedType is not { } typeRow || SelectedSubtypeRow is not { } row) return;
        if (row.Subtype.BitIndex == value) return;

        var (success, error) = ObjectTypeService.SetSubtypeBit(typeRow.Type, row.Subtype, value);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            var wasLoading = _loadingSelection;
            _loadingSelection = true;
            SelectedSubtypeBit = row.Subtype.BitIndex;
            _loadingSelection = wasLoading;
            return;
        }

        HasChanges = true;
        RefreshSubtypeRows();
        SyncUserByteControls();
        StatusText = $"'{row.Subtype.Name}' now owns bit {value} (value {row.Subtype.BitValue}).";
        IsStatusError = false;
    }

    /// <summary>True when another type already uses this name: the Add/Rename dialogs validate against it as the user types.</summary>
    public bool IsTypeNameTaken(string candidate, ObjectType? exclude = null) =>
        _project.ObjectTypes.Any(t => t != exclude && string.Equals(t.Name, candidate.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when a subtype name is free, the reason it isn't otherwise. Project-wide, since the exported label has no type in it.</summary>
    public string? SubtypeNameTakenReason(string candidate, ObjectSubtype? exclude = null) =>
        ObjectTypeService.IsSubtypeNameTaken(_project, candidate.Trim(), exclude?.Id);

    public void CreateType(string name)
    {
        var result = ObjectTypeService.Create(_project, name);
        if (!result.Success)
        {
            StatusText = result.Error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        RefreshTypeRows();
        SelectedManagedType = TypeRows.FirstOrDefault(r => r.Type.Id == result.Type!.Id);
        StatusText = $"Added type '{result.Type!.Name}', exported as number {SelectedManagedType?.ExportNumber ?? 0}.";
        IsStatusError = false;
    }

    public void RenameType(ObjectType type, string newName)
    {
        var (success, error) = ObjectTypeService.Rename(_project, type, newName);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        RefreshTypeRows();
        StatusText = $"Renamed type to '{type.Name}'.";
        IsStatusError = false;
    }

    /// <summary>Blocked while any object or placement still uses the type, by the same check the old dialog used.</summary>
    public void DeleteType(ObjectType type)
    {
        var check = ReferenceIntegrityService.CanDeleteObjectType(_project, type);
        if (!check.CanDelete)
        {
            StatusText = check.BlockingReason;
            IsStatusError = true;
            return;
        }

        var deletedName = type.Name;
        // An object that handed this type to its placements must not keep pointing at it.
        foreach (var definition in _project.ObjectDefinitions.Where(d => d.TypeId == type.Id)) definition.TypeId = null;

        ObjectTypeService.Delete(_project, type);
        HasChanges = true;
        SelectedManagedType = null;
        RefreshTypeRows();   // ...which also re-selects the object's own type in the Objects tab's combo
        StatusText = $"Deleted type '{deletedName}'.";
        IsStatusError = false;
    }

    /// <summary>Drag-and-drop in the type list: the dragged type takes the target's place, which also changes both of their exported numbers and the init order of their objects.</summary>
    public void ReorderType(ObjectType dragged, ObjectType target)
    {
        if (ReferenceEquals(dragged, target)) return;

        var from = _project.ObjectTypes.IndexOf(dragged);
        var to = _project.ObjectTypes.IndexOf(target);
        if (from < 0 || to < 0) return;

        _project.ObjectTypes.RemoveAt(from);
        _project.ObjectTypes.Insert(to, dragged);
        HasChanges = true;
        RefreshTypeRows();
        SelectedManagedType = TypeRows.FirstOrDefault(r => r.Type.Id == dragged.Id);
        StatusText = $"'{dragged.Name}' is now number {SelectedManagedType?.ExportNumber ?? 0}.";
        IsStatusError = false;
    }

    public void CreateSubtype(string name)
    {
        if (SelectedManagedType is not { } row) return;

        var (success, subtype, error) = ObjectTypeService.AddSubtype(_project, row.Type, name);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        RefreshSubtypeRows();
        SelectedSubtypeRow = SubtypeRows.FirstOrDefault(r => r.Subtype.Id == subtype!.Id);
        SyncUserByteControls();
        StatusText = $"Added subtype '{subtype!.Name}' on bit {subtype.BitIndex} (value {subtype.BitValue}).";
        IsStatusError = false;
    }

    public void RenameSubtype(ObjectSubtype subtype, string newName)
    {
        var (success, error) = ObjectTypeService.RenameSubtype(_project, subtype, newName);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        RefreshSubtypeRows();
        SyncUserByteControls();
        StatusText = $"Renamed subtype to '{subtype.Name}'.";
        IsStatusError = false;
    }

    /// <summary>Deleting a subtype deliberately leaves every placed object's user byte alone: that byte is raw game data, and clearing a bit across every map would be the bigger surprise.</summary>
    public void DeleteSubtype(ObjectSubtype subtype)
    {
        if (SelectedManagedType is not { } row) return;

        var deletedName = subtype.Name;
        ObjectTypeService.DeleteSubtype(row.Type, subtype);
        HasChanges = true;
        SelectedSubtypeRow = null;
        RefreshSubtypeRows();
        SyncUserByteControls();
        StatusText = $"Deleted subtype '{deletedName}'. Any user byte already set on a map is left as it is.";
        IsStatusError = false;
    }
}
