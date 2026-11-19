using Nocturne.API.Services.Docs;

namespace Nocturne.API.Tests.Services.Docs;

/// <summary>
/// <see cref="NocturneScalarTheme"/> treats a missing resource as empty, so a broken
/// embed in the csproj would ship the docs page unthemed without failing anything else.
/// </summary>
public class NocturneScalarThemeTests
{
    [Theory]
    [InlineData(NocturneScalarTheme.ThemeResource)]
    [InlineData(NocturneScalarTheme.NocturneThemeResource)]
    [InlineData(NocturneScalarTheme.ChartsThemeResource)]
    public void Theme_source_is_embedded(string resourceName)
    {
        typeof(NocturneScalarTheme).Assembly.GetManifestResourceNames()
            .Should().Contain(resourceName);
    }

    [Fact]
    public void Build_carries_the_theme_files_adapted_for_scalar()
    {
        var css = NocturneScalarTheme.Build();

        css.Should().Contain("--radius:", "theme.css declares it");
        css.Should().Contain("--status-normal:", "nocturne-theme.css declares it");
        css.Should().Contain("--system-event-warning:", "the charts theme declares it, and Scalar's yellow reads it");
        css.Should().Contain(".dark-mode", "Nocturne's .dark blocks are mirrored for Scalar's switch");
        css.Should().NotContain("@import", "Tailwind directives are stripped");
    }
}
