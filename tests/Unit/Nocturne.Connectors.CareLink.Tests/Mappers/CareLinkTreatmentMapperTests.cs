using FluentAssertions;
using Nocturne.Connectors.CareLink.Mappers;
using Nocturne.Connectors.CareLink.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Mappers;

public class CareLinkTreatmentMapperTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    // pumpOffset 0 → marker dateTime is already UTC, keeping the arithmetic obvious.
    private const double NoOffset = 0;

    private static CareLinkData Data(params CareLinkMarker[] markers) =>
        new() { MedicalDeviceFamily = "780G", Markers = markers.ToList() };

    [Fact]
    public void MapBoluses_MapsInsulinMarker_WithTotalAmountsAndDevice()
    {
        var data = Data(new CareLinkMarker
        {
            Type = "INSULIN",
            DateTime = "2026-06-10T11:59:25",
            Id = 120,
            BolusType = "FAST",
            ActivationType = "MANUAL",
            ProgrammedFastAmount = 0.8,
            DeliveredFastAmount = 0.7,
            ProgrammedExtendedAmount = 0.2,
            DeliveredExtendedAmount = 0.1,
            Index = 0,
        });

        var boluses = CareLinkTreatmentMapper.MapBoluses(data, NoOffset, Now);

        boluses.Should().HaveCount(1);
        var bolus = boluses[0];
        bolus.Programmed.Should().Be(1.0);
        bolus.Delivered.Should().BeApproximately(0.8, 1e-9);
        bolus.Insulin.Should().BeApproximately(0.8, 1e-9);
        bolus.BolusType.Should().Be(BolusType.Normal);
        bolus.Automatic.Should().BeFalse();
        bolus.Kind.Should().Be(BolusKind.Manual);
        bolus.Duration.Should().BeNull();
        bolus.Device.Should().Be("CareLink 780G");
        bolus.DataSource.Should().Be(DataSources.CareLinkConnector);
        bolus.Timestamp.Should().Be(new DateTime(2026, 6, 10, 11, 59, 25, DateTimeKind.Utc));
    }

    [Fact]
    public void MapBoluses_SetsAutomaticAndAlgorithmKind_ForAutocorrection()
    {
        var data = Data(new CareLinkMarker
        {
            Type = "INSULIN", DateTime = "2026-06-10T11:59:25", Id = 1,
            BolusType = "FAST", ActivationType = "AUTOCORRECTION",
            ProgrammedFastAmount = 0.3, DeliveredFastAmount = 0.3,
        });

        var bolus = CareLinkTreatmentMapper.MapBoluses(data, NoOffset, Now).Single();

        bolus.Automatic.Should().BeTrue();
        bolus.Kind.Should().Be(BolusKind.Algorithm);
    }

    [Theory]
    [InlineData("SQUARE", BolusType.Square)]
    [InlineData("DUAL", BolusType.Dual)]
    public void MapBoluses_SetsDuration_OnlyForExtendedBolusTypes(string bolusType, BolusType expected)
    {
        var data = Data(new CareLinkMarker
        {
            Type = "INSULIN", DateTime = "2026-06-10T11:59:25", Id = 5,
            BolusType = bolusType, ActivationType = "MANUAL",
            ProgrammedFastAmount = 1.0, DeliveredFastAmount = 1.0,
            ProgrammedDuration = 120,
        });

        var bolus = CareLinkTreatmentMapper.MapBoluses(data, NoOffset, Now).Single();

        bolus.BolusType.Should().Be(expected);
        bolus.Duration.Should().Be(120);
    }

    [Fact]
    public void MapBoluses_SkipsMarkers_WithNoProgrammedOrDeliveredInsulin()
    {
        var data = Data(new CareLinkMarker
        {
            Type = "INSULIN", DateTime = "2026-06-10T11:59:25", Id = 9,
            ProgrammedFastAmount = 0, DeliveredFastAmount = 0,
        });

        CareLinkTreatmentMapper.MapBoluses(data, NoOffset, Now).Should().BeEmpty();
    }

    [Fact]
    public void MapCarbIntakes_MapsMealMarker()
    {
        var data = Data(new CareLinkMarker
        {
            Type = "MEAL", DateTime = "2026-06-10T12:19:35", Amount = 40, Index = 5,
        });

        var carbs = CareLinkTreatmentMapper.MapCarbIntakes(data, NoOffset, Now);

        carbs.Should().HaveCount(1);
        carbs[0].Carbs.Should().Be(40);
        carbs[0].AbsorptionTime.Should().BeNull();
        carbs[0].DataSource.Should().Be(DataSources.CareLinkConnector);
        carbs[0].Timestamp.Should().Be(new DateTime(2026, 6, 10, 12, 19, 35, DateTimeKind.Utc));
    }

    [Fact]
    public void MapTempBasals_ComputesHourlyRateAndAlgorithmOrigin()
    {
        // Two auto-basal markers 5 minutes apart: 0.275 U over 5 min → 3.3 U/hr.
        var data = Data(
            new CareLinkMarker { Type = "AUTO_BASAL_DELIVERY", DateTime = "2026-06-10T11:54:39", Id = 119, BolusAmount = 0.275, Index = 0 },
            new CareLinkMarker { Type = "AUTO_BASAL_DELIVERY", DateTime = "2026-06-10T11:59:39", Id = 120, BolusAmount = 0.1, Index = 1 });

        var basals = CareLinkTreatmentMapper.MapTempBasals(data, NoOffset, Now);

        basals.Should().HaveCount(2);
        var first = basals[0];
        first.Origin.Should().Be(TempBasalOrigin.Algorithm);
        first.Rate.Should().BeApproximately(3.3, 1e-6);
        first.StartTimestamp.Should().Be(new DateTime(2026, 6, 10, 11, 54, 39, DateTimeKind.Utc));
        first.EndTimestamp.Should().Be(new DateTime(2026, 6, 10, 11, 59, 39, DateTimeKind.Utc));
        first.DataSource.Should().Be(DataSources.CareLinkConnector);

        // Last micro-bolus has no following marker → bounded by the 5-minute fallback.
        basals[1].EndTimestamp.Should().Be(basals[1].StartTimestamp.AddMinutes(5));
    }

    [Fact]
    public void Boluses_SyncIdentifier_IsIndependentOfArrayIndex()
    {
        CareLinkMarker MakeAt(int index) => new()
        {
            Type = "INSULIN", DateTime = "2026-06-10T11:59:25", Id = 120,
            BolusType = "FAST", ActivationType = "MANUAL",
            ProgrammedFastAmount = 0.8, DeliveredFastAmount = 0.8,
            Index = index,
        };

        var atZero = CareLinkTreatmentMapper.MapBoluses(Data(MakeAt(0)), NoOffset, Now).Single();
        var atNine = CareLinkTreatmentMapper.MapBoluses(Data(MakeAt(9)), NoOffset, Now).Single();

        atZero.SyncIdentifier.Should().Be(atNine.SyncIdentifier);
        atZero.SyncIdentifier.Should().NotContain("0:0").And.StartWith("carelink:insulin:120:");
    }

    [Fact]
    public void TempBasals_AreKeyedOnTheirStart_WhateverTheMarkerIdIndexOrNextMarker()
    {
        var alone = Data(
            new CareLinkMarker { Type = "AUTO_BASAL_DELIVERY", DateTime = "2026-06-10T11:54:39", Id = 119, BolusAmount = 0.275, Index = 0 });
        var followed = Data(
            new CareLinkMarker { Type = "AUTO_BASAL_DELIVERY", DateTime = "2026-06-10T11:51:00", Id = 7, BolusAmount = 0.2, Index = 0 },
            new CareLinkMarker { Type = "AUTO_BASAL_DELIVERY", DateTime = "2026-06-10T11:54:39", Id = 1, BolusAmount = 0.3, Index = 1 },
            new CareLinkMarker { Type = "AUTO_BASAL_DELIVERY", DateTime = "2026-06-10T11:57:00", Id = 2, BolusAmount = 0.1, Index = 2 });

        var once = CareLinkTreatmentMapper.MapTempBasals(alone, NoOffset, Now).Single();
        var again = CareLinkTreatmentMapper.MapTempBasals(followed, NoOffset, Now)[1];

        once.LegacyId.Should().Be("carelink_autobasal_1781092479000");
        once.SyncIdentifier.Should().Be(once.LegacyId);
        again.LegacyId.Should().Be(once.LegacyId);
        again.SyncIdentifier.Should().Be(once.SyncIdentifier);
    }

    [Fact]
    public void TempBasals_KeyOnTheUtcStart_AfterThePumpOffset()
    {
        var data = Data(
            new CareLinkMarker { Type = "AUTO_BASAL_DELIVERY", DateTime = "2026-06-10T21:54:39", BolusAmount = 0.275 });

        var basal = CareLinkTreatmentMapper.MapTempBasals(data, TimeSpan.FromHours(10).TotalMilliseconds, Now).Single();

        basal.LegacyId.Should().Be("carelink_autobasal_1781092479000");
    }

    [Fact]
    public void CarbIntakes_SyncIdentifier_IsIndependentOfArrayIndex()
    {
        CareLinkMarker MakeAt(int index) => new()
        {
            Type = "MEAL", DateTime = "2026-06-10T12:19:35", Amount = 40, Index = index,
        };

        var atZero = CareLinkTreatmentMapper.MapCarbIntakes(Data(MakeAt(0)), NoOffset, Now).Single();
        var atSeven = CareLinkTreatmentMapper.MapCarbIntakes(Data(MakeAt(7)), NoOffset, Now).Single();

        atZero.SyncIdentifier.Should().Be(atSeven.SyncIdentifier);
        atZero.SyncIdentifier.Should().StartWith("carelink:meal:");
    }

    [Fact]
    public void Mappers_ReturnEmpty_WhenNoMarkers()
    {
        var empty = new CareLinkData();
        CareLinkTreatmentMapper.MapBoluses(empty, NoOffset, Now).Should().BeEmpty();
        CareLinkTreatmentMapper.MapCarbIntakes(empty, NoOffset, Now).Should().BeEmpty();
        CareLinkTreatmentMapper.MapTempBasals(empty, NoOffset, Now).Should().BeEmpty();
    }
}
