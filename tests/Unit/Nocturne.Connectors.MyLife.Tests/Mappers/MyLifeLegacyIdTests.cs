using FluentAssertions;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Models;
using Xunit;

namespace Nocturne.Connectors.MyLife.Tests.Mappers;

/// <summary>
/// The stored legacy id of every MyLife record type, as exact strings.
/// Why the keys must not change: <see cref="Nocturne.Connectors.Core.Utilities.ConnectorLegacyIds"/>.
/// </summary>
public class MyLifeLegacyIdTests
{
    private const long At = 17_840_000_000_000_000L;

    private static MyLifeEvent Event(MyLifeEventType type, string? value = null, string? info = null) => new()
    {
        EventTypeId = type,
        EventDateTime = At,
        Value = value,
        InformationFromDevice = info,
        PatientId = "patient",
        DeviceId = "device",
        CRC32Checksum = 4242,
    };

    [Fact]
    public void SensorGlucose()
    {
        new MyLifeEventProcessor().MapSensorGlucose([Event(MyLifeEventType.Glucose, "120")])
            .Single().LegacyId.Should().Be("mylife_entry_3d58e7b1b128076ba0a7ed8defd20de91b200504");
    }

    [Fact]
    public void Treatment()
    {
        var result = new MyLifeEventProcessor().MapRecords(
            [Event(MyLifeEventType.BolusNormal, "2", """{"AmountOfBolus":2}""")], false, false, 0);

        result.Boluses.Single().LegacyId.Should().Be("mylife_treatment_480c028120260e10475225aa7f0cbc092c35353a");
    }

    [Fact]
    public void SiteChange()
    {
        var result = new MyLifeEventProcessor().MapRecords(
            [Event(MyLifeEventType.NeedlePriming)], false, false, 0);

        result.DeviceEvents.Select(e => e.LegacyId).Should().Equal(
            "mylife_treatment_acd4ece366be2da0e3c3a018f484aafd24553734",
            "mylife_treatment_acd4ece366be2da0e3c3a018f484aafd24553734-sitechange");
    }

    [Fact]
    public void ProfileActivation()
    {
        var readout = new MyLifePumpSettingsReadout
        {
            Id = "r1", UploadDateTime = At, ActiveBasalProgramName = "Morning", DeviceSerialNumber = "SN-1",
        };

        MyLifePumpSettingsMapper.MapToProfileActivations([readout])
            .Single().SyncIdentifier.Should().Be("mylife_active_profile_SN-1_1784000000000");
    }

    [Fact]
    public void TempBasal()
    {
        MyLifeStateSpanMapper.MapTempBasals([Event(MyLifeEventType.BasalRate, info: """{"BasalRate": 1.2}""")], false, 0)
            .Single().LegacyId.Should().Be("97c1d8dd72c8ede45cde2b15cd66a49f14c6853d");
    }
}
