using System.Windows;

namespace ZxNext.App.Views;

public partial class NewSubfolderWindow : Window
{
    public string SubfolderName => NameBox.Text;

    public NewSubfolderWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Create_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) return;
        DialogResult = true;
    }
}
