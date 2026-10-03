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
    private ObjectType? selectedType;

    [ObservableProperty]
    private string newTypeName = "";

    [ObservableProperty]
    private string renameText = "";

    [ObservableProperty]
    private string? statusText;

    [ObservableProperty]
    private bool isStatusError;

    public bool HasChanges { get; private set; }

    public ObjectTypesViewModel(ProjectState project)
    {
        _project = project;
        Refresh();
    }

    partial void OnSelectedTypeChanged(ObjectType? value) => RenameText = value?.Name ?? "";

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
}
