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

    /// <summary>
    /// A subtype exports as subobj_&lt;name&gt; with the BIT'S VALUE, not its index, so game code tests it
    /// with a plain AND against an object's userByte. The label carries no type name, which is why the
    /// names are unique project-wide.
    /// </summary>
    [Fact]
    public void Export_EmitsSubobjConstantsWithTheBitValue_NoTypeNameInTheLabel()
    {
        var project = new ProjectState();
        var platform = new ObjectType { Name = "platform" };
        platform.Subtypes.Add(new ObjectSubtype { Name = "moving", BitIndex = 3 });
        platform.Subtypes.Add(new ObjectSubtype { Name = "falling", BitIndex = 4 });
        project.ObjectTypes.Add(platform);
        project.ObjectTypes.Add(new ObjectType { Name = "enemy" }); // no subtypes: gets no subobj lines

        var asm = ObjectTypesExporter.Export(project)!.AsmText;

        Assert.Contains("subobj_moving: equ 8   ; bit 3", asm);
        Assert.Contains("subobj_falling: equ 16   ; bit 4", asm);
        Assert.DoesNotContain("subobj_platform_", asm);
        Assert.Contains("; subtypes of platform", asm);
        Assert.DoesNotContain("; subtypes of enemy", asm);
    }

    [Fact]
    public void Export_SubtypesAreListedByBitOrder_NotCreationOrder()
    {
        var project = new ProjectState();
        var type = new ObjectType { Name = "platform" };
        type.Subtypes.Add(new ObjectSubtype { Name = "later", BitIndex = 7 });
        type.Subtypes.Add(new ObjectSubtype { Name = "earlier", BitIndex = 0 });
        project.ObjectTypes.Add(type);

        var asm = ObjectTypesExporter.Export(project)!.AsmText;

        Assert.True(asm.IndexOf("subobj_earlier", StringComparison.Ordinal) < asm.IndexOf("subobj_later", StringComparison.Ordinal));
        Assert.Contains("subobj_earlier: equ 1   ; bit 0", asm);
        Assert.Contains("subobj_later: equ 128   ; bit 7", asm);
    }
}
