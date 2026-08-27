using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.Core.Export;

namespace ZxNext.App.ViewModels;

/// <summary>Backs the "New Map" dialog: name and starting Width/Height (changeable later via Resize/Trim — see ValidationText). Metatile GridSize is no longer chosen here — the whole project shares one fixed size, chosen once up front in the New Project dialog (see MapEditorWindow.NewMap_OnClick). Validates before creating, same "IsEnabled bound to IsValid" pattern as NewProjectWindow.</summary>
public partial class NewMapViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationText))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string name = "map";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationText))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private int width = 32;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationText))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private int height = 24;

    public string ValidationText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return "Enter a name.";
            if (Width <= 0 || Height <= 0) return "Width and Height must be positive.";
            var cells = (long)Width * Height;
            if (cells > MapExporter.MaxGridCells) return $"{Width}x{Height} = {cells} cells — over the {MapExporter.MaxGridCells} limit.";
            return $"{cells} cells (limit {MapExporter.MaxGridCells}).";
        }
    }

    public bool IsValid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name) || Width <= 0 || Height <= 0) return false;
            return (long)Width * Height <= MapExporter.MaxGridCells;
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
