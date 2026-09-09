using ZxNext.Core.Export;
using Xunit;

namespace ZxNext.Core.Tests;

public class AsmLabelTests
{
    [Theory]
    [InlineData("hero", "hero")]
    [InlineData("player idle", "player_idle")]
    [InlineData("industrial.v2_16_272", "industrial_v2_16_272")]
    [InlineData("gem--a", "gem_a")]                 // runs of separators collapse
    [InlineData("  spaced  ", "spaced")]            // leading/trailing trimmed
    [InlineData("42", "_42")]                       // leading digit
    [InlineData("!!!", "unnamed")]                  // nothing valid left
    [InlineData("Keep_Case", "Keep_Case")]
    public void Sanitize_ProducesAValidLabelBody(string input, string expected)
    {
        Assert.Equal(expected, AsmLabel.Sanitize(input));
    }

    [Fact]
    public void SanitizeUnique_SecondCollisionGetsASuffix_AndTheSetTracksBoth()
    {
        var used = new HashSet<string>();
        Assert.Equal("gem_a", AsmLabel.SanitizeUnique("gem-a", used));
        Assert.Equal("gem_a_2", AsmLabel.SanitizeUnique("gem.a", used));
        Assert.Equal("gem_a_3", AsmLabel.SanitizeUnique("gem a", used));
        Assert.Contains("gem_a", used);
        Assert.Contains("gem_a_2", used);
    }
}
