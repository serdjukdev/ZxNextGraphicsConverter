using System.Windows;
using Microsoft.Win32;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class ExportWindow : Window
{
    public ExportWindow()
    {
        InitializeComponent();
    }

    private void Browse_OnClick(object sender, RoutedEventArgs e)
    {
        var currentDirectory = DataContext is ExportViewModel { OutputDirectory: var dir } ? dir : "";
        var dialog = new OpenFolderDialog { Title = "Choose export output folder", InitialDirectory = currentDirectory };
        if (dialog.ShowDialog() == true && DataContext is ExportViewModel vm)
        {
            vm.OutputDirectory = dialog.FolderName;
        }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Export_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
