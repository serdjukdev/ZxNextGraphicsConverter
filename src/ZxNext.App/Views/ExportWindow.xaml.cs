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
    /// Marking it handled suppresses TWO things the DataGrid normally does on its own from that same
    /// click (Preview Handled=true blocks the paired Bubble event entirely), both restored explicitly
    /// below instead of left to the now-suppressed default path: (1) row selection — Grid.SelectedItem
    /// — and (2) moving REAL keyboard focus onto one of the row's CELLS specifically. Selection alone
    /// isn't enough, and focusing the ROW isn't enough either (tried first, still rendered grey) —
    /// DataGrid.IsSelectionActive (the thing that decides grey vs. blue) tracks whether a
    /// DataGridCell, specifically, has keyboard focus, not the row as a whole; that's also exactly
    /// why the row turned blue only once the Chunk size ComboBox (a real focusable control living
    /// inside one of that row's cells) was used instead.
    /// </summary>
    private void Grid_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var originalSource = e.OriginalSource as DependencyObject;
        if (FindAncestor<ComboBox>(originalSource) is not null) return; // let the chunk-size picker handle its own clicks

        if (FindAncestor<DataGridRow>(originalSource) is not { DataContext: ExportFolderRowViewModel row }) return;

        row.IsIncluded = !row.IsIncluded;
        Grid.SelectedItem = row;
        FindAncestor<DataGridCell>(originalSource)?.Focus();
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
