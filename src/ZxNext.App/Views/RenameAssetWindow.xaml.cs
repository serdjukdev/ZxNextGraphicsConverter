using System.Windows;

namespace ZxNext.App.Views;

public partial class RenameAssetWindow : Window
{
    private readonly Func<string, bool> _isNameTaken;
    private readonly string _nameTakenMessage;

    public string NewName => NameBox.Text.Trim();

    /// <summary><paramref name="isNameTaken"/> checks a candidate name against every OTHER asset in the project — names become ASM export labels and must be unique. <paramref name="nameTakenMessage"/> is the inline validation line shown when it returns true; the default wording suits this dialog's original caller (the Project Tree's tile/sprite rename), the Metatile Editor passes its own.</summary>
    public RenameAssetWindow(string currentName, Func<string, bool> isNameTaken, string? nameTakenMessage = null)
    {
        InitializeComponent();
        _isNameTaken = isNameTaken;
        _nameTakenMessage = nameTakenMessage ?? "Another tile/sprite already uses this name.";
        NameBox.Text = currentName;
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
        var name = NameBox.Text.Trim();
        string? error = null;
        if (string.IsNullOrWhiteSpace(name)) error = "Enter a name.";
        else if (_isNameTaken(name)) error = _nameTakenMessage;

        ValidationText.Text = error ?? "";
        ValidationText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        RenameButton.IsEnabled = error is null;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Rename_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || _isNameTaken(NameBox.Text.Trim())) return;
        DialogResult = true;
    }
}
