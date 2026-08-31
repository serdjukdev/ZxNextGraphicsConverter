using System.Windows;
using System.Windows.Media;

namespace ZxNext.App.Views;

/// <summary>
/// Drop-in replacement for <see cref="MessageBox.Show(string, string, MessageBoxButton, MessageBoxImage)"/>
/// for confirmations whose message can grow arbitrarily long (a cascade delete's list of affected
/// tiles/metatiles/maps) — a native Win32 MessageBox has no scrollbar and grows to fit its whole content,
/// which for a few hundred affected items meant the dialog exceeded the screen with no way to read the
/// rest. This caps the message area at a fixed height and scrolls instead. Only supports the two
/// MessageBoxButton variants this app's cascade-confirmation call sites actually use (OK, YesNo) —
/// deliberately not a general MessageBox replacement for the whole app's short, fixed-text dialogs (those
/// stay native, e.g. "Unsaved changes" or "Optimize palette bank?").
/// </summary>
public partial class ScrollableMessageWindow : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public ScrollableMessageWindow(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;

        AccentBar.Background = icon switch
        {
            MessageBoxImage.Error => Brushes.Firebrick,
            MessageBoxImage.Warning => Brushes.DarkOrange,
            MessageBoxImage.Question => Brushes.SteelBlue,
            _ => Brushes.Gray
        };

        var isYesNo = buttons == MessageBoxButton.YesNo;
        YesButton.Visibility = isYesNo ? Visibility.Visible : Visibility.Collapsed;
        NoButton.Visibility = isYesNo ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Visibility = isYesNo ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Yes_OnClick(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.Yes;
        DialogResult = true;
    }

    private void No_OnClick(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.No;
        DialogResult = false;
    }

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.OK;
        DialogResult = true;
    }

    /// <summary>Same call shape as <see cref="MessageBox.Show(string, string, MessageBoxButton, MessageBoxImage)"/> — every existing cascade-confirmation call site swaps to this with no other changes needed.</summary>
    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        var window = new ScrollableMessageWindow(message, title, buttons, icon);
        window.ShowDialog();
        return window.Result;
    }
}
