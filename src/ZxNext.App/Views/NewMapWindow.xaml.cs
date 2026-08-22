using System.Windows;

namespace ZxNext.App.Views;

public partial class NewMapWindow : Window
{
    public NewMapWindow()
    {
        InitializeComponent();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Create_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
