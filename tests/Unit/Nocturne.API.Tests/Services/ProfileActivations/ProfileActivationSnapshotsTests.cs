using FluentAssertions;
using Nocturne.API.Services.ProfileActivations;
using Xunit;

namespace Nocturne.API.Tests.Services.ProfileActivations;

[Trait("Category", "Unit")]
public class ProfileActivationSnapshotsTests
{
    private static string Json(string units, double sens, double low, double high, double sens2 = 0) =>
        $$"""
        {"units":"{{units}}","dia":5,"basal":[{"time":"00:00","value":0.8}],"carbratio":[{"time":"00:00","value":9.1}],
         "sens":[{"time":"00:00","value":{{sens}}}{{(sens2 > 0 ? $$""",{"time":"06:00","value":{{sens2}}}""" : "")}}],
         "target_low":[{"time":"00:00","value":{{low}}}],"target_high":[{"time":"00:00","value":{{high}}}]}
        """;

    private static (double Low, double High, double[] Isf) Read(string json)
    {
        var snapshot = ProfileActivationSnapshots.From(json, null)!;
        var target = snapshot.Targets!.Single();
        return (target.Low, target.High, snapshot.Sensitivity!.Select(s => s.Value).ToArray());
    }

    [Fact]
    public void AMmolProfile_IsConvertedOnce()
    {
        Read(Json("mmol", 3.5, 5.0, 5.5)).Should().BeEquivalentTo((90.0, 99.0, new[] { 63.0 }));
    }

    [Theory]
    [InlineData("mmol")]
    [InlineData("mmol/L")]
    public void AMmolLabelledProfileCarryingMgdl_IsNotConvertedAgain(string units)
    {
        var (low, high, isf) = Read(Json(units, 63, 90, 99, sens2: 58));

        (low, high).Should().Be((90.0, 99.0));
        isf.Should().Equal(63, 58);
    }

    [Fact]
    public void AProfileAlreadyConvertedOnceMore_IsUndoneToItsOriginalValues()
    {
        Read(Json("mmol", 1135, 1622, 1784)).Should().BeEquivalentTo((90.0, 99.0, new[] { 63.0 }));
    }

    [Fact]
    public void ReSendingTheSameSwitch_BuildsTheSameSnapshot()
    {
        var json = Json("mmol", 63, 90, 99);

        Read(json).Should().BeEquivalentTo(Read(json));
    }

    [Theory]
    [InlineData(5.0, 90.0)]
    [InlineData(6.0, 108.0)]
    public void ACorrectMmolIsf_BesideMgdlTargets_IsNotDivided(double isfMmol, double isfMgdl)
    {
        var (low, _, isf) = Read(Json("mmol", isfMmol, 90, 99));

        low.Should().Be(90);
        isf.Should().Equal(isfMgdl);
    }

    [Fact]
    public void AnIsfConvertedMoreTimesThanItsTargets_IsLeftAsBuilt()
    {
        var (low, _, isf) = Read(Json("mmol", 1135, 90, 99));

        low.Should().Be(90);
        isf.Should().Equal(20451);
    }

    [Fact]
    public void TargetsThatAreNotAConversionItCanUndo_AreLeftAsBuilt()
    {
        Read(Json("mmol", 3.5, 90.5, 99.5)).Low.Should().Be(1631);
    }

    [Fact]
    public void AMgdlProfile_IsLeftAsUploaded()
    {
        Read(Json("mg/dl", 1135, 1622, 1784)).Should().BeEquivalentTo((1622.0, 1784.0, new[] { 1135.0 }));
    }
}
