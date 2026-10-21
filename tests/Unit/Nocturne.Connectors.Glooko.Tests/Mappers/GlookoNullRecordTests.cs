using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// A <c>null</c> in a Glooko array deserializes to a null element. Each mapper drops it before mapping,
/// because dereferencing it raises a <see cref="NullReferenceException"/>, which is left to fail the
/// run, and the sibling records must still map.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoNullRecordTests
{
    private const string Source = "glooko-connector";

    private readonly GlookoTimeMapper _timeMapper =
        new(new GlookoConnectorConfiguration(), NullLogger.Instance);

    [Fact]
    public void MapBatchData_NullNormalBolus_MapsTheOthers()
    {
        var batch = Deserialize<GlookoBatchData>(
            """{"normalBoluses":[null,{"timestamp":"2026-06-20T12:00:00.000Z","insulinDelivered":2.5}]}""");

        var (boluses, _, _) = Treatments().MapBatchData(batch);

        boluses.Should().ContainSingle().Which.Insulin.Should().Be(2.5);
    }

    [Fact]
    public void TransformBatchDataToSensorGlucose_NullReading_MapsTheOthers()
    {
        var batch = Deserialize<GlookoBatchData>(
            """{"readings":[{"display_time":"2026-06-20T12:00:00.000Z","bg_value":12000},null]}""");

        var readings = new GlookoSensorGlucoseMapper(
                new GlookoConnectorConfiguration(), Source, _timeMapper, NullLogger.Instance)
            .TransformBatchDataToSensorGlucose(batch);

        readings.Should().ContainSingle().Which.Mgdl.Should().Be(120);
    }

    [Fact]
    public void MapSsv2CarbsEvents_NullEvent_MapsTheOthers()
    {
        var events = Deserialize<GlookoSsv2CarbsEvent[]>(
            """[null,{"timestamp":"2026-06-20T12:30:00.000Z","carbs":45,"guid":"c-9"}]""");

        var carbs = Treatments().MapSsv2CarbsEvents(events);

        carbs.Should().ContainSingle().Which.Carbs.Should().Be(45);
    }

    [Fact]
    public void MapV3HistoryMealsToConnectorEntries_NullHistoryEntryAndFood_MapsTheOthers()
    {
        var histories = Deserialize<GlookoV3HistoriesResponse>(
            """
            {"histories":[null,{"type":"meals","guid":"meal-1","item":{
              "guid":"meal-1","timestamp":"2026-01-10T08:00:00Z","type":"breakfast","carbs":30.0,
              "foods":[null,{"guid":"food-1","name":"Toast","carbs":30.0}]}}]}
            """);

        var entries = Treatments().MapV3HistoryMealsToConnectorEntries(histories);

        entries.Should().ContainSingle();
    }

    [Fact]
    public void TransformV2ToTempBasals_NullTempBasal_MapsTheOthers()
    {
        var batch = Deserialize<GlookoBatchData>(
            """{"temporaryBasals":[null,{"timestamp":"2026-06-20T12:00:00.000Z","duration":1800,"rate":0.5}]}""");

        var tempBasals = new GlookoTempBasalMapper(Source, _timeMapper, NullLogger.Instance)
            .TransformV2ToTempBasals(batch);

        tempBasals.Should().ContainSingle().Which.Rate.Should().Be(0.5);
    }

    [Fact]
    public void TransformDeviceSettingsToProfiles_NullSnapshotAndBasalProfile_MapsTheOthers()
    {
        var settings = Deserialize<GlookoV3DeviceSettingsResponse>(
            """
            {"deviceSettings":{"pumps":{"pump-1":{
              "2026-01-09T00:00:00Z":null,
              "2026-01-10T00:00:00Z":{
                "basalSettings":{"activeBasalProgram":"Default"},
                "pumpProfilesBasal":[null,{"segments":{"profileName":"Default","current":true,
                  "data":[null,{"segmentStart":0.0,"duration":24.0,"value":0.8}]}}]}}}}}
            """);

        var profiles = new GlookoProfileMapper(Source, NullLogger.Instance).TransformDeviceSettingsToProfiles(settings);

        profiles.Should().ContainSingle().Which.Store["Default"].Basal.Should().ContainSingle();
    }

    [Fact]
    public void DeviceSettingsMappers_NullDeviceMap_MapTheOtherDevices()
    {
        var settings = Deserialize<GlookoV3DeviceSettingsResponse>(
            """
            {"deviceSettings":{"pumps":{"pump-0":null,"pump-1":{"2026-01-10T00:00:00Z":{
              "basalSettings":{"activeBasalProgram":"Default"},
              "pumpProfilesBasal":[{"segments":{"profileName":"Default","current":true,
                "data":[{"segmentStart":0.0,"duration":24.0,"value":0.8}]}}]}}}}}
            """);
        var mapper = new GlookoProfileMapper(Source, NullLogger.Instance);

        mapper.TransformDeviceSettingsToProfiles(settings).Should().ContainSingle();
        mapper.TransformDeviceSettingsToProfileActivations(settings).Should().ContainSingle();
    }

    private GlookoV4TreatmentMapper Treatments() => new(Source, _timeMapper, NullLogger.Instance);

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json)!;
}
