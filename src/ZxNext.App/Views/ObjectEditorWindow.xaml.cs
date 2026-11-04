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

    /// <summary>And again for the two sideways strips, the animations and the frames of the selected animation. Four independent drags, so four sets of state and four formats: one can never drop into another.</summary>
    private const string AnimationDragFormat = "ZxNext.ObjectAnimationReorder";
    private Point _animationDragStart;
    private bool _animationDragArmed;
    private ObjectAnimation? _animationDragCandidate;
    private ListBoxItem? _animationDropIndicatorRow;

    private const string FrameDragFormat = "ZxNext.ObjectFrameReorder";
    private Point _frameDragStart;
    private bool _frameDragArmed;
    private ObjectFrame? _frameDragCandidate;
    private ListBoxItem? _frameDropIndicatorRow;

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


    // ---- animations and frames ----

    /// <summary>One more animation of this object. Same pattern as every other library list here: Add asks for a name, everything else is on the item's right-click menu.</summary>
    private void AddAnimation_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;

        var dialog = new RenameAssetWindow("", candidate => vm.IsAnimationNameTaken(candidate),
            "This object already has an animation with this name.", "Create")
        {
            Owner = this,
            Title = "New animation"
        };
        if (dialog.ShowDialog() == true) vm.CreateAnimation(dialog.NewName);
    }

    private void RenameAnimationMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (sender is not FrameworkElement { DataContext: ObjectAnimationRowViewModel row }) return;

        var dialog = new RenameAssetWindow(row.Animation.Name, candidate => vm.IsAnimationNameTaken(candidate, row.Animation),
            "This object already has an animation with this name.")
        {
            Owner = this,
            Title = "Rename animation"
        };
        if (dialog.ShowDialog() == true) vm.RenameAnimation(row.Animation, dialog.NewName);
    }

    /// <summary>Deleting an animation throws away its frames, and the label your own code uses for it stops being exported, so this confirms.</summary>
    private void DeleteAnimationMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (sender is not FrameworkElement { DataContext: ObjectAnimationRowViewModel row }) return;

        var confirm = MessageBox.Show(
            $"Delete the animation '{row.Animation.Name}' and its {row.Animation.Frames.Count} frame(s)?\n\n" +
            "Its table and its obj_ label disappear from object_anims.asm, so any code of yours that names it stops assembling.",
            "Delete animation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes) vm.DeleteAnimation(row.Animation);
    }

    private void DeleteFrameMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (sender is not FrameworkElement { DataContext: ObjectFrameRowViewModel row }) return;

        vm.DeleteFrameCommand.Execute(row);
    }

    private void AnimationList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _animationDragArmed = false;
        _animationDragCandidate = null;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ObjectAnimationRowViewModel row) return;

        _animationDragStart = e.GetPosition(null);
        _animationDragCandidate = row.Animation;
        _animationDragArmed = true;
    }

    private void AnimationList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_animationDragArmed || e.LeftButton != MouseButtonState.Pressed || _animationDragCandidate is not { } dragged) return;
        if (!PastDragThreshold(e.GetPosition(null), _animationDragStart)) return;

        _animationDragArmed = false;
        DragDrop.DoDragDrop(AnimationList, new DataObject(AnimationDragFormat, dragged), DragDropEffects.Move);
    }

    private void AnimationList_OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(AnimationDragFormat)) return;

        e.Effects = DragDropEffects.Move;
        SetSideDropIndicator(ref _animationDropIndicatorRow, RowUnder(e.OriginalSource));
        e.Handled = true;
    }

    private void AnimationList_OnDragLeave(object sender, DragEventArgs e) => SetSideDropIndicator(ref _animationDropIndicatorRow, null);

    private void AnimationList_OnDrop(object sender, DragEventArgs e)
    {
        SetSideDropIndicator(ref _animationDropIndicatorRow, null);
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (e.Data.GetData(AnimationDragFormat) is not ObjectAnimation dragged) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ObjectAnimationRowViewModel target) return;

        vm.ReorderAnimation(dragged, target.Animation);
    }

    private void FrameList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _frameDragArmed = false;
        _frameDragCandidate = null;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ObjectFrameRowViewModel row) return;

        _frameDragStart = e.GetPosition(null);
        _frameDragCandidate = row.Frame;
        _frameDragArmed = true;
    }

    private void FrameList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_frameDragArmed || e.LeftButton != MouseButtonState.Pressed || _frameDragCandidate is not { } dragged) return;
        if (!PastDragThreshold(e.GetPosition(null), _frameDragStart)) return;

        _frameDragArmed = false;
        DragDrop.DoDragDrop(FrameList, new DataObject(FrameDragFormat, dragged), DragDropEffects.Move);
    }

    private void FrameList_OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(FrameDragFormat)) return;

        e.Effects = DragDropEffects.Move;
        SetSideDropIndicator(ref _frameDropIndicatorRow, RowUnder(e.OriginalSource));
        e.Handled = true;
    }

    private void FrameList_OnDragLeave(object sender, DragEventArgs e) => SetSideDropIndicator(ref _frameDropIndicatorRow, null);

    /// <summary>Frame order IS play order, so a drop here changes the data, not just the view.</summary>
    private void FrameList_OnDrop(object sender, DragEventArgs e)
    {
        SetSideDropIndicator(ref _frameDropIndicatorRow, null);
        if (DataContext is not ObjectEditorViewModel vm) return;
        if (e.Data.GetData(FrameDragFormat) is not ObjectFrame dragged) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ObjectFrameRowViewModel target) return;

        vm.ReorderFrame(dragged, target.Frame);
    }

    private static bool PastDragThreshold(Point current, Point start) =>
        Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    /// <summary>The drop line for a SIDEWAYS strip: down the item's left edge, since the drop puts the dragged item in that item's place.</summary>
    private static void SetSideDropIndicator(ref ListBoxItem? current, ListBoxItem? row)
    {
        if (ReferenceEquals(current, row)) return;

        if (current is not null) current.BorderThickness = new Thickness(0);
        current = row;
        if (current is not null) current.BorderThickness = new Thickness(2, 0, 0, 0);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
