using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.Core.Export;

namespace ZxNext.App.ViewModels;

public record MapGridSizeOption(int Value, string Label);

/// <summary>Backs the "New Map" dialog: name, starting Width/Height (changeable later via Resize/Trim — see ValidationText), and the permanent MetatileGridSize (fixed forever once created). Validates before creating, same "IsEnabled bound to IsValid" pattern as NewProjectWindow.</summary>
public partial class NewMapViewModel : ObservableObject
{
    public IReadOnlyList<MapGridSizeOption> AvailableGridSizes { get; } =
    [
        new(2, "2x2"), new(3, "3x3"), new(4, "4x4")
    ];

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

    [ObservableProperty]
    private int metatileGridSize = 2;

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
