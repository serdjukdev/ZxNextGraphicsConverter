using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.Core.Export;
using ZxNext.Core.Settings;

namespace ZxNext.App.ViewModels;

/// <summary>Backs the "New Map" dialog: name and starting Width/Height (changeable later via Resize/Trim — see ValidationText). Metatile GridSize is no longer chosen here — the whole project shares one fixed size, chosen once up front in the New Project dialog (see MapEditorWindow.NewMap_OnClick), passed in here only to compute the right cell-count limit (<see cref="MapExporter.MaxGridCellsFor"/> — a GridSize=1 map's Tilemap layer is 2 bytes/cell, so its effective limit is tighter). Validates before creating, same "IsEnabled bound to IsValid" pattern as NewProjectWindow.</summary>
public partial class NewMapViewModel(int gridSize) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationText))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string name = "map";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationText))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private int width = AppSettingsStore.Load().DefaultMapWidth ?? 32;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationText))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private int height = AppSettingsStore.Load().DefaultMapHeight ?? 24;

    public string ValidationText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return "Enter a name.";
            if (Width <= 0 || Height <= 0) return "Width and Height must be positive.";
            var cells = (long)Width * Height;
            var maxCells = MapExporter.MaxGridCellsFor(gridSize);
            if (cells > maxCells) return $"{Width}x{Height} = {cells} cells — over the {maxCells} limit{MapExporter.GridCellLimitReasonFor(gridSize)}.";
            return $"{cells} cells (limit {maxCells}{MapExporter.GridCellLimitReasonFor(gridSize)}).";
        }
    }

    public bool IsValid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name) || Width <= 0 || Height <= 0) return false;
            return (long)Width * Height <= MapExporter.MaxGridCellsFor(gridSize);
        }
    }

    [RelayCommand]
    private void SetPresetSize(string widthByHeight)
    {
        var parts = widthByHeight.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h)) return;
        Width = w;
        Height = h;
    }
}
