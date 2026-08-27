using System.Windows;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class NewProjectWindow : Window
{
    public NewProjectWindow()
    {
        InitializeComponent();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Create_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is NewProjectViewModel { ShowMetatileGridSizeOption: true } vm)
        {
            vm.MetatileGridSize = Size4.IsChecked == true ? 4 : 2;
        }

        DialogResult = true;
    }
}
