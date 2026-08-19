using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class PaletteStripView : UserControl
{
    public PaletteStripView()
    {
        InitializeComponent();
    }

    private void OptimizeBank_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is PaletteStripViewModel vm) vm.RequestOptimizeBank();
    }

    private void Swatch_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PaletteStripViewModel vm) return;
        if (sender is not FrameworkElement { DataContext: PaletteSwatchViewModel swatch }) return;
        if (!swatch.IsInteractive) return;

        if (e.ClickCount >= 2)
        {
            vm.RequestEditSwatch(swatch.Index);
        }
        else
        {
            vm.SelectSwatch(swatch.Index);
        }
    }
}
