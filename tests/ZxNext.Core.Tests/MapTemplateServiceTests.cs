using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

public class MapTemplateServiceTests
{
    [Fact]
    public void Create_AddsTemplateWithGivenShapeToProject()
    {
        var project = new ProjectState();

        var template = MapTemplateService.Create(project, "room", MetatileKind.FourBpp, 1, 2, 2, [3, 1, 0, 2], [0, 5, 9, 255]);

        Assert.Same(template, Assert.Single(project.Templates));
        Assert.Equal("room", template.Name);
        Assert.Equal(MetatileKind.FourBpp, template.Kind);
        Assert.Equal(1, template.GridSize);
        Assert.Equal(2, template.Width);
        Assert.Equal(2, template.Height);
        Assert.Equal((byte[])[3, 1, 0, 2], template.MetatileIndices);
        Assert.Equal((byte[])[0, 5, 9, 255], template.CellAttributes);
    }

    [Fact]
    public void Create_WithoutAttributes_LeavesCellAttributesNull()
    {
        var project = new ProjectState();

        var template = MapTemplateService.Create(project, "no_attrs", MetatileKind.EightBpp, 2, 3, 1, [7, 8, 9], null);

        Assert.Null(template.CellAttributes);
    }

    [Fact]
    public void Delete_RemovesTemplateFromProject()
    {
        var project = new ProjectState();
        var template = MapTemplateService.Create(project, "room", MetatileKind.FourBpp, 1, 2, 2, [0, 0, 0, 0], [0, 0, 0, 0]);

        MapTemplateService.Delete(project, template);

        Assert.Empty(project.Templates);
    }
}
