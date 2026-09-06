using FluentAssertions;
using Nocturne.Core.Models.Sleep.Report;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Sleep;

[Trait("Category", "Unit")]
public class SleepStageReferenceRangeSetTests
{
    [Fact]
    public void Resolve_UnknownAgeAndSex_FallsBackToAdultFemale()
    {
        var set = SleepStageReferenceRangeSet.Resolve(ageYears: null, sex: null);

        // Adult-female deep band preserves the historical AASM default (12–23%).
        set.DeepMin.Should().Be(12);
        set.DeepMax.Should().Be(23);
        set.RemMin.Should().Be(20);
        set.RemMax.Should().Be(25);
        // Age unknown → adult band, but the label makes no age claim.
        set.Label.Should().Be("adults");
    }

    [Fact]
    public void Default_MatchesResolveWithNoInputs()
    {
        var set = SleepStageReferenceRangeSet.Default;

        set.DeepMin.Should().Be(SleepStageReferenceRangeSet.Resolve(null, null).DeepMin);
        set.Label.Should().Be("adults");
    }

    [Fact]
    public void Resolve_NegativeAge_TreatedAsUnknown()
    {
        // A future-dated DOB yields a negative age; it must fall back to the adult band, not children.
        var set = SleepStageReferenceRangeSet.Resolve(ageYears: -3, sex: BiologicalSex.Female);

        set.DeepMin.Should().Be(SleepStageReferenceRangeSet.Resolve(null, BiologicalSex.Female).DeepMin);
        set.Label.Should().Be("women");
    }

    [Fact]
    public void Resolve_UnknownSex_UsesFemaleNorms()
    {
        var unknown = SleepStageReferenceRangeSet.Resolve(ageYears: 30, sex: null);
        var female = SleepStageReferenceRangeSet.Resolve(ageYears: 30, sex: BiologicalSex.Female);

        unknown.DeepMin.Should().Be(female.DeepMin);
        unknown.DeepMax.Should().Be(female.DeepMax);
        // But the label omits the sex word when sex is unknown.
        unknown.Label.Should().Be("adults 18-39");
        female.Label.Should().Be("women 18-39");
    }

    [Fact]
    public void Resolve_WomenRetainMoreDeepSleepThanMen()
    {
        var women = SleepStageReferenceRangeSet.Resolve(ageYears: 50, sex: BiologicalSex.Female);
        var men = SleepStageReferenceRangeSet.Resolve(ageYears: 50, sex: BiologicalSex.Male);

        women.DeepMax.Should().BeGreaterThan(men.DeepMax);
        women.DeepMin.Should().BeGreaterThan(men.DeepMin);
    }

    [Fact]
    public void Resolve_DeepSleepDeclinesWithAge()
    {
        var child = SleepStageReferenceRangeSet.Resolve(ageYears: 8, sex: BiologicalSex.Female);
        var adult = SleepStageReferenceRangeSet.Resolve(ageYears: 30, sex: BiologicalSex.Female);
        var older = SleepStageReferenceRangeSet.Resolve(ageYears: 75, sex: BiologicalSex.Female);

        child.DeepMax.Should().BeGreaterThan(adult.DeepMax);
        adult.DeepMax.Should().BeGreaterThan(older.DeepMax);
    }

    [Fact]
    public void Resolve_WakeIncreasesWithAge()
    {
        var adult = SleepStageReferenceRangeSet.Resolve(ageYears: 30, sex: BiologicalSex.Male);
        var older = SleepStageReferenceRangeSet.Resolve(ageYears: 75, sex: BiologicalSex.Male);

        older.AwakeMax.Should().BeGreaterThan(adult.AwakeMax);
    }

    [Theory]
    [InlineData(8, "children")]
    [InlineData(15, "teenagers")]
    [InlineData(30, "adults 18-39")]
    [InlineData(50, "adults 40-64")]
    [InlineData(70, "older adults (65+)")]
    public void Resolve_ProducesExpectedAgeBandLabel(int age, string expectedLabel)
    {
        var set = SleepStageReferenceRangeSet.Resolve(age, sex: null);
        set.Label.Should().Be(expectedLabel);
    }
}
