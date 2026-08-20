using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.AtlasSlicer;

namespace ZxNext.App.ViewModels;

/// <summary>
/// Backs the Layer2 placement dialog: positions a dropped image within (or crops it against) the
/// fixed target canvas for a Layer2 category (256x192 / 320x256 / 640x256). Unlike the grid atlas
/// slicer, there is always exactly one output image, never a repeating grid.
/// </summary>
public partial class Layer2PlacementViewModel : ObservableObject
{
    public WriteableBitmap SourcePreview { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int TargetWidth { get; }
    public int TargetHeight { get; }

    public int MaxOffsetLeft => Math.Max(0, SourceWidth - TargetWidth);
    public int MaxOffsetTop => Math.Max(0, SourceHeight - TargetHeight);

    /// <summary>Whether there's anything smaller than the target on either axis, i.e. the "pad with transparent" choice is actually relevant.</summary>
    public bool CanChoosePadding => SourceWidth < TargetWidth || SourceHeight < TargetHeight;

    [ObservableProperty]
    private int offsetLeft;

    [ObservableProperty]
    private int offsetTop;

    /// <summary>Checked (default): pad any smaller-than-canvas axis with transparent, so the exported asset is always the full canvas size. Unchecked: keep the smaller axis at its actual source size — for a programmer composing several partial Layer2 pieces themselves.</summary>
    [ObservableProperty]
    private bool padToFullCanvas = true;

    public int ResultWidth => SourceWidth > TargetWidth ? TargetWidth : (PadToFullCanvas ? TargetWidth : SourceWidth);
    public int ResultHeight => SourceHeight > TargetHeight ? TargetHeight : (PadToFullCanvas ? TargetHeight : SourceHeight);

    /// <summary>How many real source pixels actually get copied (as opposed to <see cref="ResultWidth"/>/<see cref="ResultHeight"/>, the full — possibly padded — canvas size) — stored on the asset so a later re-quantize can reproduce this exact composition instead of over-reading past the source.</summary>
    public int CopyWidth => Math.Max(0, Math.Min(SourceWidth - OffsetLeft, ResultWidth));
    public int CopyHeight => Math.Max(0, Math.Min(SourceHeight - OffsetTop, ResultHeight));

    public Layer2PlacementViewModel(WriteableBitmap preview, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        SourcePreview = preview;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        TargetWidth = targetWidth;
        TargetHeight = targetHeight;
    }

    partial void OnOffsetLeftChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, MaxOffsetLeft);
        if (clamped != value) OffsetLeft = clamped;
    }

    partial void OnOffsetTopChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, MaxOffsetTop);
        if (clamped != value) OffsetTop = clamped;
    }

    partial void OnPadToFullCanvasChanged(bool value)
    {
        OnPropertyChanged(nameof(ResultWidth));
        OnPropertyChanged(nameof(ResultHeight));
    }

    /// <summary>Crops/pads <paramref name="sourceRgba32"/> against the chosen offset and padding choice — see <see cref="Layer2Composer"/> for the shared logic (also reused by re-quantize).</summary>
    public byte[] BuildPlacedRgba(byte[] sourceRgba32) =>
        Layer2Composer.Compose(sourceRgba32, SourceWidth, SourceHeight, OffsetLeft, OffsetTop, CopyWidth, CopyHeight, ResultWidth, ResultHeight);
}
