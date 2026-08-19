using System.Windows;
using System.Windows.Input;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class NextColorPickerWindow : Window
{
    public NextColorPickerWindow()
    {
        InitializeComponent();
    }

    private void Swatch_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NextColorSwatchViewModel swatch }) return;
        if (DataContext is NextColorPickerViewModel vm) vm.SelectColor(swatch.Color);
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
