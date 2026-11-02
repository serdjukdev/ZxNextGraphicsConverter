using ZxNext.Core.Export;
using Xunit;

namespace ZxNext.Core.Tests;

/// <summary>
/// A map row key is emitted verbatim as an ASM label prefix (and used as the output filename), while map
/// names have no character restrictions in the app. Before 2026-11-03 a map called "my level" exported
/// "my level_tilemap_grid_width: equ 40", which sjasmplus cannot assemble.
/// </summary>
public class ExportFileNamingTests
{
    [Theory]
    [InlineData("my level", "my_level")]
    [InlineData("level.1", "level_1")]
    [InlineData("caves - deep", "caves_deep")]
    [InlineData("  padded  ", "padded")]
    [InlineData("2nd floor", "_2nd_floor")] // a leading digit is not a legal label start
    public void MapRowKeys_SanitizeTheMapName_ForEveryMapRow(string mapName, string expectedPrefix)
    {
        Assert.Equal($"{expectedPrefix}_tilemap_grid", ExportFileNaming.GridRowKey(mapName, "tilemap"));
        Assert.Equal($"{expectedPrefix}_8bpp_grid", ExportFileNaming.GridRowKey(mapName, "8bpp"));
        Assert.Equal($"{expectedPrefix}_tilemap_metatiles", ExportFileNaming.MetatilesRowKey(mapName, "tilemap"));
        Assert.Equal($"{expectedPrefix}_8bpp_metatiles", ExportFileNaming.MetatilesRowKey(mapName, "8bpp"));
        Assert.Equal($"{expectedPrefix}_objects", ExportFileNaming.ObjectsRowKey(mapName));
        Assert.Equal($"{expectedPrefix}_info", ExportFileNaming.InfoRowKey(mapName));
    }

    /// <summary>An already label-safe name must come out byte-for-byte unchanged, so existing projects' labels and filenames do not move.</summary>
    [Fact]
    public void MapRowKeys_LeaveALabelSafeNameAlone()
    {
        Assert.Equal("level1_tilemap_grid", ExportFileNaming.GridRowKey("level1", "tilemap"));
        Assert.Equal("level1_8bpp_metatiles", ExportFileNaming.MetatilesRowKey("level1", "8bpp"));
        Assert.Equal("Level_1_objects", ExportFileNaming.ObjectsRowKey("Level_1"));
        Assert.Equal("map_info", ExportFileNaming.InfoRowKey("map"));
    }
}
