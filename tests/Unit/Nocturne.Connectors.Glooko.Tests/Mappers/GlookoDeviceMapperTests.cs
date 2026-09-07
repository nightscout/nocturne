using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Verifies the SSV2 pumps / cgm_devices → PatientDevice mapping: pump vs CGM categorisation, the
/// preference of the per-feed properties model over the record-level model, serial/guid fallback,
/// soft-deleted records being skipped, and a stable (deterministic, guid-keyed) Id for upserts.
/// </summary>
public class GlookoDeviceMapperTests
{
    private const string ConnectorSource = "glooko_test";

    private static GlookoDeviceMapper Mapper(double offset = 0)
    {
        var config = new GlookoConnectorConfiguration { TimezoneOffset = offset };
        var timeMapper = new GlookoTimeMapper(config, NullLogger.Instance);
        return new GlookoDeviceMapper(ConnectorSource, timeMapper, NullLogger.Instance);
    }

    private static GlookoSsv2Device Pump(
        string? guid = "pump-guid",
        string? serial = "CamAPS mylife YpsoPump",
        string? brand = "CamDiab",
        string? model = "CamAPS FX",
        string? pumpModel = "mylife YpsoPump",
        bool activelyUploaded = true,
        bool softDeleted = false) =>
        new()
        {
            Guid = guid,
            SerialNumber = serial,
            Brand = brand,
            Model = model,
            Properties = pumpModel is null ? null : new GlookoSsv2DeviceProperties { PumpModel = pumpModel },
            LastSyncTimestamp = "2026-06-26T21:14:13.432Z",
            ActivelyUploaded = activelyUploaded,
            SoftDeleted = softDeleted,
        };

    private static GlookoSsv2Device Cgm(
        string? guid = "cgm-guid",
        string? serial = "CamAPS Dexcom G6",
        string? cgmModel = "Dexcom G6",
        bool softDeleted = false) =>
        new()
        {
            Guid = guid,
            SerialNumber = serial,
            Brand = "CamDiab",
            Model = "CamAPS FX",
            Properties = cgmModel is null ? null : new GlookoSsv2DeviceProperties { CgmModel = cgmModel },
            LastSyncTimestamp = "2026-04-18T19:39:46.058Z",
            SoftDeleted = softDeleted,
        };

    [Fact]
    public void PumpMapsToInsulinPumpWithPropertiesModelPreferred()
    {
        var result = Mapper().TransformPumpsToPatientDevices([Pump()]).Single();

        result.DeviceCategory.Should().Be(DeviceCategory.InsulinPump);
        result.Manufacturer.Should().Be("CamDiab");
        // properties.pumpModel ("mylife YpsoPump") is preferred over record-level model ("CamAPS FX").
        result.Model.Should().Be("mylife YpsoPump");
        result.SerialNumber.Should().Be("CamAPS mylife YpsoPump");
        result.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void CgmMapsToCgmCategoryWithPropertiesModelPreferred()
    {
        var result = Mapper().TransformCgmDevicesToPatientDevices([Cgm()]).Single();

        result.DeviceCategory.Should().Be(DeviceCategory.CGM);
        result.Model.Should().Be("Dexcom G6");
        result.SerialNumber.Should().Be("CamAPS Dexcom G6");
    }

    [Fact]
    public void FallsBackToRecordModelWhenPropertiesModelMissing()
    {
        var result = Mapper().TransformPumpsToPatientDevices([Pump(pumpModel: null)]).Single();

        result.Model.Should().Be("CamAPS FX");
    }

    [Fact]
    public void FallsBackToGuidForSerialWhenSerialMissing()
    {
        var result = Mapper().TransformPumpsToPatientDevices([Pump(serial: null, guid: "abc-123")]).Single();

        result.SerialNumber.Should().Be("abc-123");
    }

    [Fact]
    public void SkipsSoftDeletedDevices()
    {
        var input = new[]
        {
            Pump(guid: "keep"),
            Pump(guid: "deleted", softDeleted: true),
        };

        var result = Mapper().TransformPumpsToPatientDevices(input);

        result.Should().HaveCount(1);
        result.Single().Id.Should().Be(DeriveId(DeviceCategory.InsulinPump, "keep"));
    }

    [Fact]
    public void SkipsDevicesWithNeitherGuidNorSerial()
    {
        var result = Mapper().TransformPumpsToPatientDevices([Pump(guid: null, serial: null)]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void IdIsDeterministicAndGuidKeyed()
    {
        var first = Mapper().TransformPumpsToPatientDevices([Pump(guid: "stable")]).Single();
        var second = Mapper().TransformPumpsToPatientDevices([Pump(guid: "stable")]).Single();

        first.Id.Should().Be(second.Id);
        first.Id.Should().Be(DeriveId(DeviceCategory.InsulinPump, "stable"));
    }

    [Fact]
    public void SameKeyDifferentCategoryYieldsDifferentIds()
    {
        var pump = Mapper().TransformPumpsToPatientDevices([Pump(guid: "shared")]).Single();
        var cgm = Mapper().TransformCgmDevicesToPatientDevices([Cgm(guid: "shared")]).Single();

        pump.Id.Should().NotBe(cgm.Id);
    }

    [Fact]
    public void NullInputYieldsEmptyList()
    {
        Mapper().TransformPumpsToPatientDevices(null).Should().BeEmpty();
        Mapper().TransformCgmDevicesToPatientDevices(null).Should().BeEmpty();
    }

    // Mirrors the mapper's deterministic UUIDv5 derivation so the test asserts the exact contract.
    private static Guid DeriveId(DeviceCategory category, string key)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"glooko-patient-device:{category}:{key}"));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}
