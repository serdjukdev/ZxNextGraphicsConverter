using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

public class MapServiceTests
{
    [Fact]
    public void Create_ValidSize_Succeeds_BothLayersFullyEmpty()
    {
        var project = new ProjectState();

        var result = MapService.Create(project, "level1", width: 4, height: 3, metatileGridSize: 2);

        Assert.True(result.Success, result.Error);
        var map = result.Map!;
        Assert.Equal(4, map.Width);
        Assert.Equal(3, map.Height);
        Assert.Equal(2, map.MetatileGridSize);
        Assert.Equal(12, map.TilemapLayer.MetatileIndices.Length);
        Assert.All(map.TilemapLayer.MetatileIndices, b => Assert.Equal(MapGridLayer.EmptyCell, b));
        Assert.All(map.TileLayer8Bpp.MetatileIndices, b => Assert.Equal(MapGridLayer.EmptyCell, b));
        Assert.Empty(map.SpriteLayer);
        Assert.True(map.TilemapLayerVisible);
        Assert.True(map.TileLayer8BppVisible);
        Assert.True(map.SpriteLayerVisible);
        Assert.Contains(map, project.Maps);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Create_InvalidGridSize_Rejected(int gridSize)
    {
        var project = new ProjectState();
        var result = MapService.Create(project, "bad", 4, 4, gridSize);
        Assert.False(result.Success);
        Assert.Empty(project.Maps);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-1, 4)]
    public void Create_NonPositiveDimensions_Rejected(int width, int height)
    {
        var project = new ProjectState();
        var result = MapService.Create(project, "bad", width, height, 2);
        Assert.False(result.Success);
        Assert.Empty(project.Maps);
    }

    [Fact]
    public void Create_ExactlyAtCellLimit_Succeeds_OneOver_Rejected()
    {
        var project = new ProjectState();

        var atLimit = MapService.Create(project, "fits", 128, 128, 2); // 16384 exactly
        Assert.True(atLimit.Success, atLimit.Error);

        var overLimit = MapService.Create(project, "too_big", 129, 128, 2); // 16512
        Assert.False(overLimit.Success);
        Assert.Contains("16384", overLimit.Error);
    }

    [Fact]
    public void Create_DuplicateName_GetsAutoDisambiguatedSuffix()
    {
        var project = new ProjectState();

        var first = MapService.Create(project, "level1", 4, 4, 2);
        var second = MapService.Create(project, "level1", 4, 4, 2);

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.Equal("level1", first.Map!.Name);
        Assert.Equal("level1_2", second.Map!.Name);
    }

    [Fact]
    public void Create_SortIndex_IsGlobalAndDense()
    {
        var project = new ProjectState();
        var a = MapService.Create(project, "a", 2, 2, 2).Map!;
        var b = MapService.Create(project, "b", 2, 2, 2).Map!;
        Assert.Equal(0, a.SortIndex);
        Assert.Equal(1, b.SortIndex);
    }
}
