using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests.Configuration;

public class FirstDayOfWeekTests
{
    private static readonly Dictionary<string, DayOfWeek> Regions = new()
    {
        ["en-US"] = DayOfWeek.Sunday,
        ["en-GB"] = DayOfWeek.Monday,
        ["en-AU"] = DayOfWeek.Monday,
        ["en-CA"] = DayOfWeek.Sunday,
        ["en-IE"] = DayOfWeek.Monday,
        ["en-NZ"] = DayOfWeek.Monday,
        ["en-ZA"] = DayOfWeek.Sunday,
        ["de-DE"] = DayOfWeek.Monday,
        ["fr-FR"] = DayOfWeek.Monday,
        ["es-ES"] = DayOfWeek.Monday,
        ["it-IT"] = DayOfWeek.Monday,
        ["nl-NL"] = DayOfWeek.Monday,
        ["pl-PL"] = DayOfWeek.Monday,
        ["pt-PT"] = DayOfWeek.Sunday,
        ["pt-BR"] = DayOfWeek.Sunday,
        ["sv-SE"] = DayOfWeek.Monday,
        ["nb-NO"] = DayOfWeek.Monday,
        ["da-DK"] = DayOfWeek.Monday,
        ["fi-FI"] = DayOfWeek.Monday,
        ["cs-CZ"] = DayOfWeek.Monday,
        ["ru-RU"] = DayOfWeek.Monday,
        ["ja-JP"] = DayOfWeek.Sunday,
    };

    [Fact]
    public void Every_allowed_region_is_classified()
    {
        UserDisplayPreferences.AllowedRegionFormats.Where(t => t != "")
            .Should().BeEquivalentTo(Regions.Keys);
    }

    [Theory]
    [MemberData(nameof(RegionCases))]
    public void A_region_starts_its_week_on_its_own_day(string region, DayOfWeek expected)
    {
        FirstDayOfWeek.Resolve(region, "de").Should().Be(expected);
        FirstDayOfWeek.Resolve(region, null).Should().Be(expected);
    }

    public static TheoryData<string, DayOfWeek> RegionCases()
    {
        var data = new TheoryData<string, DayOfWeek>();
        foreach (var (region, day) in Regions)
        {
            data.Add(region, day);
        }
        return data;
    }

    [Theory]
    [InlineData("en", DayOfWeek.Sunday)]
    [InlineData("pt", DayOfWeek.Sunday)]
    [InlineData("ja", DayOfWeek.Sunday)]
    [InlineData("ko", DayOfWeek.Sunday)]
    [InlineData("de", DayOfWeek.Monday)]
    [InlineData("fr", DayOfWeek.Monday)]
    [InlineData("zh", DayOfWeek.Monday)]
    public void An_empty_region_follows_the_display_language(string language, DayOfWeek expected)
    {
        FirstDayOfWeek.Resolve("", language).Should().Be(expected);
        FirstDayOfWeek.Resolve(null, language).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("xx-XX", "xx")]
    [InlineData("", "xx")]
    public void An_unknown_or_missing_preference_starts_on_Monday(string? region, string? language)
    {
        FirstDayOfWeek.Resolve(region, language).Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public void A_region_overrides_the_display_language()
    {
        FirstDayOfWeek.Resolve("en-GB", "en").Should().Be(DayOfWeek.Monday);
        FirstDayOfWeek.Resolve("en-US", "de").Should().Be(DayOfWeek.Sunday);
    }

    [Fact]
    public void The_table_matches_the_frontend_Intl_lookup()
    {
        var fixture = JsonSerializer.Deserialize<Dictionary<string, int>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "first-day-of-week.json")))!;

        fixture.Should().NotBeEmpty();
        fixture.Keys.Should().Contain(UserDisplayPreferences.AllowedRegionFormats.Where(t => t != ""));
        foreach (var (tag, day) in fixture)
        {
            var resolved = tag.Contains('-')
                ? FirstDayOfWeek.Resolve(tag, null)
                : FirstDayOfWeek.Resolve("", tag);
            ((int)resolved).Should().Be(day, $"the frontend resolves {tag} to day {day}");
        }
    }
}
