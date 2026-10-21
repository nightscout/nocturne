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
    public void MmolLabelledMgdlTargets_AreNotConvertedAgain(string units)
    {
        var (low, high, _) = Read(Json(units, 3.5, 90, 99));

        (low, high).Should().Be((90.0, 99.0));
    }

    [Fact]
    public void TargetsAlreadyConvertedOnceMore_AreUndoneToTheirOriginalValues()
    {
        var (low, high, _) = Read(Json("mmol", 3.5, 1622, 1784));

        (low, high).Should().Be((90.0, 99.0));
    }

    [Fact]
    public void ReSendingTheSameSwitch_BuildsTheSameSnapshot()
    {
        var json = Json("mmol", 3.5, 90, 99);

        Read(json).Should().BeEquivalentTo(Read(json));
    }

    [Theory]
    [InlineData(5.0, 90.0)]
    [InlineData(23.0, 414.0)]
    [InlineData(25.0, 450.0)]
    [InlineData(63.0, 1135.0)]
    public void TheIsf_IsConvertedAsUploaded_WhateverTheTargets(double isfMmol, double isfMgdl)
    {
        Read(Json("mmol", isfMmol, 90, 99)).Isf.Should().Equal(isfMgdl);
        Read(Json("mmol", isfMmol, 5.0, 5.5)).Isf.Should().Equal(isfMgdl);
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
