using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ZxNext.App.Views;

/// <summary>Name filter text box. Text: what to filter by (two-way). Escape clears it, and only closes the window when already empty.</summary>
public partial class FilterBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FilterBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FilterBox()
    {
        InitializeComponent();
    }

    private void Box_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || string.IsNullOrEmpty(Text)) return;
        Text = "";
        e.Handled = true;
    }
}
