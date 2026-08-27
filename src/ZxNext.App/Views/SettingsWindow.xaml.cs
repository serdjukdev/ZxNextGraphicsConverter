using System.Windows;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void SettingsWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var radio = vm.DefaultMetatileGridSize switch
        {
            1 => GridSize1,
            4 => GridSize4,
            _ => GridSize2
        };
        radio.IsChecked = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.DefaultMetatileGridSize = GridSize1.IsChecked == true ? 1 : GridSize4.IsChecked == true ? 4 : 2;
            vm.SaveToSettings();
        }

        DialogResult = true;
    }
}
