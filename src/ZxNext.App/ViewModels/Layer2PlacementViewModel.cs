using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

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

    /// <summary>
    /// Crops/pads <paramref name="sourceRgba32"/> against the chosen offset and padding choice.
    /// The result is always zero-initialized first (fully transparent), so any padded area needs
    /// no separate fill step — only the real source pixels that fit get copied in.
    /// </summary>
    public byte[] BuildPlacedRgba(byte[] sourceRgba32)
    {
        var width = ResultWidth;
        var height = ResultHeight;
        var result = new byte[width * height * 4];

        var copyWidth = Math.Min(SourceWidth - OffsetLeft, width);
        var copyHeight = Math.Min(SourceHeight - OffsetTop, height);

        for (var y = 0; y < copyHeight; y++)
        {
            var srcRowStart = ((OffsetTop + y) * SourceWidth + OffsetLeft) * 4;
            var dstRowStart = y * width * 4;
            Array.Copy(sourceRgba32, srcRowStart, result, dstRowStart, copyWidth * 4);
        }

        return result;
    }
}
