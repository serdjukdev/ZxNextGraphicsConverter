using System.Windows;

namespace ZxNext.App.Views;

public partial class ReQuantizeWindow : Window
{
    public ReQuantizeWindow()
    {
        InitializeComponent();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
