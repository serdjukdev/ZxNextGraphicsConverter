using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.Core.Export;
using ZxNext.Core.Model;

namespace ZxNext.App.ViewModels;

/// <summary>Which corner/edge of the OLD content stays fixed relative to the new bounds — same "3x3 anchor" convention as Photoshop's canvas-resize dialog.</summary>
public enum ResizeAnchor
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, Center, MiddleRight,
    BottomLeft, BottomCenter, BottomRight
}

/// <summary>
/// Backs the "Resize Map" dialog: new Width/Height plus a 3x3 anchor picker, with a live drop-count
/// preview computed by calling the real <see cref="MapResizeCalculator.Plan"/> on every change (cheap —
/// maps are capped at <see cref="MapExporter.MaxGridCells"/> cells) rather than re-deriving the count some
/// other way, so the preview can never drift from what actually happens on Apply. Same "IsEnabled bound
/// to IsValid" pattern as NewMapViewModel/NewProjectViewModel.
/// </summary>
public partial class MapResizeViewModel : ObservableObject
{
    private readonly MapAsset _map;

    public int OldWidth => _map.Width;
    public int OldHeight => _map.Height;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationText))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyPropertyChangedFor(nameof(DropWarningText))]
    [NotifyPropertyChangedFor(nameof(HasDrops))]
    private int newWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationText))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyPropertyChangedFor(nameof(DropWarningText))]
    [NotifyPropertyChangedFor(nameof(HasDrops))]
    private int newHeight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DropWarningText))]
    [NotifyPropertyChangedFor(nameof(HasDrops))]
    private ResizeAnchor anchor = ResizeAnchor.TopLeft;

    public MapResizeViewModel(MapAsset map)
    {
        _map = map;
        newWidth = map.Width;
        newHeight = map.Height;
    }

    [RelayCommand]
    private void SetAnchor(string anchorName)
    {
        if (Enum.TryParse<ResizeAnchor>(anchorName, out var value)) Anchor = value;
    }

    public string ValidationText
    {
        get
        {
            if (NewWidth <= 0 || NewHeight <= 0) return "Width and Height must be positive.";
            var cells = (long)NewWidth * NewHeight;
            if (cells > MapExporter.MaxGridCells) return $"{NewWidth}x{NewHeight} = {cells} cells — over the {MapExporter.MaxGridCells} limit.";
            return $"{cells} cells (limit {MapExporter.MaxGridCells}).";
        }
    }

    public bool IsValid => NewWidth > 0 && NewHeight > 0 && (long)NewWidth * NewHeight <= MapExporter.MaxGridCells;

    /// <summary>Where the OLD (0,0) cell lands in the new grid, per the current anchor — the offsetX/offsetY <see cref="MapResizeCalculator.Plan"/> itself expects.</summary>
    private (int OffsetX, int OffsetY) ComputeOffset()
    {
        var offsetX = Anchor switch
        {
            ResizeAnchor.TopLeft or ResizeAnchor.MiddleLeft or ResizeAnchor.BottomLeft => 0,
            ResizeAnchor.TopRight or ResizeAnchor.MiddleRight or ResizeAnchor.BottomRight => NewWidth - OldWidth,
            _ => (NewWidth - OldWidth) / 2
        };
        var offsetY = Anchor switch
        {
            ResizeAnchor.TopLeft or ResizeAnchor.TopCenter or ResizeAnchor.TopRight => 0,
            ResizeAnchor.BottomLeft or ResizeAnchor.BottomCenter or ResizeAnchor.BottomRight => NewHeight - OldHeight,
            _ => (NewHeight - OldHeight) / 2
        };
        return (offsetX, offsetY);
    }

    public string DropWarningText
    {
        get
        {
            if (!IsValid) return string.Empty;
            var plan = BuildPlan()!;
            return TotalDropped(plan) == 0
                ? "No content will be dropped."
                : $"Will drop {plan.DroppedTileCount} tilemap cell(s), {plan.Dropped8BppCount} 8bpp cell(s), {plan.DroppedSpriteCount} sprite(s).";
        }
    }

    public bool HasDrops => IsValid && TotalDropped(BuildPlan()!) > 0;

    private static int TotalDropped(MapResizePlan plan) => plan.DroppedTileCount + plan.Dropped8BppCount + plan.DroppedSpriteCount;

    /// <summary>Null when the current Width/Height/anchor combination isn't valid — the caller (View) should not have let Apply be clickable in that state anyway.</summary>
    public MapResizePlan? BuildPlan()
    {
        if (!IsValid) return null;
        var (offsetX, offsetY) = ComputeOffset();
        return MapResizeCalculator.Plan(_map, NewWidth, NewHeight, offsetX, offsetY);
    }
}
