using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZxNext.App.ViewModels;
using ZxNext.Core.Model;

namespace ZxNext.App.Views;

/// <summary>
/// The Object Editor: a library of named objects, each a grid of sprites the Map Editor can place as one
/// unit. Same interaction as the Metatile Editor (pick a sprite, click a cell), so only the two gestures
/// that need code-behind live here: a right-click clears a cell, and Close just closes.
/// </summary>
public partial class ObjectEditorWindow : Window
{
    /// <summary>Drag format for reordering an object within the list; carries the dragged MapObjectDefinition itself.</summary>
    private const string DragFormat = "ZxNext.ObjectDefinitionReorder";

    /// <summary>Armed on mouse-down over a row, so the drag only actually starts once the pointer clears the OS drag threshold: the same arming the Metatile Editor's gallery and the Project Tree use, so a plain click never turns into a drag.</summary>
    private Point _dragStart;
    private bool _dragArmed;
    private MapObjectDefinition? _dragCandidate;

    /// <summary>The row currently showing the green drop line, so it can be cleared again without walking the whole list.</summary>
    private ListBoxItem? _dropIndicatorRow;

    /// <summary>The same three fields again for the Types tab's own list: the two drags are independent, so they keep separate state and separate formats.</summary>
    private const string TypeDragFormat = "ZxNext.ObjectTypeReorder";
    private Point _typeDragStart;
    private bool _typeDragArmed;
    private ObjectType? _typeDragCandidate;
    private ListBoxItem? _typeDropIndicatorRow;

    public ObjectEditorWindow()
    {
        InitializeComponent();
    }

    private void DefinitionList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        _dragCandidate = null;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not MapObjectDefinition definition) return;

        _dragStart = e.GetPosition(null);
        _dragCandidate = definition;
        _dragArmed = true;
    }

    private void DefinitionList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not { } dragged) return;

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragArmed = false;
        DragDrop.DoDragDrop(DefinitionList, new DataObject(DragFormat, dragged), DragDropEffects.Move);
    }

    /// <summary>Shows where the dragged object would land: a green line along the top of the row under the pointer, since the drop puts it in that row's place.</summary>
    private void DefinitionList_OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat)) return;

        e.Effects = DragDropEffects.Move;
        SetDropIndicator(RowUnder(e.OriginalSource));
        e.Handled = true;
    }

    private void DefinitionList_OnDragLeave(object sender, DragEventArgs e) => SetDropIndicator(null);

    /// <summary>Dropping on a row moves the dragged object to that row's place. Dropping on empty space below the rows does nothing, rather than guessing an end position.</summary>
    private void DefinitionList_OnDrop(object sender, DragEventArgs e)
    {
        SetDropIndicator(null);
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (e.Data.GetData(DragFormat) is not MapObjectDefinition dragged) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not MapObjectDefinition target) return;

        vm.ReorderDefinition(dragged, target);
    }

    /// <summary>The ListBoxItem the pointer is over, found by walking up from whatever element the drag event landed on.</summary>
    private static ListBoxItem? RowUnder(object originalSource)
    {
        for (var element = originalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ListBoxItem row) return row;
        }
        return null;
    }

    /// <summary>One row at a time carries the line; the container's own BorderThickness is the line (see the ItemContainerStyle).</summary>
    private void SetDropIndicator(ListBoxItem? row)
    {
        if (ReferenceEquals(_dropIndicatorRow, row)) return;

        if (_dropIndicatorRow is not null) _dropIndicatorRow.BorderThickness = new Thickness(0);
        _dropIndicatorRow = row;
        if (_dropIndicatorRow is not null) _dropIndicatorRow.BorderThickness = new Thickness(0, 2, 0, 0);
    }

    private void Cell_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (sender is not FrameworkElement { DataContext: ObjectCellViewModel cell }) return;

        vm.ClearCellCommand.Execute(cell);
        e.Handled = true;
    }

    /// <summary>
    /// "Add..." asks for a name and nothing else, which is the pattern this project follows for a library
    /// list: one Add button, and every other action on the item's own right-click menu rather than a row of
    /// buttons under the list.
    /// </summary>
    private void Add_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;

        var dialog = new RenameAssetWindow("", candidate => vm.IsNameTaken(candidate), "Another object already uses this name.", "Create")
        {
            Owner = this,
            Title = "New object"
        };
        if (dialog.ShowDialog() == true) vm.CreateDefinition(dialog.NewName);
    }

    private void RenameMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (sender is not FrameworkElement { DataContext: MapObjectDefinition definition }) return;

        var dialog = new RenameAssetWindow(definition.Name, candidate => vm.IsNameTaken(candidate, definition), "Another object already uses this name.")
        {
            Owner = this,
            Title = "Rename object"
        };
        if (dialog.ShowDialog() == true) vm.RenameDefinition(definition, dialog.NewName);
    }

    /// <summary>Deleting an object never touches a map: every placement made from it keeps its own sprite and data, so this only confirms to catch a misclick.</summary>
    private void DeleteMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (sender is not FrameworkElement { DataContext: MapObjectDefinition definition }) return;

        var confirm = MessageBox.Show(
            $"Delete the object '{definition.Name}'?\n\nObjects already placed on maps stay where they are and keep their own sprite.",
            "Delete object", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes) vm.DeleteDefinition(definition);
    }

    // ---- Types tab ----

    private void AddType_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;

        var dialog = new RenameAssetWindow("", candidate => vm.IsTypeNameTaken(candidate), "Another object type already uses this name.", "Create")
        {
            Owner = this,
            Title = "New object type"
        };
        if (dialog.ShowDialog() == true) vm.CreateType(dialog.NewName);
    }

    private void RenameTypeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (sender is not FrameworkElement { DataContext: ObjectTypeRowViewModel row }) return;

        var dialog = new RenameAssetWindow(row.Type.Name, candidate => vm.IsTypeNameTaken(candidate, row.Type), "Another object type already uses this name.")
        {
            Owner = this,
            Title = "Rename object type"
        };
        if (dialog.ShowDialog() == true) vm.RenameType(row.Type, dialog.NewName);
    }

    /// <summary>Deleting a type is blocked while anything still uses it; the view model reports that in the status line rather than this dialog guessing.</summary>
    private void DeleteTypeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (sender is not FrameworkElement { DataContext: ObjectTypeRowViewModel row }) return;

        var confirm = MessageBox.Show(
            $"Delete the object type '{row.Type.Name}'?\n\nEvery later type's exported number moves down by one.",
            "Delete object type", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes) vm.DeleteType(row.Type);
    }

    private void AddSubtype_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;

        var dialog = new RenameAssetWindow("", candidate => vm.SubtypeNameTakenReason(candidate) is not null,
            "A subtype with this name already exists in this project.", "Create")
        {
            Owner = this,
            Title = "New subtype"
        };
        if (dialog.ShowDialog() == true) vm.CreateSubtype(dialog.NewName);
    }

    private void RenameSubtypeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (sender is not FrameworkElement { DataContext: ObjectSubtypeRowViewModel row }) return;

        var dialog = new RenameAssetWindow(row.Subtype.Name, candidate => vm.SubtypeNameTakenReason(candidate, row.Subtype) is not null,
            "A subtype with this name already exists in this project.")
        {
            Owner = this,
            Title = "Rename subtype"
        };
        if (dialog.ShowDialog() == true) vm.RenameSubtype(row.Subtype, dialog.NewName);
    }

    private void DeleteSubtypeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (sender is not FrameworkElement { DataContext: ObjectSubtypeRowViewModel row }) return;

        var confirm = MessageBox.Show(
            $"Delete the subtype '{row.Subtype.Name}'?\n\nUser bytes already set on maps are left exactly as they are.",
            "Delete subtype", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes) vm.DeleteSubtype(row.Subtype);
    }

    /// <summary>The type list reorders by drag, exactly like the object list: a type's position IS its exported number.</summary>
    private void TypeList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _typeDragArmed = false;
        _typeDragCandidate = null;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ObjectTypeRowViewModel row) return;

        _typeDragStart = e.GetPosition(null);
        _typeDragCandidate = row.Type;
        _typeDragArmed = true;
    }

    private void TypeList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_typeDragArmed || e.LeftButton != MouseButtonState.Pressed || _typeDragCandidate is not { } dragged) return;

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _typeDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _typeDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _typeDragArmed = false;
        DragDrop.DoDragDrop(TypeList, new DataObject(TypeDragFormat, dragged), DragDropEffects.Move);
    }

    private void TypeList_OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TypeDragFormat)) return;

        e.Effects = DragDropEffects.Move;
        SetTypeDropIndicator(RowUnder(e.OriginalSource));
        e.Handled = true;
    }

    private void TypeList_OnDragLeave(object sender, DragEventArgs e) => SetTypeDropIndicator(null);

    private void TypeList_OnDrop(object sender, DragEventArgs e)
    {
        SetTypeDropIndicator(null);
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (e.Data.GetData(TypeDragFormat) is not ObjectType dragged) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ObjectTypeRowViewModel target) return;

        vm.ReorderType(dragged, target.Type);
    }

    private void SetTypeDropIndicator(ListBoxItem? row)
    {
        if (ReferenceEquals(_typeDropIndicatorRow, row)) return;

        if (_typeDropIndicatorRow is not null) _typeDropIndicatorRow.BorderThickness = new Thickness(0);
        _typeDropIndicatorRow = row;
        if (_typeDropIndicatorRow is not null) _typeDropIndicatorRow.BorderThickness = new Thickness(0, 2, 0, 0);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
