namespace ZxNext.Core.Model;

/// <summary>An imported PNG/BMP/JPG file, before any Next conversion. Pixel data is decoded on demand.</summary>
public class SourceImage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}
