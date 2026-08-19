using System.Windows;

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
        DialogResult = true;
    }
}
