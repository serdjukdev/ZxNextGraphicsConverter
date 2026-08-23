using System.Windows;

namespace ZxNext.App.Views;

public partial class ObjectTypesWindow : Window
{
    public ObjectTypesWindow()
    {
        InitializeComponent();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
