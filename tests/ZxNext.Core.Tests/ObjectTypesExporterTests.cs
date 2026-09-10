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

    [Fact]
    public void Export_SanitizesTypeNames_AndDeCollides()
    {
        var project = new ProjectState();
        project.ObjectTypes.Add(new ObjectType { Name = "big door" });   // space
        project.ObjectTypes.Add(new ObjectType { Name = "big.door" });   // '.' -> same sanitized label
        project.ObjectTypes.Add(new ObjectType { Name = "9lives" });     // leading digit

        var asm = ObjectTypesExporter.Export(project)!.AsmText;

        Assert.Contains("obj_big_door: equ 0", asm);
        Assert.Contains("obj_big_door_2: equ 1", asm);   // de-collided
        Assert.Contains("obj__9lives: equ 2", asm);      // leading digit gets an underscore
        Assert.DoesNotContain("obj_big door", asm);       // never emits a raw name with a space
    }
}
