using ZxNext.Core.Conversion;
using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

/// <summary>
/// The animation tables the game's own draw_unified routine reads. The format is the user's, confirmed byte
/// for byte, so these tests pin the exact bytes: a 2-byte anchor record with no offset, 4-byte records after
/// it, signed offsets measured from the anchor, each sprite's own pre-shifted palette, and the two mirror
/// corrections computed as dx_min + dx_max / dy_min + dy_max.
/// </summary>
public class ObjectAnimationExporterTests
{
    private static GraphicsAsset Sprite(string name, int paletteSlot, int sortIndex, string? subfolder = null) => new()
    {
        Name = name,
        Category = AssetCategory.Sprite4Bpp,
        Width = 16,
        Height = 16,
        PackedPixelData = new byte[128],
        PaletteSlotIndex = paletteSlot,
        SortIndex = sortIndex,
        FolderPath = subfolder is null
            ? AssetCategory.Sprite4Bpp.ToFolderPath()
            : AssetCategory.Sprite4Bpp.ToFolderPath() + "/" + subfolder
    };

    [Fact]
    public void Export_NoObjects_ReturnsNull() => Assert.Null(ObjectAnimationExporter.Export(new ProjectState()));

    /// <summary>A static object is a one-frame animation and must still export an ordinary table: his engine's "sprites in frame = 1, ordinary drawing" case.</summary>
    [Fact]
    public void Export_AStaticOneSpriteObject_IsAnOrdinaryOneFrameTable()
    {
        var project = new ProjectState();
        var hero = Sprite("hero", paletteSlot: 2, sortIndex: 0);
        project.Assets.Add(hero);

        var definition = ObjectDefinitionService.Create(project, "platform").Definition!;
        ObjectDefinitionService.SetCell(definition.FirstFrame, 0, 0, hero.Id);

        var asm = ObjectAnimationExporter.Export(project)!.AsmText;

        Assert.Equal("object_anims.asm", ObjectAnimationExporter.Export(project)!.AsmFileName);
        Assert.Contains("a_platform_main:", asm);
        Assert.Contains("    db 1          ; frames", asm);
        Assert.Contains("    dw a_platform_main_f0", asm);
        Assert.Contains("a_platform_main_f0:", asm);
        Assert.Contains("    db 0,1,0,0     ; duration, sprites, mirror X, mirror Y", asm);
        Assert.Contains("    db 32,0          ; anchor: palette, pattern (hero)", asm);   // slot 2 -> 32
        Assert.Contains("obj_platform_main: equ a_platform_main", asm);
    }

    /// <summary>
    /// The case the whole feature exists for: a head over two legs, the lower row nudged 8 pixels left. The
    /// anchor is the head (first in reading order), the legs are signed offsets from it, and the X mirror
    /// correction is dx_min + dx_max = -8 + 8 = 0.
    /// </summary>
    [Fact]
    public void Export_ARowNudgedLeft_GivesSignedOffsetsFromTheAnchorAndTheMirrorCorrections()
    {
        var project = new ProjectState();
        var head = Sprite("head", paletteSlot: 1, sortIndex: 0);
        var leg = Sprite("leg", paletteSlot: 1, sortIndex: 1);
        project.Assets.AddRange([head, leg]);

        var definition = ObjectDefinitionService.Create(project, "hero").Definition!;
        var frame = definition.FirstFrame;
        ObjectDefinitionService.SetCell(frame, 0, 0, head.Id);
        ObjectDefinitionService.AddRow(frame);
        ObjectDefinitionService.AddCell(frame, 1);
        ObjectDefinitionService.SetCell(frame, 1, 0, leg.Id);
        ObjectDefinitionService.SetCell(frame, 1, 1, leg.Id);
        ObjectDefinitionService.SetRowOffset(frame, 1, -8, 0);
        ObjectDefinitionService.SetFrameDuration(frame, 6);

        var asm = ObjectAnimationExporter.Export(project)!.AsmText;

        // dx of the legs: -8 and +8 relative to the head; dy 16. mirror X = -8 + 8 = 0, mirror Y = 0 + 16 = 16.
        Assert.Contains("    db 6,3,0,16     ; duration, sprites, mirror X, mirror Y", asm);
        Assert.Contains("    db 16,0          ; anchor: palette, pattern (head)", asm);
        Assert.Contains("    db 248,16,16,1   ; dx, dy, palette, pattern (leg)", asm);  // -8 as a byte
        Assert.Contains("    db 8,16,16,1   ; dx, dy, palette, pattern (leg)", asm);
    }

    [Fact]
    public void Export_SeveralFramesAndAnimations_EachGetsItsOwnSelfContainedTable()
    {
        var project = new ProjectState();
        var a = Sprite("a", paletteSlot: 0, sortIndex: 0);
        var b = Sprite("b", paletteSlot: 3, sortIndex: 1);
        project.Assets.AddRange([a, b]);

        var definition = ObjectDefinitionService.Create(project, "hero").Definition!;
        ObjectDefinitionService.SetCell(definition.FirstFrame, 0, 0, a.Id);
        ObjectDefinitionService.SetFrameDuration(definition.FirstFrame, 4);

        // A second frame, copied from the first and then repainted: the usual way a phase is made.
        var idle = definition.Animations[0];
        var second = ObjectDefinitionService.AddFrame(idle, 0).Frame!;
        ObjectDefinitionService.SetCell(second, 0, 0, b.Id);

        var run = ObjectDefinitionService.AddAnimation(definition, "run").Animation!;
        ObjectDefinitionService.SetCell(run.Frames[0], 0, 0, b.Id);

        var asm = ObjectAnimationExporter.Export(project)!.AsmText;

        Assert.Contains("a_hero_main:", asm);
        Assert.Contains("    db 2          ; frames", asm);
        Assert.Contains("    dw a_hero_main_f0", asm);
        Assert.Contains("    dw a_hero_main_f1", asm);
        Assert.Contains("a_hero_main_f1:", asm);
        Assert.Contains("    db 4,1,0,0     ; duration, sprites, mirror X, mirror Y", asm);   // the copy kept the duration
        Assert.Contains("    db 48,1          ; anchor: palette, pattern (b)", asm);          // slot 3 -> 48
        Assert.Contains("a_hero_run:", asm);
        Assert.Contains("obj_hero_run: equ a_hero_run", asm);
        Assert.DoesNotContain("obj_anim_table", asm);   // deliberately no table of animation addresses
    }

    [Fact]
    public void Export_SanitizesAndDeCollidesLabels()
    {
        var project = new ProjectState();
        var sprite = Sprite("s", paletteSlot: 0, sortIndex: 0);
        project.Assets.Add(sprite);

        var first = ObjectDefinitionService.Create(project, "big hero").Definition!;      // space
        ObjectDefinitionService.SetCell(first.FirstFrame, 0, 0, sprite.Id);
        var second = ObjectDefinitionService.Create(project, "big.hero").Definition!;     // '.' -> same label
        ObjectDefinitionService.SetCell(second.FirstFrame, 0, 0, sprite.Id);

        var asm = ObjectAnimationExporter.Export(project)!.AsmText;

        Assert.Contains("a_big_hero_main:", asm);
        Assert.Contains("a_big_hero_main_2:", asm);   // de-collided
        Assert.DoesNotContain("a_big hero", asm);
    }

    /// <summary>A sub-folder sprite's pattern number is local to its own binary, which is exactly what a map placement would export for it too.</summary>
    [Fact]
    public void Export_ASubfolderSprite_UsesItsLocalPatternNumber()
    {
        var project = new ProjectState();
        var rootSprite = Sprite("root", paletteSlot: 0, sortIndex: 0);
        var bankFirst = Sprite("walk_0", paletteSlot: 0, sortIndex: 1, subfolder: "walk");
        var bankSecond = Sprite("walk_1", paletteSlot: 0, sortIndex: 2, subfolder: "walk");
        project.Assets.AddRange([rootSprite, bankFirst, bankSecond]);

        var definition = ObjectDefinitionService.Create(project, "enemy").Definition!;
        ObjectDefinitionService.SetCell(definition.FirstFrame, 0, 0, bankSecond.Id);

        var asm = ObjectAnimationExporter.Export(project)!.AsmText;

        // Second sprite inside its own sub-folder: local index 1, not the global 2.
        Assert.Contains("    db 0,1          ; anchor: palette, pattern (walk_1)", asm);
        Assert.Equal(["walk_1"], ObjectDefinitionService.FindSubfolderSprites(project, definition));
    }
}
