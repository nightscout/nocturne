using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;

namespace Nocturne.API.Tests.GoldenFiles;

/// <summary>
/// The rule <see cref="Nocturne.Core.Contracts.Entries.IEntryStore.CheckDuplicateAsync"/> defines,
/// through the v1 and v3 create endpoints.
/// </summary>
[Trait("Category", "Unit")]
public class EntryUploadDuplicateRuleTests : GoldenFileTestBase
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const long StoredMills = 1727690400000L;
    private const string Device = "xDrip-DexcomG6";

    public EntryUploadDuplicateRuleTests(GoldenFileWebAppFactory factory) : base(factory) { }

    [Theory]
    [InlineData(60_000L)]
    [InlineData(300_000L)]
    [InlineData(-300_000L)]
    [InlineData(1L)]
    public async Task V1_EqualValueAtAnotherTimestamp_IsStored(long offsetMills)
    {
        await SeedStoredSgv(120, Device);
        var mills = StoredMills + offsetMills;

        var response = await PostJsonAsync("/api/v1/entries", new[] { Sgv(120, mills, Device) });

        response.IsSuccessStatusCode.Should().BeTrue();
        (await EchoedDates(response)).Should().Equal(mills);
        SensorGlucoseRows().Should().Be(2);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(125)]
    public async Task V1_SameDeviceAndTimestamp_IsADuplicateAndEchoesTheStoredRow(double sgv)
    {
        await SeedStoredSgv(120, Device);

        var response = await PostJsonAsync("/api/v1/entries", new[] { Sgv(sgv, StoredMills, Device) });

        response.IsSuccessStatusCode.Should().BeTrue();
        var echoed = (await ReadArray(response)).Single();
        echoed.GetProperty("date").GetInt64().Should().Be(StoredMills);
        echoed.GetProperty("sgv").GetDouble().Should().Be(120);
        SensorGlucoseRows().Should().Be(1);
    }

    [Fact]
    public async Task V1_OtherDeviceAtTheSameTimestamp_IsStored()
    {
        await SeedStoredSgv(120, Device);

        var response = await PostJsonAsync("/api/v1/entries", new[] { Sgv(120, StoredMills, "Juggluco") });

        response.IsSuccessStatusCode.Should().BeTrue();
        SensorGlucoseRows().Should().Be(2);
    }

    [Fact]
    public async Task V1_EqualMeterReadingAMinuteLater_IsStored()
    {
        await SeedMeterGlucose(new MeterGlucoseEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenantId,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(StoredMills).UtcDateTime,
            Mgdl = 150,
            Device = Device,
        });

        var response = await PostJsonAsync("/api/v1/entries", new[]
        {
            new { type = "mbg", mbg = 150, date = StoredMills + 60_000, device = Device },
        });

        response.IsSuccessStatusCode.Should().BeTrue();
        Count("meter_glucose").Should().Be(2);
    }

    [Fact]
    public async Task V3_EqualValueAMinuteLater_IsCreated()
    {
        await SeedStoredSgv(120, Device);

        var response = await PostJsonAsync("/api/v3/entries", Sgv(120, StoredMills + 60_000, Device));

        response.IsSuccessStatusCode.Should().BeTrue();
        SensorGlucoseRows().Should().Be(2);
    }

    [Fact]
    public async Task V3_SameDeviceAndTimestamp_AnswersDeduplication()
    {
        await SeedStoredSgv(120, Device);

        var response = await PostJsonAsync("/api/v3/entries", Sgv(120, StoredMills, Device));

        ((int)response.StatusCode).Should().Be(200);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("isDeduplication").GetBoolean().Should().BeTrue();
        SensorGlucoseRows().Should().Be(1);
    }

    private static object Sgv(double sgv, long mills, string device) =>
        new { type = "sgv", sgv, date = mills, device, direction = "Flat" };

    private Task SeedStoredSgv(double mgdl, string device) =>
        SeedSensorGlucose(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenantId,
            LegacyId = "aaaaaaaaaaaaaaaaaaaaa001",
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(StoredMills).UtcDateTime,
            Mgdl = mgdl,
            Direction = "Flat",
            Device = device,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });

    private static async Task<List<JsonElement>> ReadArray(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static async Task<List<long>> EchoedDates(HttpResponseMessage response) =>
        (await ReadArray(response)).Select(e => e.GetProperty("date").GetInt64()).ToList();

    private long SensorGlucoseRows() => Count("sensor_glucose");

    private long Count(string table)
    {
        using var cmd = Factory.Connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
