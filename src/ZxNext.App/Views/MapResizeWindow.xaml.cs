using System.Windows;

namespace ZxNext.App.Views;

public partial class MapResizeWindow : Window
{
    public MapResizeWindow()
    {
        InitializeComponent();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Apply_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
