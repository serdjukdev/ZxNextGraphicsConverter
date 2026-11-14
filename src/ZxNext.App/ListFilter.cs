using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace ZxNext.App;

/// <summary>
/// Name filter for any list: set <c>local:ListFilter.Text</c> on an ItemsControl and only items whose
/// <c>Name</c> contains that text (ignoring case) stay visible. Re-applied when the list gets a new
/// ItemsSource. The selected item always stays visible, so filtering never silently drops a selection.
/// </summary>
public static class ListFilter
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(ListFilter), new PropertyMetadata("", OnTextChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked", typeof(bool), typeof(ListFilter), new PropertyMetadata(false));

    private static readonly DependencyPropertyDescriptor ItemsSourceDescriptor =
        DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ItemsControl));

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list) return;
        if (!(bool)list.GetValue(IsHookedProperty))
        {
            list.SetValue(IsHookedProperty, true);
            EventHandler onSourceChanged = (_, _) => Apply(list);
            ItemsSourceDescriptor.AddValueChanged(list, onSourceChanged);
            list.Unloaded += (_, _) => { ItemsSourceDescriptor.RemoveValueChanged(list, onSourceChanged); list.SetValue(IsHookedProperty, false); };
        }
        Apply(list);
    }

    /// <summary>In: the list. Sets its view's filter from the current text (none when empty).</summary>
    private static void Apply(ItemsControl list)
    {
        if (list.ItemsSource is null) return;
        var view = CollectionViewSource.GetDefaultView(list.ItemsSource);
        var text = GetText(list)?.Trim() ?? "";
        view.Filter = text.Length == 0
            ? null
            : item => Matches(item, text) || (list is Selector selector && ReferenceEquals(item, selector.SelectedItem));
    }

    /// <summary>In: a list item and the filter text. True if the item's Name contains the text, ignoring case.</summary>
    public static bool Matches(object? item, string text) =>
        item?.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item) is string name
        && name.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Project Tree row visibility while filtering. In: [0] the row's Name, [1] the filter text, [2] IsFolder.
/// Out: Visible for folders (they stay, so matches keep their place) and for leaves whose name contains the text.
/// </summary>
public class TreeFilterVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var name = values.Length > 0 ? values[0] as string : null;
        var text = (values.Length > 1 ? values[1] as string : null)?.Trim() ?? "";
        var isFolder = values.Length > 2 && values[2] is true;
        if (isFolder || text.Length == 0) return Visibility.Visible;
        return name is not null && name.Contains(text, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
