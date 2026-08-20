using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class ExportWindow : Window
{
    public ExportWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Toggles the clicked row's Export? checkbox regardless of where in the row was clicked — except
    /// inside the Chunk size column's ComboBox, which needs its own clicks to open/pick normally.
    /// Marks the event handled so the DataGrid's own default behavior (select the cell first, then a
    /// SECOND click to actually interact with it) never kicks in — one click always does it.
    /// </summary>
    private void Grid_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var originalSource = e.OriginalSource as DependencyObject;
        if (FindAncestor<ComboBox>(originalSource) is not null) return; // let the chunk-size picker handle its own clicks

        if (FindAncestor<DataGridRow>(originalSource) is not { DataContext: ExportFolderRowViewModel row }) return;

        row.IsIncluded = !row.IsIncluded;
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void Browse_OnClick(object sender, RoutedEventArgs e)
    {
        var currentDirectory = DataContext is ExportViewModel { OutputDirectory: var dir } ? dir : "";
        var dialog = new OpenFolderDialog { Title = "Choose export output folder", InitialDirectory = currentDirectory };
        if (dialog.ShowDialog() == true && DataContext is ExportViewModel vm)
        {
            vm.OutputDirectory = dialog.FolderName;
        }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Export_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
