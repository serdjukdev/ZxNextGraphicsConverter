using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

public class ObjectTypeServiceTests
{
    [Fact]
    public void Create_ValidName_Succeeds_AddsToProject()
    {
        var project = new ProjectState();

        var result = ObjectTypeService.Create(project, "portal");

        Assert.True(result.Success, result.Error);
        Assert.Equal("portal", result.Type!.Name);
        Assert.Contains(result.Type, project.ObjectTypes);
    }

    [Fact]
    public void Create_DuplicateName_GetsAutoDisambiguatedSuffix()
    {
        var project = new ProjectState();

        var first = ObjectTypeService.Create(project, "portal");
        var second = ObjectTypeService.Create(project, "portal");

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.Equal("portal", first.Type!.Name);
        Assert.Equal("portal_2", second.Type!.Name);
    }

    [Fact]
    public void Create_EmptyName_Rejected()
    {
        var project = new ProjectState();
        var result = ObjectTypeService.Create(project, "   ");
        Assert.False(result.Success);
        Assert.Empty(project.ObjectTypes);
    }

    [Fact]
    public void Rename_ToTakenName_Rejected_OwnNameUnchanged()
    {
        var project = new ProjectState();
        var portal = ObjectTypeService.Create(project, "portal").Type!;
        ObjectTypeService.Create(project, "character");

        var (success, error) = ObjectTypeService.Rename(project, portal, "character");

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Equal("portal", portal.Name);
    }

    [Fact]
    public void Rename_ToItsOwnCurrentName_Allowed()
    {
        var project = new ProjectState();
        var portal = ObjectTypeService.Create(project, "portal").Type!;

        var (success, error) = ObjectTypeService.Rename(project, portal, "portal");

        Assert.True(success, error);
    }

    [Fact]
    public void Delete_RemovesFromProject()
    {
        var project = new ProjectState();
        var portal = ObjectTypeService.Create(project, "portal").Type!;

        ObjectTypeService.Delete(project, portal);

        Assert.Empty(project.ObjectTypes);
    }

    [Fact]
    public void MoveUp_SwapsWithThePrecedingEntry()
    {
        var project = new ProjectState();
        var character = ObjectTypeService.Create(project, "character").Type!;
        var portal = ObjectTypeService.Create(project, "portal").Type!;

        ObjectTypeService.MoveUp(project, portal);

        Assert.Equal([portal, character], project.ObjectTypes);
    }

    [Fact]
    public void MoveUp_AlreadyFirst_NoOp()
    {
        var project = new ProjectState();
        var character = ObjectTypeService.Create(project, "character").Type!;
        var portal = ObjectTypeService.Create(project, "portal").Type!;

        ObjectTypeService.MoveUp(project, character);

        Assert.Equal([character, portal], project.ObjectTypes);
    }

    [Fact]
    public void MoveDown_SwapsWithTheFollowingEntry()
    {
        var project = new ProjectState();
        var character = ObjectTypeService.Create(project, "character").Type!;
        var portal = ObjectTypeService.Create(project, "portal").Type!;

        ObjectTypeService.MoveDown(project, character);

        Assert.Equal([portal, character], project.ObjectTypes);
    }

    [Fact]
    public void MoveDown_AlreadyLast_NoOp()
    {
        var project = new ProjectState();
        var character = ObjectTypeService.Create(project, "character").Type!;
        var portal = ObjectTypeService.Create(project, "portal").Type!;

        ObjectTypeService.MoveDown(project, portal);

        Assert.Equal([character, portal], project.ObjectTypes);
    }

    [Fact]
    public void AddSubtype_TakesTheLowestFreeBit_AndRefusesPastEight()
    {
        var project = new ProjectState();
        var type = ObjectTypeService.Create(project, "platform").Type!;

        for (var i = 0; i < ObjectType.MaxSubtypes; i++)
        {
            var added = ObjectTypeService.AddSubtype(project, type, $"variant{i}");
            Assert.True(added.Success, added.Error);
            Assert.Equal(i, added.Subtype!.BitIndex);          // 0, 1, 2, ... by default
            Assert.Equal((byte)(1 << i), added.Subtype.BitValue);
        }

        var ninth = ObjectTypeService.AddSubtype(project, type, "one_too_many");
        Assert.False(ninth.Success);
        Assert.Contains("one per bit", ninth.Error);
        Assert.Equal(ObjectType.MaxSubtypes, type.Subtypes.Count);
    }

    [Fact]
    public void AddSubtype_FillsAGapLeftByADelete()
    {
        var project = new ProjectState();
        var type = ObjectTypeService.Create(project, "platform").Type!;
        ObjectTypeService.AddSubtype(project, type, "a");
        var b = ObjectTypeService.AddSubtype(project, type, "b").Subtype!;
        ObjectTypeService.AddSubtype(project, type, "c");

        ObjectTypeService.DeleteSubtype(type, b);               // frees bit 1
        var replacement = ObjectTypeService.AddSubtype(project, type, "d");

        Assert.True(replacement.Success, replacement.Error);
        Assert.Equal(1, replacement.Subtype!.BitIndex);
    }

    /// <summary>A subtype's exported label is subobj_&lt;name&gt; with no type in it, so two types cannot both have an "idle".</summary>
    [Fact]
    public void AddSubtype_NameMustBeUniqueAcrossTheWholeProject_NotJustItsOwnType()
    {
        var project = new ProjectState();
        var enemy = ObjectTypeService.Create(project, "enemy").Type!;
        var platform = ObjectTypeService.Create(project, "platform").Type!;

        Assert.True(ObjectTypeService.AddSubtype(project, enemy, "idle").Success);
        var clash = ObjectTypeService.AddSubtype(project, platform, "IDLE");

        Assert.False(clash.Success);
        Assert.Contains("already exists", clash.Error);
        Assert.Empty(platform.Subtypes);
    }

    [Fact]
    public void SetSubtypeBit_MovesIt_ButRefusesABitASiblingAlreadyOwns()
    {
        var project = new ProjectState();
        var type = ObjectTypeService.Create(project, "platform").Type!;
        var a = ObjectTypeService.AddSubtype(project, type, "a").Subtype!;   // bit 0
        var b = ObjectTypeService.AddSubtype(project, type, "b").Subtype!;   // bit 1

        var moved = ObjectTypeService.SetSubtypeBit(type, b, 5);
        Assert.True(moved.Success, moved.Error);
        Assert.Equal(5, b.BitIndex);
        Assert.Equal(32, b.BitValue);

        var refused = ObjectTypeService.SetSubtypeBit(type, a, 5);
        Assert.False(refused.Success);
        Assert.Contains("already belongs to subtype 'b'", refused.Error);
        Assert.Equal(0, a.BitIndex);                                          // unchanged
    }
}
