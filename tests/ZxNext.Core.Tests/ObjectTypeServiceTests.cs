using ZxNext.Core.Conversion;
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
}
