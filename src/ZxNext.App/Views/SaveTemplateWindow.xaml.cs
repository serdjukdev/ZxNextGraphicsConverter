using System.Windows;

namespace ZxNext.App.Views;

public partial class SaveTemplateWindow : Window
{
    public string TemplateName => NameBox.Text.Trim();

    public SaveTemplateWindow(string suggestedName)
    {
        InitializeComponent();
        NameBox.Text = suggestedName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        UpdateValidation();
    }

    private void NameBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateValidation();

    private void UpdateValidation()
    {
        var isEmpty = string.IsNullOrWhiteSpace(NameBox.Text);
        ValidationText.Text = isEmpty ? "Enter a name." : "";
        ValidationText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = !isEmpty;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) return;
        DialogResult = true;
    }
}
