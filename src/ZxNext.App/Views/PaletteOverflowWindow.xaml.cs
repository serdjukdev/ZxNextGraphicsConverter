using System.Windows;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class PaletteOverflowWindow : Window
{
    public OverflowChoice Choice { get; private set; } = OverflowChoice.Cancel;

    public PaletteOverflowWindow()
    {
        InitializeComponent();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = OverflowChoice.Cancel;
        DialogResult = false;
    }

    private void ReduceThisTile_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = OverflowChoice.ReduceThisTile;
        DialogResult = true;
    }

    private void ReQuantizeCategory_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = OverflowChoice.ReQuantizeCategory;
        DialogResult = true;
    }
}
