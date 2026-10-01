using FluentAssertions;
using Nocturne.API.Services.Auth;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

public class ButtonColorContrastTests
{
    [Theory]
    [InlineData("#24292e", ButtonColorContrast.Light)]
    [InlineData("#1a73e8", ButtonColorContrast.Light)]
    [InlineData("#000", ButtonColorContrast.Light)]
    [InlineData("#ffffff", ButtonColorContrast.Dark)]
    [InlineData("#FFD700", ButtonColorContrast.Dark)]
    [InlineData("  #fff8  ", ButtonColorContrast.Dark)]
    [InlineData("#24292eff", ButtonColorContrast.Light)]
    public void Picks_the_higher_contrast_foreground(string background, string expected)
    {
        ButtonColorContrast.ForegroundFor(background).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("rgb(0, 0, 0)")]
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    [InlineData("#+12345")]
    public void Returns_null_for_colours_it_cannot_read(string? background)
    {
        ButtonColorContrast.ForegroundFor(background).Should().BeNull();
    }
}
