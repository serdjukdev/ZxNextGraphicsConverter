namespace ZxNext.Core.Export;

/// <summary>How a folder's (or, for Layer2, one image's) binary data is split into files at export time. Chosen per row in the Export dialog, default <see cref="EightKb"/> everywhere.</summary>
public enum ExportChunkSize
{
    EightKb,
    SixteenKb,
    WholeFile
}

public static class ExportChunkSizeExtensions
{
    /// <summary>The byte boundary <see cref="BinaryChunker"/>/<see cref="Layer2Exporter"/> pack against — <see cref="ExportChunkSize.WholeFile"/> maps to <see cref="int.MaxValue"/> so nothing ever crosses a "boundary," producing exactly one file no matter how big the data is.</summary>
    public static int ToByteBoundary(this ExportChunkSize size) => size switch
    {
        ExportChunkSize.EightKb => 8192,
        ExportChunkSize.SixteenKb => 16384,
        ExportChunkSize.WholeFile => int.MaxValue,
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    };
}
