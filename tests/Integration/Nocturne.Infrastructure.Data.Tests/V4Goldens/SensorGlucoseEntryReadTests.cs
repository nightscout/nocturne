using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Projections;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class SensorGlucoseEntryReadTests(V4GoldenFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialReadPreservesLegacyJsonCanonicalSelectionAndPaging(bool descending)
    {
        var tenant = Guid.NewGuid();
        using var scope = await fixture.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var devices = scope.ServiceProvider.GetRequiredService<IPatientDeviceRepository>();
        var primary = await devices.CreateAsync(new PatientDevice
        {
            DeviceCategory = DeviceCategory.CGM, Manufacturer = "Dexcom", Model = "G7", Rank = 1,
        }, WriteOrigin.Backfill);
        var start = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await repo.BulkCreateAsync(Enumerable.Range(0, 12).Select(i => new SensorGlucose
        {
            Timestamp = start.AddMinutes(i / 2 * 5), Mgdl = 100 + i * 10,
            PatientDeviceId = i % 2 == 0 ? primary.Id : null,
            Device = i % 2 == 0 ? "a" : "b", DataSource = "entry-projection-test",
            LegacyId = i % 3 == 0 ? $"legacy-{i}" : null,
            App = i % 2 == 0 ? "test-app" : null, UtcOffset = i % 2 == 0 ? 600 : null,
            Direction = i % 2 == 0 ? GlucoseDirection.SingleUp : null,
            TrendRate = i % 2 == 0 ? 1.5 : null, Noise = i % 2 == 0 ? 2 : null,
            Filtered = i % 2 == 0 ? 12345 : null, Unfiltered = i % 2 == 0 ? 23456 : null,
            Delta = i % 2 == 0 ? -1.5 : null,
            AdditionalProperties = new() { ["ignored"] = "legacy projection does not read this" },
            SmoothedMgdl = 101, UnsmoothedMgdl = 99,
        }), WriteOrigin.Backfill);

        var full = (await repo.GetAsync(start, start.AddMinutes(25), null, null, 100, descending: descending)).ToList();
        var slim = (await repo.GetForEntriesAsync(start, start.AddMinutes(25), null, null, 100, descending: descending)).ToList();
        slim.Should().HaveCount(12);
        LegacyJson(slim).Should().Be(LegacyJson(full));
        LegacyJson(CanonicalGlucoseStream.Select(slim, [primary])).Should()
            .Be(LegacyJson(CanonicalGlucoseStream.Select(full, [primary])));
        slim.Should().OnlyContain(r => r.AdditionalProperties == null && r.SmoothedMgdl == null);

        var expected = await repo.GetAsync(start.AddMinutes(5), start.AddMinutes(20), "a", "entry-projection-test",
            2, 1, descending, nativeOnly: true, patientDeviceId: primary.Id);
        var actual = await repo.GetForEntriesAsync(start.AddMinutes(5), start.AddMinutes(20), "a", "entry-projection-test",
            2, 1, descending, nativeOnly: true, patientDeviceId: primary.Id);
        LegacyJson(actual).Should().Be(LegacyJson(expected));
        actual.Should().NotBeEmpty();

        var cursor = full[3];
        expected = await repo.GetAsync(start, null, null, null, 3, 99, descending,
            afterTimestamp: cursor.Timestamp, afterId: cursor.Id);
        actual = await repo.GetForEntriesAsync(start, null, null, null, 3, 99, descending,
            afterTimestamp: cursor.Timestamp, afterId: cursor.Id);
        LegacyJson(actual).Should().Be(LegacyJson(expected));
    }

    [Fact]
    public async Task PartialReadKeepsDedupSoftDeleteAndTenantIsolation()
    {
        var otherTenant = Guid.NewGuid();
        using (var otherScope = await fixture.BeginTenantScopeAsync(otherTenant))
            await otherScope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>().CreateAsync(
                new SensorGlucose { Timestamp = DateTime.UtcNow, Mgdl = 99 }, WriteOrigin.Backfill);

        var tenant = Guid.NewGuid();
        using var scope = await fixture.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var start = DateTime.UtcNow;
        await repo.BulkCreateAsync(new[]
        {
            new SensorGlucose { Timestamp = start, Mgdl = 120, DataSource = "dexcom" },
            new SensorGlucose { Timestamp = start.AddSeconds(10), Mgdl = 120.5, DataSource = "libre" },
            new SensorGlucose { Timestamp = start.AddMinutes(5), Mgdl = 180, LegacyId = "deleted" },
        }, WriteOrigin.Backfill);
        await repo.DeleteByLegacyIdAsync("deleted", WriteOrigin.Backfill);
        (await fixture.QueryAsync(tenant, ctx => ctx.LinkedRecords.CountAsync(r => !r.IsPrimary))).Should().Be(1);
        var full = await repo.GetAsync(null, null, null, null, 100);
        var slim = (await repo.GetForEntriesAsync(null, null, null, null, 100)).ToList();
        slim.Should().ContainSingle();
        LegacyJson(slim).Should().Be(LegacyJson(full));
    }

    private static string LegacyJson(IEnumerable<SensorGlucose> readings) =>
        JsonSerializer.Serialize(readings.Select(EntryProjection.FromSensorGlucose));
}
