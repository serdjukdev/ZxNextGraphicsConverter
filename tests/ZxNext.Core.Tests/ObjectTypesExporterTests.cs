using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

public class ObjectTypesExporterTests
{
    [Fact]
    public void Export_EmitsOneEquPerType_InListOrder()
    {
        var project = new ProjectState();
        project.ObjectTypes.Add(new ObjectType { Name = "character" });
        project.ObjectTypes.Add(new ObjectType { Name = "portal" });

        var result = ObjectTypesExporter.Export(project);

        Assert.NotNull(result);
        Assert.Equal("object_types", result!.RowKey);
        Assert.Equal("object_types.asm", result.AsmFileName);
        Assert.False(result.IsChunked);
        Assert.Null(result.PaletteFile);
        Assert.Contains("obj_character: equ 0", result.AsmText);
        Assert.Contains("obj_portal: equ 1", result.AsmText);
    }

    [Fact]
    public void Export_NoTypesDefined_ReturnsNull()
    {
        var project = new ProjectState();
        Assert.Null(ObjectTypesExporter.Export(project));
    }
}
