using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.App.Rendering;
using ZxNext.Core.Conversion;
using ZxNext.Core.Model;

namespace ZxNext.App.ViewModels;

/// <summary>One row of the animation list: the animation and how many frames it holds.</summary>
public class ObjectAnimationRowViewModel(ObjectAnimation animation)
{
    public ObjectAnimation Animation { get; } = animation;
    public string Display => $"{Animation.Name}   ({Animation.Frames.Count} frame{(Animation.Frames.Count == 1 ? "" : "s")})";
}

/// <summary>
/// One thumbnail in the frame strip: the frame's own picture plus its number and duration. Flipping through
/// these is the preview: the user said playback is not needed, seeing each frame is.
/// </summary>
public partial class ObjectFrameRowViewModel : ObservableObject
{
    public ObjectFrame Frame { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private int index;

    [ObservableProperty]
    private WriteableBitmap? preview;

    public string Display => $"{Index}";

    public string ToolTipText => $"Frame {Index}, held {Frame.DurationFrames} game frame(s). Click to edit it, drag to reorder, right-click to delete.";

    public ObjectFrameRowViewModel(ObjectFrame frame, int index, WriteableBitmap? preview)
    {
        Frame = frame;
        this.index = index;
        this.preview = preview;
    }

    /// <summary>Called when the duration box changes, so the tooltip stops lying about it.</summary>
    public void NotifyDurationChanged() => OnPropertyChanged(nameof(ToolTipText));
}

/// <summary>
/// The animation and frame half of the Object Editor. An object holds named animations, each a list of
/// frames; a FRAME is the row layout the editor has always had, plus a duration. So the row editor below
/// always edits <see cref="CurrentFrame"/>, and everything that used to read the object's rows now reads
/// the selected frame's.
///
/// <para>
/// The export turns each animation into one self-contained table for his engine's draw_unified (see
/// ObjectAnimationExporter). A static object is simply a one-frame animation, so there is no special case
/// anywhere for "an object without animation".
/// </para>
/// </summary>
public partial class ObjectEditorViewModel
{
    public ObservableCollection<ObjectAnimationRowViewModel> Animations { get; } = [];

    public ObservableCollection<ObjectFrameRowViewModel> Frames { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnimationSelection))]
    [NotifyCanExecuteChangedFor(nameof(AddFrameCommand))]
    private ObjectAnimationRowViewModel? selectedAnimation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrameSelection))]
    [NotifyPropertyChangedFor(nameof(FrameHeader))]
    private ObjectFrameRowViewModel? selectedFrame;

    /// <summary>The selected frame's duration in game frames, which is the only number of an animation table that is typed rather than measured.</summary>
    [ObservableProperty]
    private int frameDuration;

    public bool HasAnimationSelection => SelectedAnimation is not null;

    public bool HasFrameSelection => SelectedFrame is not null;

    public string FrameHeader => SelectedFrame is { } frame
        ? $"Frame {frame.Index} of {SelectedAnimation?.Animation.Name}"
        : "No frame";

    /// <summary>The frame every row/cell edit applies to. Null only while nothing is selected at all.</summary>
    public ObjectFrame? CurrentFrame => SelectedFrame?.Frame ?? SelectedAnimation?.Animation.Frames.FirstOrDefault();

    private void RefreshAnimations()
    {
        var selectedId = SelectedAnimation?.Animation.Id;
        Animations.Clear();
        if (SelectedDefinition is { } definition)
        {
            foreach (var animation in definition.Animations) Animations.Add(new ObjectAnimationRowViewModel(animation));
        }

        SelectedAnimation = (selectedId is { } id ? Animations.FirstOrDefault(r => r.Animation.Id == id) : null)
                            ?? Animations.FirstOrDefault();
    }

    private void RefreshFrames()
    {
        var selectedFrameObject = SelectedFrame?.Frame;
        Frames.Clear();
        if (SelectedAnimation is { } animationRow)
        {
            for (var i = 0; i < animationRow.Animation.Frames.Count; i++)
            {
                var frame = animationRow.Animation.Frames[i];
                Frames.Add(new ObjectFrameRowViewModel(frame, i, TileGridBitmapRenderer.RenderObjectFrame(frame, _project)));
            }
        }

        SelectedFrame = (selectedFrameObject is not null ? Frames.FirstOrDefault(r => ReferenceEquals(r.Frame, selectedFrameObject)) : null)
                        ?? Frames.FirstOrDefault();
    }

    partial void OnSelectedAnimationChanged(ObjectAnimationRowViewModel? value)
    {
        RefreshFrames();
        // The frame selection drives the row editor, so everything below has to follow it.
        RebuildRows();
        UpdateRowVisualBase();
        RefreshComposite();
        SyncSizeControls();
        NotifyDerived();
    }

    partial void OnSelectedFrameChanged(ObjectFrameRowViewModel? value)
    {
        var wasLoading = _loadingSelection;
        _loadingSelection = true;
        FrameDuration = value?.Frame.DurationFrames ?? 0;
        _loadingSelection = wasLoading;

        RebuildRows();
        UpdateRowVisualBase();
        RefreshComposite();
        SyncSizeControls();
        NotifyDerived();
    }

    partial void OnFrameDurationChanged(int value)
    {
        if (_loadingSelection || SelectedFrame is not { } row) return;

        var clamped = Math.Clamp(value, 0, 255);
        if (clamped != value)
        {
            FrameDuration = clamped;
            return;
        }

        ObjectDefinitionService.SetFrameDuration(row.Frame, clamped);
        row.NotifyDurationChanged();
        HasChanges = true;
        StatusText = clamped == 0
            ? $"Frame {row.Index} changes every game frame."
            : $"Frame {row.Index} is held {clamped} game frame(s).";
        IsStatusError = false;
    }

    /// <summary>True when this object already has an animation of that name: the Add/Rename dialogs validate against it as the user types.</summary>
    public bool IsAnimationNameTaken(string candidate, ObjectAnimation? exclude = null) =>
        SelectedDefinition is { } d &&
        d.Animations.Any(a => a != exclude && string.Equals(a.Name, candidate.Trim(), StringComparison.OrdinalIgnoreCase));

    public void CreateAnimation(string name)
    {
        if (SelectedDefinition is not { } d) return;

        var (success, animation, error) = ObjectDefinitionService.AddAnimation(d, name);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        RefreshAnimations();
        SelectedAnimation = Animations.FirstOrDefault(r => r.Animation.Id == animation!.Id);
        StatusText = $"Added animation '{animation!.Name}' with one frame.";
        IsStatusError = false;
    }

    public void RenameAnimation(ObjectAnimation animation, string newName)
    {
        if (SelectedDefinition is not { } d) return;

        var (success, error) = ObjectDefinitionService.RenameAnimation(d, animation, newName);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        RefreshAnimations();
        StatusText = $"Renamed animation to '{animation.Name}'.";
        IsStatusError = false;
    }

    public void DeleteAnimation(ObjectAnimation animation)
    {
        if (SelectedDefinition is not { } d) return;

        var (success, error) = ObjectDefinitionService.DeleteAnimation(d, animation);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            return;
        }

        var deletedName = animation.Name;
        HasChanges = true;
        SelectedAnimation = null;
        RefreshAnimations();
        StatusText = $"Deleted animation '{deletedName}'.";
        IsStatusError = false;
    }

    /// <summary>Drag-and-drop in the animation list. The FIRST animation matters beyond cosmetics: its first frame is the object's picture on a map.</summary>
    public void ReorderAnimation(ObjectAnimation dragged, ObjectAnimation target)
    {
        if (SelectedDefinition is not { } d) return;

        ObjectDefinitionService.ReorderAnimation(d, dragged, target);
        HasChanges = true;
        RefreshAnimations();
        SelectedAnimation = Animations.FirstOrDefault(r => r.Animation.Id == dragged.Id);
        StatusText = $"Moved '{dragged.Name}'. The first animation's first frame is what a map draws.";
        IsStatusError = false;
    }

    /// <summary>Adds a frame right after the selected one, copying its layout and duration: consecutive frames almost always differ by a detail.</summary>
    [RelayCommand(CanExecute = nameof(HasAnimationSelection))]
    private void AddFrame()
    {
        if (SelectedAnimation is not { } animationRow) return;

        var (success, frame, error) = ObjectDefinitionService.AddFrame(animationRow.Animation, SelectedFrame?.Index ?? -1);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        RefreshAnimations();   // the animation row shows its frame count
        RefreshFrames();
        SelectedFrame = Frames.FirstOrDefault(r => ReferenceEquals(r.Frame, frame));
        StatusText = $"Frame {SelectedFrame?.Index ?? 0} added as a copy of the one before it.";
        IsStatusError = false;
    }

    [RelayCommand]
    private void DeleteFrame(ObjectFrameRowViewModel? row)
    {
        if (row is null || SelectedAnimation is not { } animationRow) return;

        var (success, error) = ObjectDefinitionService.DeleteFrame(animationRow.Animation, row.Frame);
        if (!success)
        {
            StatusText = error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        SelectedFrame = null;
        RefreshAnimations();
        RefreshFrames();
        StatusText = "Frame deleted.";
        IsStatusError = false;
    }

    /// <summary>Drag-and-drop in the frame strip: frame order IS play order, so this is real data.</summary>
    public void ReorderFrame(ObjectFrame dragged, ObjectFrame target)
    {
        if (SelectedAnimation is not { } animationRow) return;

        ObjectDefinitionService.ReorderFrame(animationRow.Animation, dragged, target);
        HasChanges = true;
        RefreshFrames();
        SelectedFrame = Frames.FirstOrDefault(r => ReferenceEquals(r.Frame, dragged));
        StatusText = "Frame moved.";
        IsStatusError = false;
    }
}
