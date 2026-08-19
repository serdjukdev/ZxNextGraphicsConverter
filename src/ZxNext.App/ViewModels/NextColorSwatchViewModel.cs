using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.Model;

namespace ZxNext.App.ViewModels;

public partial class NextColorSwatchViewModel(NextColor color, Brush brush, string tooltip) : ObservableObject
{
    public NextColor Color { get; } = color;
    public Brush Brush { get; } = brush;
    public string Tooltip { get; } = tooltip;

    [ObservableProperty]
    private bool isSelected;
}
