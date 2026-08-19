namespace ZxNext.Core.Model;

/// <summary>
/// The four fixed top-level asset categories of a project's virtual tree
/// (sprite/4bpp, sprite/8bpp, tile/4bpp, tile/8bpp).
/// </summary>
public enum AssetCategory
{
    Sprite4Bpp,
    Sprite8Bpp,
    Tile4Bpp,
    Tile8Bpp
}

public static class AssetCategoryExtensions
{
    public static string ToFolderPath(this AssetCategory category) => category switch
    {
        AssetCategory.Sprite4Bpp => "sprite/4bpp/images",
        AssetCategory.Sprite8Bpp => "sprite/8bpp/images",
        AssetCategory.Tile4Bpp => "tile/4bpp/images",
        AssetCategory.Tile8Bpp => "tile/8bpp/images",
        _ => throw new System.ArgumentOutOfRangeException(nameof(category))
    };

    public static bool IsFourBpp(this AssetCategory category) =>
        category is AssetCategory.Sprite4Bpp or AssetCategory.Tile4Bpp;

    /// <summary>Fixed asset cell size: 16x16 for sprites, 8x8 for tiles.</summary>
    public static (int Width, int Height) CellSize(this AssetCategory category) => category switch
    {
        AssetCategory.Sprite4Bpp or AssetCategory.Sprite8Bpp => (16, 16),
        AssetCategory.Tile4Bpp or AssetCategory.Tile8Bpp => (8, 8),
        _ => throw new System.ArgumentOutOfRangeException(nameof(category))
    };
}
